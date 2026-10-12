using System;
using Microsoft.Win32;
using OcrTool.Core;

namespace OcrTool.App;

internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OcrTool";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (key == null)
        {
            throw new InvalidOperationException("Cannot open registry key HKCU\\" + RunKeyPath);
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                throw new InvalidOperationException("Cannot resolve the executable path");
            }
            key.SetValue(ValueName, "\"" + exe + "\"");
            Logger.Log($"Autostart enabled: {exe}");
        }
        else
        {
            key.DeleteValue(ValueName, false);
            Logger.Log("Autostart disabled");
        }
    }
}
