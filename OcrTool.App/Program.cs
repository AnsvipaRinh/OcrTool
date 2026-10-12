using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using OcrTool.Core;

namespace OcrTool.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Headless CLI mode: OCR a file without tray, hotkey, overlay or clipboard.
        // Usage: OcrTool.App.exe --ocr-file <image> [--lang eng+rus] [--output result.json]
        //        [--psm N] [--oem N] [--debug-merge true]
        // Also: OcrTool.App.exe --autostart on|off|status
        if (args.Length >= 2 && string.Equals(args[0], "--ocr-file", StringComparison.OrdinalIgnoreCase))
        {
            RunSilentOcr(args);
            return;
        }

        if (args.Length >= 2 && string.Equals(args[0], "--autostart", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunAutostartCommand(args);
            return;
        }

        Logger.Log("Application startup");

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Logger.Log("UNHANDLED_UI: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Logger.Log("UNHANDLED_DOMAIN: " + e.ExceptionObject);

        Application.Run(new TrayApplicationContext());
        
        Logger.Log("Application exit");
    }

    private static void RunSilentOcr(string[] args)
    {
        string? imagePath = null, outputPath = null, lang = "eng+rus";
        var debugMerge = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--lang":
                    if (i + 1 < args.Length) lang = args[++i];
                    break;
                case "--output":
                    if (i + 1 < args.Length) outputPath = args[++i];
                    break;
                case "--debug-merge":
                    if (i + 1 < args.Length && args[i + 1].Equals("true", StringComparison.OrdinalIgnoreCase)) i++;
                    debugMerge = true;
                    break;
                case "--psm":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var psm)) OcrEngine.DebugPageSegMode = psm;
                    break;
                case "--oem":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var oem)) OcrEngine.DebugEngineMode = oem;
                    break;
                default:
                    if (args[i].StartsWith("--"))
                    {
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++;
                        break;
                    }
                    if (imagePath == null) imagePath = args[i];
                    break;
            }
        }


        string? polarityTemp = null;
        try
        {
            if (imagePath == null) throw new ArgumentException("no image path supplied");
            if (!File.Exists(imagePath)) throw new FileNotFoundException("image not found", imagePath);

            var preparedPath = ImagePrep.PrepareFile(imagePath, out polarityTemp);
            var useDual = string.Equals(lang, "eng+rus", StringComparison.OrdinalIgnoreCase);
            OcrResult merged;
            OcrResult? eng = null, rus = null;
            string? strategy = null;

            if (useDual)
            {
                var dual = OcrEngine.RecognizeDualPass(preparedPath, "silent", debugMerge);
                merged = dual.MergedResult;
                eng = dual.EnglishResult;
                rus = dual.RussianResult;
                strategy = dual.Strategy;
            }
            else
            {
                merged = OcrEngine.Recognize(preparedPath, lang);
            }

            var payload = new Dictionary<string, object?>
            {
                ["type"] = "silent_ocr_result",
                ["image"] = Path.GetFullPath(imagePath),
                ["language_requested"] = lang,
                ["text"] = merged.Text,
                ["chars"] = merged.Text.Length,
                ["mean_confidence"] = merged.MeanConfidence,
                ["duration_ms"] = merged.Duration.TotalMilliseconds,
                ["source_language"] = merged.Language,
                ["engine_used"] = merged.EngineUsed,
                ["passes"] = useDual
                    ? new Dictionary<string, object?>
                    {
                        ["strategy"] = strategy,
                        ["eng"] = new { chars = eng!.Text.Length, conf = eng.MeanConfidence, ms = eng.Duration.TotalMilliseconds, engine = eng.EngineUsed },
                        ["rus"] = new { chars = rus!.Text.Length, conf = rus.MeanConfidence, ms = rus.Duration.TotalMilliseconds, engine = rus.EngineUsed },
                        ["eng+rus"] = new { chars = merged.Text.Length, conf = merged.MeanConfidence, ms = merged.Duration.TotalMilliseconds, engine = merged.EngineUsed }
                    }
                    : null,
                ["words"] = merged.Words.Take(400).Select(w => new { w.Text, w.Confidence, w.SourceLanguage, Box = new { w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height } }).ToArray()
            };

            if (useDual && debugMerge)
            {
                payload["merge_debug"] = MergeDiagnostics.Build(eng!.Words, rus!.Words, merged.Words);
            }

            var self = System.Diagnostics.Process.GetCurrentProcess();
            payload["process_ws_peak_mb"] = Math.Round(self.PeakWorkingSet64 / 1048576.0, 1);
            payload["process_private_mb"] = Math.Round(self.PrivateMemorySize64 / 1048576.0, 1);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var target = outputPath ?? Path.ChangeExtension(Path.GetFullPath(imagePath), ".ocr.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, json);
            Console.WriteLine($"OCR_OK {target} chars={merged.Text.Length} conf={merged.MeanConfidence:F3} ms={merged.Duration.TotalMilliseconds:F1} ws_peak_mb={payload["process_ws_peak_mb"]} private_mb={payload["process_private_mb"]}");
            ImagePrep.DeleteTemp(polarityTemp);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            ImagePrep.DeleteTemp(polarityTemp);
            var payload = new Dictionary<string, object?>
            {
                ["type"] = "silent_ocr_result",
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().FullName
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var target = outputPath ?? "ocr_error.json";
            try
            {
                payload["process_ws_peak_mb"] = Math.Round(System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                File.WriteAllText(target, json);
            }
            catch { }
            Console.Error.WriteLine("OCR_FAIL " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static int RunAutostartCommand(string[] args)
    {
        try
        {
            switch (args[1].ToLowerInvariant())
            {
                case "on":
                    AutoStart.SetEnabled(true);
                    Console.WriteLine("AUTOSTART on");
                    return 0;
                case "off":
                    AutoStart.SetEnabled(false);
                    Console.WriteLine("AUTOSTART off");
                    return 0;
                case "status":
                    Console.WriteLine(AutoStart.IsEnabled() ? "AUTOSTART on" : "AUTOSTART off");
                    return 0;
                default:
                    Console.Error.WriteLine("usage: OcrTool.App.exe --autostart on|off|status");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("AUTOSTART_FAIL " + ex.Message);
            return 1;
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private GlobalHotkey _hotkey;
    private readonly string _configPath;
    private AppConfig _config;
    private SelectionOverlay? _overlay;

    public TrayApplicationContext()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        LoadConfig();

        _trayMenu = CreateTrayMenu();

        _hotkey = new GlobalHotkey(_config.Hotkey);
        _hotkey.HotkeyPressed += OnHotkeyPressed;

        var hotkeyStatus = _hotkey.IsRegistered ? "" : " (HOTKEY UNAVAILABLE)";
        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "OCR Tool - Press " + _config.Hotkey + hotkeyStatus,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.MouseClick += OnTrayIconMouseClick;

        Logger.Log($"Tray icon initialized, hotkey: {_config.Hotkey}, registered: {_hotkey.IsRegistered}");

        if (!_hotkey.IsRegistered)
        {
            Logger.Log("WARNING: RegisterHotKey failed");
            _trayIcon.BalloonTipTitle = "OCR Tool - Hotkey Unavailable";
            _trayIcon.BalloonTipText = "Hotkey registration failed.\nTry restarting the application.";
            _trayIcon.ShowBalloonTip(5000);
        }
        else
        {
            Logger.Log("Hotkey registered successfully");
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            else
            {
                _config = new AppConfig();
                SaveConfig();
            }
        }
        catch
        {
            _config = new AppConfig();
        }
    }

    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        
        var captureItem = new ToolStripMenuItem("Capture OCR", null, (s, e) => StartCapture());
        menu.Items.Add(captureItem);

        menu.Items.Add(new ToolStripSeparator());

        var hotkeyItem = new ToolStripMenuItem("Hotkey: " + _config.Hotkey, null, (s, e) => ChangeHotkey());
        menu.Items.Add(hotkeyItem);

        var autoStartItem = new ToolStripMenuItem("Start with Windows");
        autoStartItem.Checked = AutoStart.IsEnabled();
        autoStartItem.Click += (s, e) =>
        {
            try
            {
                AutoStart.SetEnabled(!AutoStart.IsEnabled());
            }
            catch (Exception ex)
            {
                Logger.Log($"Autostart toggle failed: {ex.Message}");
                _trayIcon.ShowBalloonTip(3000, "OCR Tool", "Could not update startup entry: " + ex.Message, ToolTipIcon.Error);
            }
            autoStartItem.Checked = AutoStart.IsEnabled();
        };
        menu.Items.Add(autoStartItem);
        menu.Opening += (s, e) => autoStartItem.Checked = AutoStart.IsEnabled();

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("About", null, (s, e) => ShowAbout());
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Exit());
        menu.Items.Add(exitItem);

        return menu;
    }

    private Icon CreateTrayIcon()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("OcrTool.App.tray.ico");
        if (stream != null)
        {
            var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
            if (size <= 0 || size > 256)
            {
                size = 16;
            }
            return new Icon(stream, size, size);
        }

        Logger.Log("tray.ico resource missing, using fallback");
        return CreateFrameIcon(16);
    }

    private Icon CreateFrameIcon(int size)
    {
        var scale = size / 16f;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        var inset = 1.6f * scale;
        var arm = 4.2f * scale;
        var x0 = inset;
        var y0 = inset;
        var x1 = size - inset;
        var y1 = size - inset;

        var segments = new List<(PointF A, PointF B)>
        {
            (new PointF(x0, y0), new PointF(x0 + arm, y0)),
            (new PointF(x0, y0), new PointF(x0, y0 + arm)),
            (new PointF(x1, y0), new PointF(x1 - arm, y0)),
            (new PointF(x1, y0), new PointF(x1, y0 + arm)),
            (new PointF(x0, y1), new PointF(x0 + arm, y1)),
            (new PointF(x0, y1), new PointF(x0, y1 - arm)),
            (new PointF(x1, y1), new PointF(x1 - arm, y1)),
            (new PointF(x1, y1), new PointF(x1, y1 - arm)),
            (new PointF(3.0f * scale, 8.0f * scale), new PointF(13.0f * scale, 8.0f * scale))
        };

        using var blackPen = new Pen(Color.Black, 2.6f * scale);
        blackPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        blackPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        using var whitePen = new Pen(Color.White, 1.9f * scale);
        whitePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        whitePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

        foreach (var seg in segments)
        {
            g.DrawLine(blackPen, seg.A, seg.B);
        }
        foreach (var seg in segments)
        {
            g.DrawLine(whitePen, seg.A, seg.B);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Logger.Log("Tray icon left-click: starting capture");
            StartCapture();
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        Logger.Log("WM_HOTKEY received");
        StartCapture();
    }

    private void StartCapture()
    {
        Logger.Log("Starting capture");

        if (_overlay != null) return;

        try
        {
            var snapshot = CaptureVirtualScreen();
            _overlay = new SelectionOverlay(snapshot);
            _overlay.SelectionCompleted += OnSelectionCompleted;
            _overlay.SelectionCancelled += OnSelectionCancelled;
            Logger.Log($"Creating overlay, bounds: {SystemInformation.VirtualScreen}, snapshot: {snapshot?.Width}x{snapshot?.Height}");
            _overlay.Show();
            Logger.Log("Overlay shown");
        }
        catch (Exception ex)
        {
            Logger.Log("StartCapture failed: " + ex);
            _overlay?.Dispose();
            _overlay = null;
        }
    }

    private static Bitmap? CaptureVirtualScreen()
    {
        try
        {
            var bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Log($"Snapshot capture failed: {ex.Message}");
            return null;
        }
    }

    private async void OnSelectionCompleted(object? sender, Rectangle selection)
    {
        Logger.Log($"Selection completed: {selection}");
        var snapshot = _overlay?.Snapshot;
        _overlay?.Dispose();
        _overlay = null;

        Bitmap? bitmap = null;
        try
        {
            if (snapshot != null)
            {
                var src = selection;
                src.Intersect(new Rectangle(Point.Empty, snapshot.Size));
                if (src.Width > 0 && src.Height > 0)
                {
                    bitmap = snapshot.Clone(src, snapshot.PixelFormat);
                    Logger.Log($"Cropped from frozen snapshot: {src.Width}x{src.Height}");
                }
            }
            else
            {
                bitmap = CaptureSelection(selection);
            }

            if (bitmap == null)
            {
                ShowToast("Capture failed", "Try again");
                Logger.Log("Crop failed: empty selection");
                return;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"ocr_capture_{Guid.NewGuid():N}.png");
            using (var prepared = ImagePrep.InvertIfLightOnDark(bitmap))
            {
                (prepared ?? bitmap).Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            Logger.Log($"Running OCR on {tempPath}");
            var result = OcrEngine.RecognizeDualPass(tempPath, "capture");

            if (!string.IsNullOrWhiteSpace(result.MergedResult.Text))
            {
                Clipboard.SetText(result.MergedResult.Text, TextDataFormat.UnicodeText);
                ShowToast("Copied to clipboard", result.MergedResult.Text.Length + " characters");
                Logger.Log($"OCR success: {result.MergedResult.Text.Length} chars copied");
            }
            else
            {
                ShowToast("No text found", "Try selecting a different region");
                Logger.Log("OCR: no text found");
            }

            try { File.Delete(tempPath); } catch { }
        }
        catch (Exception ex)
        {
            ShowToast("OCR Error", ex.Message);
            Logger.Log($"OCR Error: {ex.Message}");
        }
        finally
        {
            try { bitmap?.Dispose(); } catch (Exception ex) { Logger.Log("Dispose bitmap failed: " + ex.Message); }
            try { snapshot?.Dispose(); } catch (Exception ex) { Logger.Log("Dispose snapshot failed: " + ex.Message); }
        }
    }

    private void OnSelectionCancelled(object? sender, EventArgs e)
    {
        Logger.Log("Selection cancelled");
        var snapshot = _overlay?.Snapshot;
        _overlay?.Dispose();
        _overlay = null;
        try { snapshot?.Dispose(); } catch (Exception ex) { Logger.Log("Dispose snapshot failed: " + ex.Message); }
    }

    private Bitmap? CaptureSelection(Rectangle selection)
    {
        try
        {
            var bounds = selection;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void ChangeHotkey()
    {
        using var dialog = new HotkeyDialog(_config.Hotkey);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _hotkey.Dispose();
            _config.Hotkey = dialog.SelectedHotkey;
            _hotkey = new GlobalHotkey(_config.Hotkey);
            _hotkey.HotkeyPressed += OnHotkeyPressed;
            _trayIcon.Text = "OCR Tool - Press " + _config.Hotkey;
            UpdateHotkeyMenuItem();
            SaveConfig();
            Logger.Log($"New hotkey registered for: {_config.Hotkey}");
        }
    }

    private void UpdateHotkeyMenuItem()
    {
        foreach (ToolStripItem item in _trayMenu.Items)
        {
            if (item.Text.StartsWith("Hotkey:"))
            {
                item.Text = "Hotkey: " + _config.Hotkey;
                break;
            }
        }
    }

    private void ShowAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(
            "OCR Tool " + version + "\n\n" +
            "Hotkey: " + _config.Hotkey + "\n" +
            "OCR Engine: Tesseract (bundled) + tessdata_best\n" +
            "Languages: English + Russian, auto-detected per line\n\n" +
            "Press the hotkey, drag to select text region,\n" +
            "result is automatically copied to clipboard.",
            "About OCR Tool",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    private void ShowToast(string title, string message)
    {
        try
        {
            var toast = new ToastForm(title, message);
            toast.Show();
        }
        catch (Exception ex)
        {
            Logger.Log("ShowToast failed: " + ex.Message);
        }
    }

    private void Exit()
    {
        _hotkey?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _overlay?.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkey?.Dispose();
            _trayIcon?.Dispose();
            _overlay?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AppConfig
{
    public string Hotkey { get; set; } = "Ctrl+Shift+Q";
}

internal static class NativeMethods
{
    public const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
}

/// <summary>
/// Diagnostics for --debug-merge: reports, per word cluster, what the eng pass produced,
/// what the rus pass produced, and which one the merge actually used.
/// It never influences selection - the app's merge logic is untouched.
/// </summary>
internal static class MergeDiagnostics
{
    private static float Overlap(Rectangle a, Rectangle b)
    {
        var x1 = Math.Max(a.Left, b.Left);
        var y1 = Math.Max(a.Top, b.Top);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        if (x2 <= x1 || y2 <= y1) return 0f;
        var inter = (x2 - x1) * (float)(y2 - y1);
        var smaller = Math.Min(a.Width * (float)a.Height, b.Width * (float)b.Height);
        return smaller <= 0 ? 0f : Math.Min(1f, inter / smaller);
    }

    public static object Build(List<WordInfo> engWords, List<WordInfo> rusWords, List<WordInfo> mergedWords)
    {
        var rows = new List<object>();
        var scriptMismatch = 0;
        var engWon = 0;
        var rusWon = 0;
        var undecidable = 0;

        // Match eng words to rus words by bounding-box overlap.
        var usedRus = new HashSet<int>();
        foreach (var e in engWords)
        {
            var bestJ = -1;
            var bestScore = 0.30f; // >=30% overlap, mirrors the merge threshold
            for (var j = 0; j < rusWords.Count; j++)
            {
                if (usedRus.Contains(j)) continue;
                var s = Overlap(e.Bounds, rusWords[j].Bounds);
                if (s >= bestScore)
                {
                    bestScore = s;
                    bestJ = j;
                }
            }

            string? rusText = null;
            float rusConf = 0;
            var overlap = bestScore;
            if (bestJ >= 0)
            {
                usedRus.Add(bestJ);
                rusText = rusWords[bestJ].Text;
                rusConf = rusWords[bestJ].Confidence;
            }

            // What did the merge actually output for this cluster?
            var chosenText = "";
            foreach (var m in mergedWords)
            {
                if (Overlap(e.Bounds, m.Bounds) >= 0.30f)
                {
                    chosenText = m.Text;
                    break;
                }
            }

            var source = chosenText.Length == 0 ? "none"
                : string.Equals(chosenText, e.Text, StringComparison.Ordinal) ? "eng"
                : rusText != null && string.Equals(chosenText, rusText, StringComparison.Ordinal) ? "rus"
                : "other";

            if (source == "eng") engWon++;
            else if (source == "rus") rusWon++;
            else undecidable++;

            // The interesting case: eng and rus disagree => script confusion.
            if (rusText != null && !string.Equals(e.Text, rusText, StringComparison.Ordinal))
            {
                scriptMismatch++;
                rows.Add(new
                {
                    box = new { e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height },
                    eng_text = e.Text,
                    eng_conf = e.Confidence,
                    rus_text = rusText,
                    rus_conf = rusConf,
                    overlap = Math.Round(overlap, 3),
                    merged_text = chosenText,
                    merged_from = source,
                    script_conflict = true
                });
            }
        }

        return new
        {
            summary = new
            {
                eng_words = engWords.Count,
                rus_words = rusWords.Count,
                merged_words = mergedWords.Count,
                script_conflicting_clusters = scriptMismatch,
                chosen_from_eng = engWon,
                chosen_from_rus = rusWon,
                chosen_unclear = undecidable,
                note = "script_conflicting_clusters = clusters where eng and rus passes disagreed. merged_from tells you which pass the merge actually kept."
            },
            script_conflicts = rows
        };
    }
}