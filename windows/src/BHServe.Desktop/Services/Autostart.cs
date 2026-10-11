using Microsoft.Win32;

namespace BHServe.App.Services;

/// <summary>
/// Run-at-login via the per-user registry Run key (no admin needed) — the Windows
/// analog of the mac LaunchAgent. Launches the GUI with --tray so it starts hidden.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BHServe";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    public static void Enable()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(ValueName, $"\"{ExePath}\" --tray");
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
