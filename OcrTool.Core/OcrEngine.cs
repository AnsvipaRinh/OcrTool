using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tesseract;

namespace OcrTool.Core;

public sealed class OcrResult
{
    public string Text { get; set; } = "";
    public List<WordInfo> Words { get; set; } = new();
    public double MeanConfidence { get; set; }
    public TimeSpan Duration { get; set; }
    public string Language { get; set; } = "";
    public string EngineUsed { get; set; } = "";
}

public sealed class WordInfo
{
    public string Text { get; set; } = "";
    public Rectangle Bounds { get; set; }
    public float Confidence { get; set; }
    public string SourceLanguage { get; set; } = "";
}

public sealed class DualPassResult
{
    public OcrResult EnglishResult { get; set; } = new();
    public OcrResult RussianResult { get; set; } = new();
    public OcrResult MergedResult { get; set; } = new();
    public string Strategy { get; set; } = "dual-pass-merge";
    public string TestName { get; set; } = "";
    public TimeSpan TotalDuration { get; set; }
}

public static class OcrEngine
{
    private static readonly string TessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata_best");

    private const double MatchOverlapThreshold = 0.3;
    private const double OrphanOverlapThreshold = 0.1;
    private const double ScriptBonus = 15.0;
    private const double NearTieMargin = 25.0;
    private const float HighConfidence = 65f;
    private const float LowConfidenceFloor = 40f;

    private const double RescueLineThreshold = 88.0;
    private const double RescueConfidenceMargin = 5.0;
    private const double RescueMinimumConfidence = 75.0;

    private static bool? _systemTesseractAvailable;
    private static string? _systemTesseractPath;

    /// <summary>Optional PageSegMode (0-13) override, set via the --psm CLI flag.</summary>
    public static int? DebugPageSegMode { get; set; }

    /// <summary>Optional EngineMode/OEM override, set via the --oem CLI flag.</summary>
    public static int? DebugEngineMode { get; set; }

    public static OcrResult Recognize(string imagePath, string language)
    {
        Logger.Log($"Recognize: path={imagePath}, lang={language}");

        var systemResult = TryRecognizeWithSystemTesseract(imagePath, language);
        if (systemResult != null)
        {
            Logger.Log($"System tesseract succeeded: {systemResult.EngineUsed}, words={systemResult.Words.Count}, conf={systemResult.MeanConfidence:F1}");
            return systemResult;
        }

        Logger.Log("Falling back to bundled tesseract 5.2.0");
        using var engine = new TesseractEngine(TessDataPath, language,
            DebugEngineMode.HasValue ? (EngineMode)DebugEngineMode.Value : EngineMode.Default);
        using var img = Pix.LoadFromFile(imagePath);

        var start = DateTime.UtcNow;
        using var page = DebugPageSegMode.HasValue
            ? engine.Process(img, (PageSegMode)DebugPageSegMode.Value)
            : engine.Process(img);
        var duration = DateTime.UtcNow - start;

        var result = new OcrResult
        {
            MeanConfidence = ToPercent(page.GetMeanConfidence()),
            Duration = duration,
            Language = language,
            EngineUsed = "bundled-5.2.0"
        };

        result.Words = ExtractWords(page, language);

        Logger.Log($"Bundled tesseract: words={result.Words.Count}, conf={result.MeanConfidence:F1}");

        var rebuilt = NormalizeText(BuildText(GroupWordsIntoLines(result.Words)));
        result.Text = string.IsNullOrWhiteSpace(rebuilt) ? NormalizeText(page.GetText()) : rebuilt;
        return result;
    }

    private static List<WordInfo> ExtractWords(Page page, string language)
    {
        var words = new List<WordInfo>();
        using var iterator = page.GetIterator();
        iterator.Begin();
        do
        {
            if (iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
            {
                var text = iterator.GetText(PageIteratorLevel.Word) ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    words.Add(new WordInfo
                    {
                        Text = text.Trim(),
                        Bounds = new Rectangle(rect.X1, rect.Y1, rect.Width, rect.Height),
                        Confidence = iterator.GetConfidence(PageIteratorLevel.Word),
                        SourceLanguage = language
                    });
                }
            }
        } while (iterator.Next(PageIteratorLevel.Word));
        return words;
    }

