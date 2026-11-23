using System.Runtime.InteropServices;

namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// Native pw_stream_events vtable layout. Field order must match libpipewire's struct; the
/// function pointers are produced with <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StreamEvents
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
internal delegate void DestroyCallback(IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void StateChangedCallback(IntPtr data, int oldState, int state, IntPtr error);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ControlInfoCallback(IntPtr data, uint id, IntPtr control);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void IoChangedCallback(IntPtr data, uint id, IntPtr area, uint size);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ParamChangedCallback(IntPtr data, uint id, IntPtr param);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void BufferCallback(IntPtr data, IntPtr buffer);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ProcessCallback(IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void CommandCallback(IntPtr data, IntPtr command);
