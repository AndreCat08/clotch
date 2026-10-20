using System.Diagnostics;
using Microsoft.Win32;

namespace Clotch.Core.Settings;

/// <summary>Per-user autostart via HKCU\...\Run (no admin rights; the value is removed on toggle off).</summary>
public sealed class AutoStart(string runKeyPath, string valueName, string command)
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static AutoStart ForCurrentProcess() => new(DefaultRunKey, "Clotch",
        $"\"{Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName}\"");

    public string Command { get; } = command;

    public bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return k?.GetValue(valueName) is string v && v.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(runKeyPath);
        if (enabled) k.SetValue(valueName, Command);
        else k.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
