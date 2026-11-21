using System.Runtime.InteropServices;

// libpipewire expects UTF-8 across the ABI. Strings are marshalled explicitly as LPUTF8Str and
// .NET (Core) does not apply BestFitMapping, so CA2101 has no applicable risk here.
#pragma warning disable CA2101

namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// Minimal libpipewire client surface needed to consume one ScreenCast video stream.
/// Constants were verified against PipeWire 1.6.2 headers (spa/utils/type.h, spa/param/*.h).
/// </summary>
internal static class PipeWireNative
{
    public const string Library = "libpipewire-0.3.so.0";

    public const uint StreamEventsVersion = 2;
    public const int DirectionInput = 0;
    public const uint IdAny = 0xffffffff;
    public const uint StreamFlagAutoConnect = 1u << 0;
    public const uint StreamFlagMapBuffers = 1u << 2;

    public const int StreamStateError = -1;
    public const int StreamStateUnconnected = 0;
    public const int StreamStateConnecting = 1;
    public const int StreamStatePaused = 2;
    public const int StreamStateStreaming = 3;

    public const uint ParamFormat = 4;

    public const int SpaTypeNone = 1;
    public const int SpaTypeId = 3;
    public const int SpaTypeRectangle = 10;
    public const int SpaTypeObject = 15;
    public const int SpaTypeChoice = 19;

    public const uint SpaTypeObjectFormat = 0x40003;
    public const uint SpaParamEnumFormat = 3;

    public const uint FormatKeyMediaType = 1;
    public const uint FormatKeyMediaSubtype = 2;
    public const uint VideoKeyFormat = 0x20001;
    public const uint VideoKeySize = 0x20003;

    public const uint MediaTypeVideo = 2;
    public const uint MediaSubtypeRaw = 1;

    public const int VideoFormatRgbx = 7;
    public const int VideoFormatBgrx = 8;
    public const int VideoFormatXrgb = 9;
    public const int VideoFormatXbgr = 10;
    public const int VideoFormatRgba = 11;
    public const int VideoFormatBgra = 12;
    public const int VideoFormatArgb = 13;
    public const int VideoFormatAbgr = 14;

    public const int ChoiceRange = 1;
    public const int ChoiceEnum = 3;

    public const int HookBytes = 128;

    [DllImport(Library, EntryPoint = "pw_init")]
    internal static extern void Init(IntPtr argc, IntPtr argv);

    [DllImport(Library, EntryPoint = "pw_thread_loop_new", CharSet = CharSet.Unicode)]
    internal static extern IntPtr ThreadLoopNew(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr properties);

    [DllImport(Library, EntryPoint = "pw_thread_loop_get_loop")]
    internal static extern IntPtr ThreadLoopGetLoop(IntPtr threadLoop);

    [DllImport(Library, EntryPoint = "pw_thread_loop_start")]
    internal static extern int ThreadLoopStart(IntPtr threadLoop);

    [DllImport(Library, EntryPoint = "pw_thread_loop_stop")]
    internal static extern void ThreadLoopStop(IntPtr threadLoop);

    [DllImport(Library, EntryPoint = "pw_thread_loop_destroy")]
    internal static extern void ThreadLoopDestroy(IntPtr threadLoop);

    [DllImport(Library, EntryPoint = "pw_context_new")]
    internal static extern IntPtr ContextNew(IntPtr loop, IntPtr properties, nuint userDataSize);

    [DllImport(Library, EntryPoint = "pw_context_destroy")]
    internal static extern void ContextDestroy(IntPtr context);

    [DllImport(Library, EntryPoint = "pw_context_connect_fd")]
    internal static extern IntPtr ContextConnectFd(IntPtr context, int fd, IntPtr properties, nuint userDataSize);

    [DllImport(Library, EntryPoint = "pw_properties_new_string", CharSet = CharSet.Unicode)]
    internal static extern IntPtr PropertiesNewString([MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    [DllImport(Library, EntryPoint = "pw_stream_new", CharSet = CharSet.Unicode)]
    internal static extern IntPtr StreamNew(
        IntPtr core,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr properties);

    [DllImport(Library, EntryPoint = "pw_stream_destroy")]
    internal static extern void StreamDestroy(IntPtr stream);

    [DllImport(Library, EntryPoint = "pw_stream_add_listener")]
    internal static extern void StreamAddListener(IntPtr stream, IntPtr hook, IntPtr events, IntPtr data);

    [DllImport(Library, EntryPoint = "pw_stream_connect")]
    internal static extern int StreamConnect(
        IntPtr stream,
        int direction,
        uint targetId,
        uint flags,
        IntPtr parameters,
        uint parameterCount);

    [DllImport(Library, EntryPoint = "pw_stream_dequeue_buffer")]
    internal static extern IntPtr StreamDequeueBuffer(IntPtr stream);

    [DllImport(Library, EntryPoint = "pw_stream_queue_buffer")]
    internal static extern int StreamQueueBuffer(IntPtr stream, IntPtr buffer);

    [DllImport(Library, EntryPoint = "pw_stream_get_state")]
    internal static extern int StreamGetState(IntPtr stream, IntPtr error);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaPod
    {
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaBuffer
    {
        public uint MetaCount;
        public uint DataCount;
        public IntPtr Metas;
        public IntPtr Datas;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaData
    {
        public uint Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public IntPtr Data;
        public IntPtr Chunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpaChunk
    {
        public uint Offset;
        public uint Size;
        public int Stride;
        public uint Flags;
    }
}
