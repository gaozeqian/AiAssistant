using System.Globalization;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// Consumes one xdg-desktop-portal ScreenCast node through libpipewire: a private thread loop,
/// a PW_DIRECTION_INPUT stream, and a bounded latest-frame slot. Any failure degrades capture to
/// the portal screenshot fallback instead of failing the pipeline.
/// </summary>
public sealed partial class PipeWireScreenCastStream : IDisposable
{

    private readonly ILogger<PipeWireScreenCastStream> _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _minFrameInterval;
    private readonly PipeWireFrameProcessor _frameProcessor;
    private readonly Lock _sync = new();
    private readonly List<GCHandle> _pinnedHandles = [];

    private IntPtr _threadLoop;
    private IntPtr _eventsPointer;
    private IntPtr _hookPointer;
    private IntPtr _context;
    private IntPtr _core;
    private IntPtr _stream;
    private GCHandle _selfHandle;
    private PipeWireFrame? _latest;
    private TaskCompletionSource<PipeWireFrame?>? _waiter;
    private int _negotiatedFormat;
    private int _negotiatedWidth;
    private int _negotiatedHeight;
    private int _streamState = PipeWireNative.StreamStateUnconnected;
    private DateTimeOffset _lastCopiedAt = DateTimeOffset.MinValue;
    private string? _lastError;
    private bool _disposed;

    public PipeWireScreenCastStream(
        ILogger<PipeWireScreenCastStream> logger,
        TimeSpan? minFrameInterval = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clock = clock ?? new SystemClock();
        _minFrameInterval = minFrameInterval ?? TimeSpan.FromSeconds(1);
        _frameProcessor = new PipeWireFrameProcessor(_minFrameInterval);
    }

    public CapabilityState Capability
    {
        get
        {
            var state = Volatile.Read(ref _streamState);
            if (state == PipeWireNative.StreamStateError)
            {
                return CapabilityState.Degraded(
                    CapabilityKind.ScreenCapture,
                    $"PipeWire ScreenCast stream failed ({_lastError ?? "unknown error"})",
                    "Re-authorize the xdg-desktop-portal screen dialog; portal screenshots remain available.");
            }

            if (state == PipeWireNative.StreamStateStreaming)
            {
                var width = Volatile.Read(ref _negotiatedWidth);
                var height = Volatile.Read(ref _negotiatedHeight);
                return CapabilityState.Supported(
                    CapabilityKind.ScreenCapture,
                    $"Wayland PipeWire ScreenCast stream {width}x{height}");
            }

            return CapabilityState.Degraded(
                CapabilityKind.ScreenCapture,
                "Wayland PipeWire stream is idle; portal screenshots are used on demand",
                "首次采集请求时才会在 portal 授权后启动帧流。");
        }
    }

    /// <summary>
    /// Connects the stream to the portal node. The remote handle is consumed: PipeWire owns the fd
    /// afterwards, so the caller must not close it again.
    /// </summary>
    public bool TryStart(
        SafeFileHandle remote,
        uint? targetSerial,
        uint targetNodeId,
        int fallbackWidth,
        int fallbackHeight,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = null;
        if (remote.IsInvalid || remote.IsClosed)
        {
            error = "PipeWire remote fd is invalid";
            return false;
        }

        if (!PipeWireNativeSetup.EnsureInitialised())
        {
            error = "libpipewire is not available";
            return false;
        }

        var fd = (int)remote.DangerousGetHandle();
        remote.SetHandleAsInvalid();
        try
        {
            _threadLoop = PipeWireNative.ThreadLoopNew("aiassistant-screencast", IntPtr.Zero);
            if (_threadLoop == IntPtr.Zero)
            {
                throw new InvalidOperationException("pw_thread_loop_new failed");
            }

            var loop = PipeWireNative.ThreadLoopGetLoop(_threadLoop);
            _context = PipeWireNative.ContextNew(loop, IntPtr.Zero, 0);
            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException("pw_context_new failed");
            }

            _core = PipeWireNative.ContextConnectFd(_context, fd, IntPtr.Zero, 0);
            if (_core == IntPtr.Zero)
            {
                throw new InvalidOperationException("pw_context_connect_fd failed");
            }

            var properties = PipeWireNativeSetup.BuildStreamProperties(targetSerial);
            _stream = PipeWireNative.StreamNew(_core, "aiassistant-screencast", properties);
            if (_stream == IntPtr.Zero)
            {
                throw new InvalidOperationException("pw_stream_new failed");
            }

            var events = CreateStreamEvents();
            _eventsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<StreamEvents>());
            Marshal.StructureToPtr(events, _eventsPointer, fDeleteOld: false);
            _hookPointer = Marshal.AllocHGlobal(PipeWireNative.HookBytes);
            for (var i = 0; i < PipeWireNative.HookBytes; i++)
            {
                Marshal.WriteByte(_hookPointer, i, 0);
            }

            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            PipeWireNative.StreamAddListener(
                _stream,
                _hookPointer,
                _eventsPointer,
                GCHandle.ToIntPtr(_selfHandle));

