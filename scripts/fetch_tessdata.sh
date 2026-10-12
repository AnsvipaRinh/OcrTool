#!/usr/bin/env bash
# Downloads the tessdata_best models used by OcrTool.
# The models are not committed to git (~40 MB of binaries).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/OcrTool.App/tessdata_best"
BASE="https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main"

mkdir -p "$DEST"
for f in eng rus osd; do
  if [ -s "$DEST/$f.traineddata" ]; then
    echo "have  $f.traineddata"
  else
    echo "fetch $f.traineddata"
    curl -L --fail --silent --show-error -o "$DEST/$f.traineddata" "$BASE/$f.traineddata"
  fi
done
echo "tessdata ready: $DEST"
