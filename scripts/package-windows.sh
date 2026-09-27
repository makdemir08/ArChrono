#!/usr/bin/env bash
# ArChrono'yu macOS/Linux üzerinden Windows için paketler (çapraz derleme, self-contained tek exe):
#   dist/ArChrono-<sürüm>-Windows-<x64|ARM64>-Setup.exe      (NSIS: brew install makensis)
#   dist/ArChrono-<sürüm>-Windows-<x64|ARM64>-Portable.zip
# Kullanım: scripts/package-windows.sh [win-x64|win-arm64]
# Windows'ta: scripts/publish-windows.ps1
set -euo pipefail

RID="${1:-win-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$ROOT/scripts/dotnet.sh"
OUT="$ROOT/artifacts/windows.noindex/$RID"
PUBLISH="$OUT/publish"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
ARCH_NAME=$([[ "$RID" == "win-arm64" ]] && echo "ARM64" || echo "x64")

rm -rf "$OUT"
mkdir -p "$OUT" "$ROOT/dist"

# İkon: uygulamanın kendi çizimi (tools/ArChrono.Screenshots --icon) → çok boyutlu .ico (PNG girdili).
ICON_PNG="$OUT/icon-1024.png"
ICO="$OUT/ArChrono.ico"
"$DOTNET" run --project "$ROOT/tools/ArChrono.Screenshots" -c Release -v q -- --icon "$ICON_PNG" 1024 >/dev/null
for size in 16 24 32 48 64 128 256; do
  sips -z "$size" "$size" "$ICON_PNG" --out "$OUT/icon-$size.png" >/dev/null
done
python3 - "$OUT" "$ICO" <<'PY'
import struct, sys
out, ico = sys.argv[1], sys.argv[2]
sizes = [16, 24, 32, 48, 64, 128, 256]
images = [open(f"{out}/icon-{s}.png", "rb").read() for s in sizes]
offset = 6 + 16 * len(sizes)
entries, data = b"", b""
for size, png in zip(sizes, images):
    entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset + len(data))
    data += png
with open(ico, "wb") as f:
    f.write(struct.pack("<HHH", 0, 1, len(sizes)) + entries + data)
PY
rm -f "$OUT"/icon-*.png

"$DOTNET" publish "$ROOT/src/ArChrono.App/ArChrono.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None -p:DebugSymbols=false -p:ApplicationIcon="$ICO" -nologo -v q \
  -o "$PUBLISH"
find "$PUBLISH" -name "*.pdb" -delete

ZIP="$ROOT/dist/ArChrono-$VERSION-Windows-$ARCH_NAME-Portable.zip"
rm -f "$ZIP"
(cd "$PUBLISH" && zip -q -9 "$ZIP" ArChrono.exe)
echo "Hazır: $ZIP"

if command -v makensis >/dev/null; then
  SETUP="$ROOT/dist/ArChrono-$VERSION-Windows-$ARCH_NAME-Setup.exe"
  # makensis, Türkçe karakterleri çözebilmek için UTF-8 locale ister.
  LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8 makensis -V2 -DVERSION="$VERSION" -DARCH="$RID" -DSOURCE_DIR="$PUBLISH" \
    -DICON="$ICO" -DOUTFILE="$SETUP" "$ROOT/installers/windows/ArChrono.nsi"
  echo "Hazır: $SETUP"
else
  echo "Not: makensis bulunamadı, Setup.exe atlandı (brew install makensis)." >&2
fi
echo "Not: imzasız paket; Windows SmartScreen ilk açılışta uyarı gösterebilir. ArChrono sistemdeki Git'i kullanır (Git for Windows 2.38+)."
