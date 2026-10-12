#!/usr/bin/env bash
# Builds the Windows release zip into dist/.
# Usage: bash scripts/package_release.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$ROOT/OcrTool.App/OcrTool.App.csproj"
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$CSPROJ" | head -1)"
[ -n "$VERSION" ] || VERSION="0.0.0"

bash "$ROOT/scripts/fetch_tessdata.sh"

if [ -n "${DOTNET:-}" ]; then
  DOTNET_BIN="$DOTNET"
elif [ -x "$ROOT/dotnet/dotnet" ]; then
  DOTNET_BIN="$ROOT/dotnet/dotnet"
else
  DOTNET_BIN="$(command -v dotnet || true)"
fi
if [ -z "$DOTNET_BIN" ]; then
  echo "dotnet SDK not found: install the .NET 8 SDK or set DOTNET=/path/to/dotnet" >&2
  exit 1
fi

OUT_DIR="$ROOT/dist"
STAGE="$OUT_DIR/publish"
ZIP="$OUT_DIR/OcrTool-v${VERSION}-win-x64.zip"

rm -rf "$STAGE"
mkdir -p "$OUT_DIR"

"$DOTNET_BIN" publish "$CSPROJ" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -o "$STAGE"

rm -f "$ZIP"
(cd "$STAGE" && zip -r -q "$ZIP" .)

echo "wrote $ZIP"
