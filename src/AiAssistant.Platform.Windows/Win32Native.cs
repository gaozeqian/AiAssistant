using System.Runtime.InteropServices;

namespace AiAssistant.Platform.Windows;

internal static partial class Win32Native
{
    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;
    public const int Srccopy = 0x00CC0020;
    public const uint MonitorInfoPrimary = 1;
    public const uint MonitorDefaultToPrimary = 1;
    public const uint MonitorDefaultToNearest = 2;
    public const int GwlStyle = -16;
    public const int EsPassword = 0x0020;
    public const uint WmGetTextLength = 0x000E;
    public const uint WmGetText = 0x000D;
    public const uint SmtoAbortIfHung = 0x0002;
    public static readonly nuint HkeyCurrentUser = 0x80000001u;
    public const int KeyRead = 0x20019;
    public const int KeyWrite = 0x20006;
    public const uint RegSz = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader BmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref NativeRect lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(NativePoint pt, uint dwFlags);

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeoutW(
        nint hWnd,
        uint msg,
        nuint wParam,
        [Out] char[]? lParam,
        uint fuFlags,
        uint uTimeout,
        out nuint lpdwResult);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint hdc, ref BitmapInfo pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StretchBlt(
        nint hdcDest, int xDest, int yDest, int wDest, int hDest,
        nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegOpenKeyExW(nuint hKey, string lpSubKey, uint ulOptions, int samDesired, out nuint phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegQueryValueExW(nuint hKey, string lpValueName, nint lpReserved, out uint lpType, nint lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegSetValueExW(nuint hKey, string lpValueName, uint reserved, uint dwType, byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegDeleteValueW(nuint hKey, string lpValueName);

    [DllImport("advapi32.dll")]
    public static extern int RegCloseKey(nuint hKey);
}
