using System.Globalization;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AiAssistant.Platform.Linux.PipeWire;

public sealed partial class PipeWireScreenCastStream : IDisposable
{
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
        if (!PipeWireFrameProcessor.TryParseNegotiatedFormat(
                param,
                out var format,
                out var width,
                out var height,
                out var error))
        {
            if (error is not null)
            {
                _lastError = error;
            }

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
        var now = _clock.UtcNow;
        if (!_frameProcessor.TryCreateFrame(
                bufferPointer,
                Volatile.Read(ref _negotiatedWidth),
                Volatile.Read(ref _negotiatedHeight),
                Volatile.Read(ref _negotiatedFormat),
                now,
                out var frame,
                out var error))
        {
            if (error is not null)
            {
                _lastError = error;
            }

            return;
        }

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