            Volatile.Write(ref _negotiatedWidth, fallbackWidth);
            Volatile.Write(ref _negotiatedHeight, fallbackHeight);

            var formatRequest = SpaPodCodec.BuildVideoFormatRequest(
                [
                    PipeWireNative.VideoFormatBgra,
                    PipeWireNative.VideoFormatBgrx,
                    PipeWireNative.VideoFormatRgba,
                    PipeWireNative.VideoFormatRgbx
                ],
                // The portal-reported size can be smaller than the actual node size (HiDPI monitors);
                // keep a wide range so the screencast node's fixed format still matches.
                maxWidth: 8192,
                maxHeight: 8192);
            var podHandle = GCHandle.Alloc(formatRequest, GCHandleType.Pinned);
            _pinnedHandles.Add(podHandle);
            var parameterArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parameterArray, 0, podHandle.AddrOfPinnedObject());

            var targetId = targetSerial.HasValue ? PipeWireNative.IdAny : targetNodeId;
            var flags = PipeWireNative.StreamFlagAutoConnect | PipeWireNative.StreamFlagMapBuffers;
            var connectResult = PipeWireNative.StreamConnect(
                _stream,
                PipeWireNative.DirectionInput,
                targetId,
                flags,
                parameterArray,
                1);
            Marshal.FreeHGlobal(parameterArray);
            if (connectResult < 0)
            {
                throw new InvalidOperationException($"pw_stream_connect failed with {connectResult}");
            }

            var startResult = PipeWireNative.ThreadLoopStart(_threadLoop);
            if (startResult < 0)
            {
                throw new InvalidOperationException($"pw_thread_loop_start failed with {startResult}");
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            error = ex.Message;
            _lastError = ex.GetType().Name;
            _logger.LogWarning("PipeWire ScreenCast stream start failed with {ExceptionType}.", ex.GetType().Name);
            Stop();
            return false;
        }
    }

    /// <summary>Returns the freshest frame, waiting up to <paramref name="timeout"/> for the next one.</summary>
    public async Task<PipeWireFrame?> CaptureAsync(TimeSpan maxFrameAge, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PipeWireFrame? latest;
        lock (_sync)
        {
            latest = _latest;
        }

        if (latest is not null && _clock.UtcNow - latest.CapturedAtUtc <= maxFrameAge)
        {
            return latest;
        }

        TaskCompletionSource<PipeWireFrame?> waiter;
        lock (_sync)
        {
            _waiter ??= new TaskCompletionSource<PipeWireFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = _waiter;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await waiter.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (ReferenceEquals(_waiter, waiter))
                {
                    _waiter = null;
                }

                return _latest;
            }
        }
    }

    public void Stop()
    {
        var threadLoop = _threadLoop;
        var stream = _stream;
        var context = _context;
        _threadLoop = IntPtr.Zero;
        _stream = IntPtr.Zero;
        _context = IntPtr.Zero;
        _core = IntPtr.Zero;

        if (threadLoop != IntPtr.Zero)
        {
            try
            {
                PipeWireNative.ThreadLoopStop(threadLoop);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Library already unavailable; nothing left to stop.
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Linux.PipeWire.PipeWireScreenCastStream.Stop", ex);
            }
        }

        if (stream != IntPtr.Zero)
        {
            PipeWireNative.StreamDestroy(stream);
        }

        if (context != IntPtr.Zero)
        {
            PipeWireNative.ContextDestroy(context);
        }

        if (threadLoop != IntPtr.Zero)
        {
            PipeWireNative.ThreadLoopDestroy(threadLoop);
        }

        foreach (var handle in _pinnedHandles)
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        _pinnedHandles.Clear();
        if (_hookPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_hookPointer);
            _hookPointer = IntPtr.Zero;
        }

        if (_eventsPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_eventsPointer);
            _eventsPointer = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        Volatile.Write(ref _streamState, PipeWireNative.StreamStateUnconnected);
        lock (_sync)
        {
            _waiter?.TrySetResult(null);
            _waiter = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

}