    /// <summary>charlesw <c>Page.GetMeanConfidence()</c> returns 0..1 while word confidences are 0..100.</summary>
    private static double ToPercent(double confidence) => confidence <= 1.0 ? confidence * 100.0 : confidence;

    private static OcrResult? TryRecognizeWithSystemTesseract(string imagePath, string language)
    {
        if (!IsSystemTesseractAvailable())
        {
            Logger.Log("System tesseract not available");
            return null;
        }

        var langCode = language switch
        {
            "eng" => "eng",
            "rus" => "rus",
            "eng+rus" => "eng+rus",
            _ => language
        };

        var tessdataDir = TessDataPath;
        var psm = DebugPageSegMode ?? 6;
        var args = $"-l {langCode} --psm {psm} --tessdata-dir \"{tessdataDir}\" -c tessedit_create_tsv=1 \"{imagePath}\" stdout";
        var startInfo = new ProcessStartInfo
        {
            FileName = _systemTesseractPath!,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        var start = DateTime.UtcNow;
        using var process = Process.Start(startInfo);
        if (process == null) return null;

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var duration = DateTime.UtcNow - start;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            Logger.Log($"System tesseract failed: exit={process.ExitCode}, err={error}");
            return null;
        }

        var result = ParseTsvOutput(output, language, duration);
        if (result == null) return null;

        result.EngineUsed = $"system-tesseract ({GetSystemTesseractVersion()})";
        return result;
    }

    private static OcrResult? ParseTsvOutput(string tsvOutput, string language, TimeSpan duration)
    {
        var lines = tsvOutput.Split('\n');
        if (lines.Length < 2) return null;

        var words = new List<WordInfo>();

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 12) continue;

            if (!float.TryParse(parts[10], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float confF)) continue;
            if (confF < 0) continue;

            var text = parts[11];
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (!int.TryParse(parts[6], out int left) ||
                !int.TryParse(parts[7], out int top) ||
                !int.TryParse(parts[8], out int width) ||
                !int.TryParse(parts[9], out int height))
                continue;

