# OCR Tool

A small Windows tray utility: press a hotkey, drag a rectangle over any text
on the screen, and the recognised text lands in the clipboard.

- Windows 10/11, x64, self-contained build (no .NET installation needed)
- English + Russian, decided per line
- Dark-theme aware: light-on-dark regions are inverted before OCR
- Configurable hotkey (default `Ctrl+Shift+Q`)
- Start with Windows (one toggle, no admin rights)
- Headless CLI mode for scripts and CI

## Install

1. Download `OcrTool-v*-win-x64.zip` from the Releases page.
2. Extract it anywhere (for example `C:\Tools\OcrTool`).
3. Run `OcrTool.App.exe` — it sits in the notification area (system tray).

Keep the folder in one place: `config.json` and the autostart entry point at
the exe path.

## Usage

Press **Ctrl+Shift+Q**, drag a rectangle over the text, release. The recognised
text is copied to the clipboard and a toast shows the result.

Tray menu:

| Item | Action |
|---|---|
| Capture OCR | Same as the hotkey, started from the menu |
| Hotkey: … | Change the hotkey |
| Start with Windows | Toggle the autostart entry |
| About | Version, engine and models |
| Exit | Quit |

### Autostart

Enable it from the tray menu (*Start with Windows*), or from the command line:

```powershell
OcrTool.App.exe --autostart on      # enable
OcrTool.App.exe --autostart status  # print "AUTOSTART on/off"
OcrTool.App.exe --autostart off     # disable
```

The entry is stored as `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
with the current exe path. It needs no administrator rights and shows up in
Task Manager → Startup apps, so you can disable it there as well.

### Headless CLI

```powershell
OcrTool.App.exe --ocr-file "C:\path\image.png" --output result.json
```

| Flag | Meaning |
|---|---|
| `--ocr-file <image>` | Image to recognise (PNG/BMP/JPG) |
| `--lang eng\|rus\|eng+rus` | Model set, default `eng+rus` |
| `--output <file.json>` | Where to write the JSON (default: next to the image) |
| `--psm N` | Override the page segmentation mode (0–13) |
| `--oem N` | Override the engine mode |
| `--debug-merge true` | Also run the legacy eng/rus passes and include merge diagnostics |

The JSON contains the recognised `text`, `mean_confidence`, the engine and
pipeline `strategy`, and up to 400 words with bounding boxes and per-word
confidences. Exit code is `0` on success and `1` on failure.

## How the OCR pipeline works

1. **Polarity normalisation** — the captured region is sampled; if it is
   light text on a dark background, it is inverted. Tesseract's models expect
   dark text on a light background, and dark-themed screenshots otherwise come
   back as lines of visually similar Latin/Cyrillic homoglyphs.
2. **Single `eng+rus` pass** — one recognition run over `tessdata_best` models.
3. **Line-level rescue** — lines with low average confidence are re-read with
   the English model. The candidate replaces the original only if it is clearly
   better (confidence floor plus margin), which fixes ambiguous hex/hotkey
   tokens without ever replacing correct Cyrillic with Latin homoglyphs.
4. **Normalisation** — bullets unified to `•`, `=Ox51` / `=8x...` prefixes
   rewritten to `=0x...`, ellipsis artefacts repaired.

Measured on the project reference screenshot (dark theme, 13 lines, mixed
English/Russian):

| Pipeline | CER | WER |
|---|---|---|
| Two-pass merge, no polarity fix | 12.43% | 23.08% |
| **This release** | **1.46%** | **7.69%** |

## Build from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone <repo-url>
cd OcrTool
bash scripts/fetch_tessdata.sh    # downloads eng/rus/osd models (~40 MB, not committed)
bash scripts/package_release.sh   # publish + zip -> dist/OcrTool-v<version>-win-x64.zip
```

Or just publish:

```bash
dotnet publish OcrTool.App/OcrTool.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false -o publish
```

Run `publish/OcrTool.App.exe`.

## Releases

Push a tag starting with `v` (for example `v1.0.0`): GitHub Actions builds the
zip on a Windows runner and attaches it to a GitHub Release.

```bash
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

## Configuration and logs

- `config.json` next to the exe: `{ "Hotkey": "Ctrl+Shift+Q" }`
- Log: `%LOCALAPPDATA%\OcrTool\OcrTool.log`

## License

MIT — see [LICENSE](LICENSE).
