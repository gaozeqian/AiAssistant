using System.Runtime.InteropServices;

namespace AiAssistant.Desktop;

/// <summary>
/// Reads the OS "reduce motion" preference once at startup. When it is on, motion durations are
/// zeroed so transitions and animations resolve instantly.
/// <para>
/// Windows is detected through <c>SPI_GETCLIENTAREAANIMATION</c>. macOS
/// (<c>NSWorkspace.accessibilityDisplayShouldReduceMotion</c>) and Linux
/// (<c>org.gnome.desktop.interface enable-animations</c>) are not wired yet — both need native
/// interop the desktop assembly does not own, so they currently report "no preference" and keep
/// the default durations. This is a documented gap, not a silent assumption.
/// </para>
/// </summary>
internal static class ReducedMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // 调用失败时按"未开启"处理：宁可播放动效，也不要因为探测失败把界面变死板。
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animationsEnabled, 0)
                && !animationsEnabled;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
}