            words.Add(new WordInfo
            {
                Text = text,
                Bounds = new Rectangle(left, top, width, height),
                Confidence = confF,
                SourceLanguage = language
            });
        }

        if (words.Count == 0) return null;

        var rebuiltText = NormalizeText(BuildText(GroupWordsIntoLines(words)));
        var result = new OcrResult
        {
            Text = rebuiltText,
            Words = words,
            MeanConfidence = words.Count > 0 ? words.Average(w => w.Confidence) : 0,
            Duration = duration,
            Language = language
        };

        return result;
    }

    private static bool IsSystemTesseractAvailable()
    {
        if (_systemTesseractAvailable.HasValue) return _systemTesseractAvailable.Value;

        var candidates = new[]
        {
            "tesseract",
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scoop", "shims", "tesseract.exe")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) continue;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && output.Contains("tesseract"))
                {
                    _systemTesseractAvailable = true;
                    _systemTesseractPath = candidate;
                    Logger.Log($"Found system tesseract at: {candidate}");
                    return true;
                }
            }
            catch
            {
            }
        }

        Logger.Log("System tesseract not found in any standard location");
        _systemTesseractAvailable = false;
        return false;
    }

    private static string GetSystemTesseractVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _systemTesseractPath ?? "tesseract",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return "unknown";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var line = output.Split('\n')[0];
            return line.Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    public static DualPassResult RecognizeDualPass(string imagePath, string testName, bool includeLegacyPasses = false)
    {
        var start = DateTime.UtcNow;

        OcrResult? combined = null;
        try
        {
            combined = Recognize(imagePath, "eng+rus");
        }
        catch (Exception ex)
        {
            Logger.Log($"Single-pass eng+rus failed: {ex.Message}");
        }

        if (combined != null && combined.Words.Count > 0 && !string.IsNullOrWhiteSpace(combined.Text))
        {
            Logger.Log($"Single-pass eng+rus: words={combined.Words.Count}, conf={combined.MeanConfidence:F1}, ms={combined.Duration.TotalMilliseconds:F0}");

            if (includeLegacyPasses)
            {
                var legacyEng = Recognize(imagePath, "eng");
                var legacyRus = Recognize(imagePath, "rus");
                var rescuedDebug = TryRescueLowConfLines(imagePath, combined, legacyEng);
                return new DualPassResult
                {
                    TestName = testName,
                    Strategy = rescuedDebug > 0
                        ? "single-pass-eng+rus (+debug passes, line-rescue)"
                        : "single-pass-eng+rus (+debug passes)",
                    EnglishResult = legacyEng,
                    RussianResult = legacyRus,
                    MergedResult = combined,
                    TotalDuration = DateTime.UtcNow - start
                };
            }

            var rescued = TryRescueLowConfLines(imagePath, combined);
            return new DualPassResult
            {
                TestName = testName,
                Strategy = rescued > 0 ? "single-pass-eng+rus+line-rescue" : "single-pass-eng+rus",
                EnglishResult = combined,
                RussianResult = combined,
                MergedResult = combined,
                TotalDuration = DateTime.UtcNow - start
            };
        }

        Logger.Log("Single-pass eng+rus produced no text, falling back to legacy dual-pass merge");

        var engResult = Recognize(imagePath, "eng");
        var rusResult = Recognize(imagePath, "rus");

        var merged = MergeWordLevel(engResult, rusResult);

        return new DualPassResult
        {
            TestName = testName,
            Strategy = "dual-pass-merge (fallback)",
            EnglishResult = engResult,
            RussianResult = rusResult,
            MergedResult = merged,
            TotalDuration = DateTime.UtcNow - start
        };
    }

    private sealed class WordPair
    {
        public WordInfo? Eng;
        public WordInfo? Rus;
        public WordInfo Chosen = null!;
        public double Margin;
        public bool ScriptConflict;
        public int LineIndex = -1;
    }

    private static OcrResult MergeWordLevel(OcrResult eng, OcrResult rus)
    {
        var pairs = new List<WordPair>();
        var usedRus = new bool[rus.Words.Count];

        foreach (var engWord in eng.Words)
        {
            var idx = FindBestOverlap(engWord.Bounds, rus.Words, usedRus);
            if (idx >= 0)
            {
                usedRus[idx] = true;
                var rusWord = rus.Words[idx];
                if (IsFragmentPair(engWord, rusWord))
                {
                    continue;
                }
                pairs.Add(Decide(engWord, rusWord));
            }
            else
            {
                if (IsFragmentOfEng(engWord, rus.Words))
                {
                    continue;
                }
                pairs.Add(new WordPair { Eng = engWord, Chosen = engWord });
            }
        }

        for (int j = 0; j < rus.Words.Count; j++)
        {
            if (usedRus[j]) continue;
            if (OverlapsAnyEng(rus.Words[j].Bounds, eng.Words, OrphanOverlapThreshold)) continue;
            pairs.Add(new WordPair { Rus = rus.Words[j], Chosen = rus.Words[j] });
        }

        AssignLines(pairs);
        ApplyLineScriptVoting(pairs);

        var merged = new OcrResult
        {
            Language = "eng+rus (word merge)",
            Duration = eng.Duration + rus.Duration,
            EngineUsed = string.Equals(eng.EngineUsed, rus.EngineUsed, StringComparison.Ordinal)
                ? eng.EngineUsed
                : $"{eng.EngineUsed} + {rus.EngineUsed}"
        };

        merged.Words = pairs.Select(p => p.Chosen)
            .OrderBy(w => w.Bounds.Top)
            .ThenBy(w => w.Bounds.Left)
            .ToList();

        merged.Text = NormalizeText(BuildText(GroupWordsIntoLines(merged.Words)));
        merged.MeanConfidence = merged.Words.Count > 0
            ? merged.Words.Average(w => w.Confidence)
            : 0;

        return merged;
    }

    private static WordPair Decide(WordInfo eng, WordInfo rus)
    {
        var pair = new WordPair { Eng = eng, Rus = rus };

        if (eng.Text == rus.Text)
        {
            pair.Chosen = eng.Confidence >= rus.Confidence ? eng : rus;
            pair.Margin = Math.Abs(eng.Confidence - rus.Confidence);
            pair.ScriptConflict = false;
            return pair;
        }

        var engScore = eng.Confidence + ScriptAgreementBonus(eng.Text, expectLatin: true);
        var rusScore = rus.Confidence + ScriptAgreementBonus(rus.Text, expectLatin: false);
        pair.Margin = Math.Abs(engScore - rusScore);
        pair.ScriptConflict = ScriptConflict(eng.Text, rus.Text);
        pair.Chosen = engScore >= rusScore ? eng : rus;
        return pair;
    }

    private static int FindBestOverlap(Rectangle bounds, List<WordInfo> candidates, bool[] used)
    {
        var bestIdx = -1;
        var bestOverlap = 0.0;

        for (int j = 0; j < candidates.Count; j++)
        {
            if (used[j]) continue;

            var overlap = CalculateOverlap(bounds, candidates[j].Bounds);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestIdx = j;
            }
        }

        return bestOverlap >= MatchOverlapThreshold ? bestIdx : -1;
    }

    private static readonly Regex HexPrefixPattern = new Regex(@"=([8eEoOqQaAcsS\u041E\u043E])[xX](\.\.\.|\d)", RegexOptions.Compiled);
    private static readonly Regex BulletPattern = new Regex(@"(?m)^([ \t]*)[\u00A9\u00AE\u2022\u2023\u25CF\u25CB\u25E6\u25AA]", RegexOptions.Compiled);

    private static string NormalizeHexPrefixes(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return HexPrefixPattern.Replace(text, "=0x$2");
    }

    private static string NormalizeBullets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return BulletPattern.Replace(text, m => m.Groups[1].Value + "\u2022");
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = NormalizeBullets(NormalizeHexPrefixes(text));
        return text.Replace(". . .", "...").Replace(".. .", "...").Replace(". ..", "...");
    }

    private static bool IsFragmentPair(WordInfo a, WordInfo b)
    {
        return IsFragmentOfEng(a, new List<WordInfo> { b }) && IsFragmentOfEng(b, new List<WordInfo> { a });
    }

    private static bool IsFragmentOfEng(WordInfo word, List<WordInfo> otherWords)
    {
        if (word.Text.Length > 15) return false;
        if (word.Confidence >= 50f) return false;

        foreach (var other in otherWords)
        {
            var intersect = Rectangle.Intersect(word.Bounds, other.Bounds);
            if (intersect.IsEmpty) continue;

            var verticalOverlap = (double)intersect.Height / Math.Min(word.Bounds.Height, other.Bounds.Height);
            var horizontalOverlap = (double)intersect.Width / Math.Min(word.Bounds.Width, other.Bounds.Width);

            if (verticalOverlap > 0.5 && horizontalOverlap > 0.1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool OverlapsAnyEng(Rectangle bounds, List<WordInfo> engWords, double threshold)
    {
        foreach (var engWord in engWords)
        {
            if (CalculateOverlap(bounds, engWord.Bounds) >= threshold)
            {
                return true;
            }
        }
        return false;
    }

    private static double CalculateOverlap(Rectangle a, Rectangle b)
    {
        var intersect = Rectangle.Intersect(a, b);
        if (intersect.IsEmpty) return 0.0;

        var areaA = (double)a.Width * a.Height;
        var areaB = (double)b.Width * b.Height;
        var areaIntersect = (double)intersect.Width * intersect.Height;

        if (areaA <= 0 || areaB <= 0) return 0.0;

        return Math.Max(areaIntersect / areaA, areaIntersect / areaB);
    }

    private static void AssignLines(List<WordPair> pairs)
    {
        var lineTops = new List<float>();
        var lineHeights = new List<float>();

        foreach (var pair in pairs)
        {
            var top = pair.Chosen.Bounds.Top;
            var height = pair.Chosen.Bounds.Height;
            var lineIdx = -1;

            for (int i = 0; i < lineTops.Count; i++)
            {
                if (Math.Abs(top - lineTops[i]) <= Math.Max(lineHeights[i] * 0.6f, 4f))
                {
                    lineIdx = i;
                    break;
                }
            }

            if (lineIdx >= 0)
            {
                pair.LineIndex = lineIdx;
                continue;
            }

            lineTops.Add(top);
            lineHeights.Add(height);
            pair.LineIndex = lineTops.Count - 1;
        }
    }

    private static void ApplyLineScriptVoting(List<WordPair> pairs)
    {
        var lineCount = pairs.Count > 0 ? pairs.Max(p => p.LineIndex) + 1 : 0;
        if (lineCount == 0) return;

        for (int line = 0; line < lineCount; line++)
        {
            var linePairs = pairs.Where(p => p.LineIndex == line).ToList();

            var latinVotes = 0;
            var cyrillicVotes = 0;

            foreach (var pair in linePairs)
            {
                if (pair.Eng != null && pair.Eng.Confidence >= HighConfidence && IsPureLatin(pair.Eng.Text))
                {
                    latinVotes++;
                }
                if (pair.Rus != null && pair.Rus.Confidence >= HighConfidence && IsPureCyrillic(pair.Rus.Text))
                {
                    cyrillicVotes++;
                }
            }

            if (latinVotes == cyrillicVotes) continue;

            var preferLatin = latinVotes > cyrillicVotes;

            foreach (var pair in linePairs)
            {
                if (!pair.ScriptConflict || pair.Margin >= NearTieMargin) continue;
                if (pair.Eng == null || pair.Rus == null) continue;

                if (pair.Chosen.Confidence >= HighConfidence) continue;

                var alt = ReferenceEquals(pair.Chosen, pair.Eng) ? pair.Rus : pair.Eng;
                if (alt == null || alt.Confidence < LowConfidenceFloor) continue;

                var chosenLatin = IsPureLatin(pair.Chosen.Text);
                var altLatin = IsPureLatin(alt.Text);

                if (preferLatin && !chosenLatin && altLatin)
                {
                    pair.Chosen = alt;
                }
                else if (!preferLatin && chosenLatin && !altLatin)
                {
                    pair.Chosen = alt;
                }
            }
        }
    }

    private static double ScriptAgreementBonus(string text, bool expectLatin)
    {
        var hasLatin = false;
        var hasCyrillic = false;

        foreach (var c in text)
        {
            if (c >= 0x0400 && c <= 0x04FF)
            {
                hasCyrillic = true;
            }
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= 0x00C0 && c <= 0x024F))
            {
                hasLatin = true;
            }
        }

        if (!hasLatin && !hasCyrillic) return 0.0;

        var scriptAgrees = expectLatin ? hasLatin && !hasCyrillic : hasCyrillic && !hasLatin;
        return scriptAgrees ? ScriptBonus : -ScriptBonus;
    }

    private static bool ScriptConflict(string engText, string rusText)
    {
        var engLatin = IsPureLatin(engText);
        var engCyrillic = IsPureCyrillic(engText);
        var rusLatin = IsPureLatin(rusText);
        var rusCyrillic = IsPureCyrillic(rusText);

        return (engLatin && rusCyrillic) || (engCyrillic && rusLatin);
    }

    private static bool IsPureLatin(string text)
    {
        var hasLatin = false;
        foreach (var c in text)
        {
            if (c >= 0x0400 && c <= 0x04FF) return false;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= 0x00C0 && c <= 0x024F))
            {
                hasLatin = true;
            }
        }
        return hasLatin;
    }

    private static bool IsPureCyrillic(string text)
    {
        var hasCyrillic = false;
        foreach (var c in text)
        {
            if (c >= 0x0400 && c <= 0x04FF)
            {
                hasCyrillic = true;
            }
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= 0x00C0 && c <= 0x024F))
            {
                return false;
            }
        }
        return hasCyrillic;
    }

    private sealed class TextLine
    {
        public List<WordInfo> Words { get; } = new();
        public double Cy { get; set; }
        public double Height { get; set; }
        public double Top => Words.Count == 0 ? 0 : Words.Min(w => w.Bounds.Top);
        public double AverageConfidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence);
        public string Text => string.Join(" ", Words.Select(w => w.Text));
    }

    private static List<TextLine> GroupWordsIntoLines(IEnumerable<WordInfo> words)
    {
        var lines = new List<TextLine>();

        foreach (var word in words.OrderBy(w => w.Bounds.Top).ThenBy(w => w.Bounds.Left))
        {
            var cy = word.Bounds.Top + word.Bounds.Height / 2.0;
            TextLine? target = null;

            foreach (var line in lines)
            {
                if (Math.Abs(cy - line.Cy) <= Math.Max(line.Height * 0.6, 8))
                {
                    target = line;
                    break;
                }
            }

            if (target == null)
            {
                lines.Add(new TextLine { Words = { word }, Cy = cy, Height = word.Bounds.Height });
            }
            else
            {
                var count = target.Words.Count;
                target.Cy = (target.Cy * count + cy) / (count + 1);
                target.Height = Math.Max(target.Height, word.Bounds.Height);
                target.Words.Add(word);
            }
        }

        foreach (var line in lines)
        {
            line.Words.Sort((x, y) => x.Bounds.Left.CompareTo(y.Bounds.Left));
        }

        return lines.OrderBy(l => l.Top).ToList();
    }

    private static string BuildText(IEnumerable<TextLine> lines)
        => string.Join(Environment.NewLine, lines.Select(l => l.Text));

    private static int TryRescueLowConfLines(string imagePath, OcrResult primary, OcrResult? engPass = null)
    {
        try
        {
            var lines = GroupWordsIntoLines(primary.Words);
            var suspect = lines.Where(l => l.Words.Count > 0 && l.AverageConfidence < RescueLineThreshold).ToList();
            if (suspect.Count == 0) return 0;

            var start = DateTime.UtcNow;
            var eng = engPass ?? Recognize(imagePath, "eng");
            var engLines = GroupWordsIntoLines(eng.Words);

            var replaced = 0;
            foreach (var line in suspect)
            {
                var match = engLines.FirstOrDefault(l => Math.Abs(l.Cy - line.Cy) <= Math.Max(line.Height * 0.6, 8));
                if (match == null) continue;
                if (match.AverageConfidence < RescueMinimumConfidence) continue;
                if (match.AverageConfidence < line.AverageConfidence - RescueConfidenceMargin) continue;

                line.Words.Clear();
                line.Words.AddRange(match.Words);
                replaced++;
            }

            if (replaced > 0)
            {
                primary.Words = lines
                    .SelectMany(l => l.Words)
                    .OrderBy(w => w.Bounds.Top)
                    .ThenBy(w => w.Bounds.Left)
                    .ToList();

                var rebuilt = NormalizeText(BuildText(lines));
                if (!string.IsNullOrWhiteSpace(rebuilt)) primary.Text = rebuilt;
            }

            Logger.Log($"Line rescue: {replaced}/{suspect.Count} lines replaced from eng pass, ms={(DateTime.UtcNow - start).TotalMilliseconds:F0}");
            return replaced;
        }
        catch (Exception ex)
        {
            Logger.Log($"Line rescue skipped: {ex.Message}");
            return 0;
        }
    }
}
