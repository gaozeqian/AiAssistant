using System.Globalization;
using System.Runtime.InteropServices;

namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// One-time PipeWire native initialization and stream property construction.
/// Kept separate from the stream lifecycle so native setup concerns stay isolated.
/// </summary>
internal static class PipeWireNativeSetup
{
    private static readonly object InitLock = new();
    private static bool _initialised;

    internal static bool EnsureInitialised()
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

    internal static IntPtr BuildStreamProperties(uint? targetSerial)
    {
        var properties = targetSerial.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"media.type=Video media.category=Capture media.role=Screen target.object={targetSerial.Value}")
            : "media.type=Video media.category=Capture media.role=Screen";
        return PipeWireNative.PropertiesNewString(properties);
    }

    /// <summary>True when the PipeWire client library can be loaded on this host.</summary>
    internal static bool IsLibraryAvailable
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

}
