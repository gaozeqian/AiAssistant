using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Optional Windows user-level startup switch via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Disabled by default; never requires administrator elevation.
/// </summary>
public sealed class WindowsAutoStartService(string executableCommand, string valueName = "AiAssistant") : IAutoStartService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.AutoStart, @"HKCU\...\Run user auto-start")
        : CapabilityState.Unavailable(CapabilityKind.AutoStart, "Windows 注册表自启仅在 Windows 主机可用");

    public Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        if (Win32Native.RegOpenKeyExW(Win32Native.HkeyCurrentUser, RunSubKey, 0, Win32Native.KeyRead, out var hKey) != 0)
        {
            return Task.FromResult(false);
        }

        try
        {
            uint size = 0;
            var rc = Win32Native.RegQueryValueExW(hKey, valueName, 0, out _, 0, ref size);
            return Task.FromResult(rc == 0 && size > 0);
        }
        finally
        {
            _ = Win32Native.RegCloseKey(hKey);
        }
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        if (Win32Native.RegOpenKeyExW(Win32Native.HkeyCurrentUser, RunSubKey, 0, Win32Native.KeyWrite, out var hKey) != 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (enabled)
            {
                var bytes = Encoding.Unicode.GetBytes(executableCommand + '\0');
                _ = Win32Native.RegSetValueExW(hKey, valueName, 0, Win32Native.RegSz, bytes, (uint)bytes.Length);
            }
            else
            {
                _ = Win32Native.RegDeleteValueW(hKey, valueName);
            }
        }
        finally
        {
            _ = Win32Native.RegCloseKey(hKey);
        }

        return Task.CompletedTask;
    }
}
