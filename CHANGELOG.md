# Changelog

## 1.0.2

### Crash fix
- Fixed a crash that terminated the process after a completed hotkey scan:
  an unhandled exception on the UI thread escaped the scan handler, and the
  WinForms `ThreadExceptionDialog` itself failed to open (Win32 error 1410),
  taking the process down.
- Unhandled UI-thread and AppDomain exceptions are now logged to
  `%LOCALAPPDATA%\OcrTool\OcrTool.log` (`UNHANDLED_UI` / `UNHANDLED_DOMAIN`)
  instead of showing a dialog, so the tray app survives and the real exception
  is captured.
- Defensive try/catch around clipboard/toast calls, bitmap disposal in
  `finally` blocks, overlay creation, and the hotkey window procedure.

## 1.0.1

### Background power
- The global hotkey now uses `RegisterHotKey` instead of a low-level
  keyboard hook (`WH_KEYBOARD_LL`): the process is woken only for the exact
  hotkey combination instead of for every keystroke made system-wide.
  Idle CPU in the tray remains 0.
- Removed a duplicated `GCHandle.Alloc` in the hotkey constructor.

## 1.0.0

First public release.

### OCR pipeline
- Single `eng+rus` Tesseract pass with `tessdata_best` models; the legacy
  two-pass word merge is kept only as a fallback and behind `--debug-merge`.
- Polarity normalisation: light-on-dark (dark theme) captures are detected and
  inverted before recognition. Tesseract expects dark text on a light
  background; without this, dark screenshots produced whole lines of homoglyph
  noise.
- Line-level rescue: lines whose average confidence is below the threshold are
  re-read with the English model and swapped in only when the candidate is
  clearly better (confidence floor + margin), so Cyrillic lines are never
  replaced by Latin homoglyph garbage.
- Text normalisation: bullets unified to `•`, `=Ox51`/`=8x...`/`=8o...` style
  prefixes rewritten to `=0x...`, ellipsis artifacts (`. . .`, `.. .`, `. ..`)
  repaired to `...`.
- Page text is rebuilt from word boxes (identical to the raw engine text after
  whitespace normalisation) so single lines can be replaced without touching
  the rest.

Measured on the project reference screenshot (dark theme, 13 lines, mixed
English/Russian), character error rate / word error rate:

| Pipeline | CER | WER |
|---|---|---|
| Two-pass merge, no polarity fix | 12.43% | 23.08% |
| Single pass, no polarity fix | 12.80% | 17.31% |
| **1.0.0 (this release)** | **1.46%** | **7.69%** |

The residual difference against the ground truth is a known gap in the ground
truth itself (four lines in the image carry a bullet that the reference text
does not).

### App
- Tray icon with menu: Capture OCR, Hotkey…, Start with Windows, About, Exit.
- Hotkey capture (default `Ctrl+Shift+Q`), drag a rectangle, result goes to the
  clipboard with a toast notification.
- Autostart with Windows: tray toggle or `--autostart on|off|status`
  (HKCU Run key, no admin rights, visible in Task Manager → Startup apps).
- Headless CLI mode: `--ocr-file <image>` writes a JSON result with the text,
  per-word bounding boxes, confidences and pipeline diagnostics; `--psm` and
  `--oem` override the engine parameters.
- DPI-aware (SystemAware), DPI scaling up to 200% verified.
- Config file (`config.json`) next to the exe; log at
  `%LOCALAPPDATA%\OcrTool\OcrTool.log`.
