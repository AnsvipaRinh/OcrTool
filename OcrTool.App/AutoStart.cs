using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OcrTool.Core;

namespace OcrTool.App;

internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OcrTool";
    private const string ShortcutName = "OcrTool.lnk";

    private static string StartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key?.GetValue(ValueName) is string value)
            {
                var registeredPath = value.Trim().Trim('"');
                if (File.Exists(registeredPath))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Autostart registry check failed: " + ex.Message);
        }

        // The Startup-folder shortcut is a second per-user launch path. It also
        // survives cases where the Run entry is removed or damaged.
        return File.Exists(StartupShortcutPath);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (key == null)
            throw new InvalidOperationException("Cannot open registry key HKCU\\" + RunKeyPath);

        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            try
            {
                if (File.Exists(StartupShortcutPath))
                    File.Delete(StartupShortcutPath);
            }
            catch (Exception ex)
            {
                Logger.Log("Could not remove Startup-folder shortcut: " + ex.Message);
            }

            Logger.Log("Autostart disabled (Run entry and Startup shortcut removed)");
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException("Cannot resolve the current executable path");

        // Keep the standard Run entry for compatibility with Task Manager.
        key.SetValue(ValueName, "\"" + exe + "\"");

        // Add an independent fallback. The explicit working directory matters:
        // Windows starts Run/Startup applications with a non-application CWD.
        try
        {
            CreateStartupShortcut(exe);
        }
        catch (Exception ex)
        {
            // The Run entry remains valid if Windows Script Host is unavailable.
            Logger.Log("Startup shortcut creation failed; Run entry is retained: " + ex);
        }

        Logger.Log("Autostart enabled: " + exe);
    }

    private static void CreateStartupShortcut(string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StartupShortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create Windows Script Host object");

            dynamic shellObject = shell;
            shortcut = shellObject.CreateShortcut(StartupShortcutPath);
            dynamic link = shortcut;
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Description = "Start OCR Tool with Windows";
            link.WindowStyle = 1;
            link.Save();
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }
}
