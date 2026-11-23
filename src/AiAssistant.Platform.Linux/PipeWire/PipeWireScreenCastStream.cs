using System.Globalization;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AiAssistant.Platform.Linux.PipeWire;

public sealed record PipeWireFrame(int Width, int Height, byte[] Bgra32, DateTimeOffset CapturedAtUtc);

/// <summary>
/// Consumes one xdg-desktop-portal ScreenCast node through libpipewire: a private thread loop,
/// a PW_DIRECTION_INPUT stream, and a bounded latest-frame slot. Any failure degrades capture to
/// the portal screenshot fallback instead of failing the pipeline.
/// </summary>
public sealed class PipeWireScreenCastStream : IDisposable
{
    private static readonly object InitLock = new();
    private static bool _initialised;

    private readonly ILogger<PipeWireScreenCastStream> _logger;
    private readonly TimeSpan _minFrameInterval;
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
        TimeSpan? minFrameInterval = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _minFrameInterval = minFrameInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>True when the PipeWire client library can be loaded on this host.</summary>
    public static bool IsLibraryAvailable
    {
        get
        {
            try
            {
                return NativeLibrary.TryLoad(PipeWireNative.Library, out _);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
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

        if (!EnsureInitialised())
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

            var properties = BuildStreamProperties(targetSerial);
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

        if (latest is not null && TimeProvider.System.GetUtcNow() - latest.CapturedAtUtc <= maxFrameAge)
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

    private static bool EnsureInitialised()
    {
        lock (InitLock)
        {
            if (_initialised)
            {
                return true;
            }

            try
            {
                var argc = Marshal.AllocHGlobal(sizeof(int));
                var argv = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteInt32(argc, 0);
                    Marshal.WriteIntPtr(argv, IntPtr.Zero);
                    PipeWireNative.Init(argc, argv);
                }
                finally
                {
                    Marshal.FreeHGlobal(argc);
                    Marshal.FreeHGlobal(argv);
                }

                _initialised = true;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    private static IntPtr BuildStreamProperties(uint? targetSerial)
    {
        var properties = targetSerial.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"media.type=Video media.category=Capture media.role=Screen target.object={targetSerial.Value}")
            : "media.type=Video media.category=Capture media.role=Screen";
        return PipeWireNative.PropertiesNewString(properties);
    }

    private static StreamEvents CreateStreamEvents() => new()
    {
        Version = PipeWireNative.StreamEventsVersion,
        Destroy = Marshal.GetFunctionPointerForDelegate(OnDestroyCallback),
        StateChanged = Marshal.GetFunctionPointerForDelegate(OnStateChangedCallback),
        ControlInfo = Marshal.GetFunctionPointerForDelegate(OnControlInfoCallback),
        IoChanged = Marshal.GetFunctionPointerForDelegate(OnIoChangedCallback),
        ParamChanged = Marshal.GetFunctionPointerForDelegate(OnParamChangedCallback),
        AddBuffer = Marshal.GetFunctionPointerForDelegate(OnAddBufferCallback),
        RemoveBuffer = Marshal.GetFunctionPointerForDelegate(OnRemoveBufferCallback),
        Process = Marshal.GetFunctionPointerForDelegate(OnProcessCallback),
        Drained = Marshal.GetFunctionPointerForDelegate(OnDrainedCallback),
        Command = Marshal.GetFunctionPointerForDelegate(OnCommandCallback),
        TriggerDone = Marshal.GetFunctionPointerForDelegate(OnTriggerDoneCallback)
    };

    private static PipeWireScreenCastStream? FromData(IntPtr data) =>
        data == IntPtr.Zero ? null : GCHandle.FromIntPtr(data).Target as PipeWireScreenCastStream;

    private static void OnDestroy(IntPtr data) => FromData(data)?._logger.LogDebug("PipeWire stream destroyed.");

    private static void OnStateChanged(IntPtr data, int oldState, int state, IntPtr error)
    {
        var stream = FromData(data);
        if (stream is null)
        {
            return;
        }

        Volatile.Write(ref stream._streamState, state);
        if (state == PipeWireNative.StreamStateError)
        {
            stream._lastError = error == IntPtr.Zero ? "stream error" : Marshal.PtrToStringUTF8(error);
            stream._logger.LogWarning("PipeWire stream error: {ErrorType}.", stream._lastError);
            lock (stream._sync)
            {
                stream._waiter?.TrySetResult(null);
                stream._waiter = null;
            }
        }
    }

    private static void OnParamChanged(IntPtr data, uint id, IntPtr param)
    {
        var stream = FromData(data);
        if (stream is null || id != PipeWireNative.ParamFormat || param == IntPtr.Zero)
        {
            return;
        }

        try
        {
            stream.ApplyNegotiatedFormat(param);
        }
        catch (Exception ex)
        {
            stream._lastError = ex.GetType().Name;
            stream._logger.LogDebug("PipeWire format update skipped ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private void ApplyNegotiatedFormat(IntPtr param)
    {
        var size = Marshal.ReadInt32(param);
        if (size <= 0 || size > 8192)
        {
            return;
        }

        var bytes = new byte[8 + size];
        Marshal.Copy(param, bytes, 0, bytes.Length);
        if (!SpaPodCodec.TryParseVideoFormat(bytes, out var format, out var width, out var height))
        {
            _lastError = "unsupported negotiated video format";
            return;
        }

        Volatile.Write(ref _negotiatedFormat, format);
        Volatile.Write(ref _negotiatedWidth, width);
        Volatile.Write(ref _negotiatedHeight, height);
    }

    private static void OnProcess(IntPtr data)
    {
        // Never let managed exceptions cross the native callback boundary: the runtime aborts the process.
        try
        {
            var stream = FromData(data);
            if (stream is null || stream._stream == IntPtr.Zero)
            {
                return;
            }

            while (true)
            {
                var buffer = PipeWireNative.StreamDequeueBuffer(stream._stream);
                if (buffer == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    stream.ConsiderBuffer(buffer);
                }
                finally
                {
                    _ = PipeWireNative.StreamQueueBuffer(stream._stream, buffer);
                }
            }
        }
        catch (Exception ex)
        {
            FromData(data)?._logger.LogDebug("PipeWire buffer skipped ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private void ConsiderBuffer(IntPtr bufferPointer)
    {
        var now = TimeProvider.System.GetUtcNow();
        if (now - _lastCopiedAt < _minFrameInterval)
        {
            return;
        }

        var spaBufferPointer = Marshal.ReadIntPtr(bufferPointer);
        if (spaBufferPointer == IntPtr.Zero)
        {
            return;
        }

        var spaBuffer = Marshal.PtrToStructure<PipeWireNative.SpaBuffer>(spaBufferPointer);
        if (spaBuffer.DataCount == 0 || spaBuffer.Datas == IntPtr.Zero)
        {
            return;
        }

        // spa_buffer.datas points at the first spa_data struct (not an array of pointers).
        var spaData = Marshal.PtrToStructure<PipeWireNative.SpaData>(spaBuffer.Datas);
        if (spaData.Data == IntPtr.Zero || spaData.Chunk == IntPtr.Zero)
        {
            return;
        }

        var chunk = Marshal.PtrToStructure<PipeWireNative.SpaChunk>(spaData.Chunk);
        var width = Volatile.Read(ref _negotiatedWidth);
        var height = Volatile.Read(ref _negotiatedHeight);
        var format = Volatile.Read(ref _negotiatedFormat);
        if (chunk.Size == 0 || width <= 0 || height <= 0 || format == 0)
        {
            return;
        }

        var size = (int)Math.Min(chunk.Size, spaData.MaxSize == 0 ? chunk.Size : spaData.MaxSize);
        var source = new byte[size];
        Marshal.Copy(IntPtr.Add(spaData.Data, (int)chunk.Offset), source, 0, size);
        if (!PipeWireFrameConverter.TryConvertToBgra32(source, width, height, chunk.Stride, format, out var bgra) ||
            bgra is null)
        {
            _lastError = "unsupported pixel layout";
            return;
        }

        var frame = new PipeWireFrame(width, height, bgra, now);
        _lastCopiedAt = now;
        TaskCompletionSource<PipeWireFrame?>? waiter;
        lock (_sync)
        {
            _latest = frame;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult(frame);
    }

    private static void OnControlInfo(IntPtr data, uint id, IntPtr control)
    {
    }

    private static void OnIoChanged(IntPtr data, uint id, IntPtr area, uint size)
    {
    }

    private static void OnAddBuffer(IntPtr data, IntPtr buffer)
    {
    }

    private static void OnRemoveBuffer(IntPtr data, IntPtr buffer)
    {
    }

    private static void OnDrained(IntPtr data)
    {
    }

    private static void OnCommand(IntPtr data, IntPtr command)
    {
    }

    private static void OnTriggerDone(IntPtr data)
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamEvents
    {
        public uint Version;
        public IntPtr Destroy;
        public IntPtr StateChanged;
        public IntPtr ControlInfo;
        public IntPtr IoChanged;
        public IntPtr ParamChanged;
        public IntPtr AddBuffer;
        public IntPtr RemoveBuffer;
        public IntPtr Process;
        public IntPtr Drained;
        public IntPtr Command;
        public IntPtr TriggerDone;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyCallback(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateChangedCallback(IntPtr data, int oldState, int state, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ControlInfoCallback(IntPtr data, uint id, IntPtr control);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IoChangedCallback(IntPtr data, uint id, IntPtr area, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ParamChangedCallback(IntPtr data, uint id, IntPtr param);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BufferCallback(IntPtr data, IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProcessCallback(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CommandCallback(IntPtr data, IntPtr command);

    private static readonly DestroyCallback OnDestroyCallback = OnDestroy;
    private static readonly StateChangedCallback OnStateChangedCallback = OnStateChanged;
    private static readonly ControlInfoCallback OnControlInfoCallback = OnControlInfo;
    private static readonly IoChangedCallback OnIoChangedCallback = OnIoChanged;
    private static readonly ParamChangedCallback OnParamChangedCallback = OnParamChanged;
    private static readonly BufferCallback OnAddBufferCallback = OnAddBuffer;
    private static readonly BufferCallback OnRemoveBufferCallback = OnRemoveBuffer;
    private static readonly ProcessCallback OnProcessCallback = OnProcess;
    private static readonly ProcessCallback OnDrainedCallback = OnDrained;
    private static readonly CommandCallback OnCommandCallback = OnCommand;
    private static readonly ProcessCallback OnTriggerDoneCallback = OnTriggerDone;
}
