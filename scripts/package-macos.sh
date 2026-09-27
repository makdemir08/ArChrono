#!/usr/bin/env bash
# ArChrono.app ve .dmg paketini oluşturur (self-contained).
# Kullanım: scripts/package-macos.sh [osx-arm64|osx-x64] [--dmg]
#
# Ortam değişkenleri (isteğe bağlı):
#   MAC_SIGN_IDENTITY   "Developer ID Application: Ad (TEAMID)" — yoksa ad-hoc imzalanır
#   MAC_NOTARY_PROFILE  xcrun notarytool store-credentials ile oluşturulan profil (dmg notarize edilir)
set -euo pipefail

RID="${1:-osx-arm64}"
MAKE_DMG="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$ROOT/scripts/dotnet.sh"
OUT="$ROOT/artifacts/macos.noindex/$RID"
APP="$OUT/ArChrono.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

"$DOTNET" publish "$ROOT/src/ArChrono.App/ArChrono.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:DebugType=None -p:DebugSymbols=false -nologo -v q \
  -o "$APP/Contents/MacOS"
find "$APP/Contents/MacOS" -name "*.pdb" -delete

# İkon: uygulamanın kendi çizimi (tools/ArChrono.Screenshots --icon).
ICON_PNG="$OUT/icon-1024.png"
"$DOTNET" run --project "$ROOT/tools/ArChrono.Screenshots" -c Release -v q -- --icon "$ICON_PNG" 1024 >/dev/null
ICONSET="$OUT/ArChrono.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/ArChrono.icns"
rm -rf "$ICONSET" "$ICON_PNG"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ArChrono</string>
  <key>CFBundleDisplayName</key><string>ArChrono</string>
  <key>CFBundleIdentifier</key><string>dev.archrono.app</string>
  <key>CFBundleExecutable</key><string>ArChrono</string>
  <key>CFBundleIconFile</key><string>ArChrono</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

if [[ -n "${MAC_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --timestamp --options runtime \
    --entitlements "$ROOT/installers/macos/ArChrono.entitlements" \
    --sign "$MAC_SIGN_IDENTITY" "$APP"
else
  echo "Not: ad-hoc imza (dağıtım için MAC_SIGN_IDENTITY ayarlayın)." >&2
  codesign --force --deep --sign - "$APP"
fi
echo "Hazır: $APP"

if [[ "$MAKE_DMG" == "--dmg" ]]; then
  mkdir -p "$ROOT/dist"
  ARCH_NAME=$([[ "$RID" == "osx-arm64" ]] && echo "AppleSilicon" || echo "Intel")
  DMG="$ROOT/dist/ArChrono-${VERSION}-macOS-${ARCH_NAME}.dmg"
  STAGE="$OUT/dmg"
  rm -rf "$STAGE" "$DMG"
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -volname "ArChrono" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
  rm -rf "$STAGE"
  if [[ -n "${MAC_SIGN_IDENTITY:-}" ]]; then codesign --force --sign "$MAC_SIGN_IDENTITY" "$DMG"; fi
  if [[ -n "${MAC_NOTARY_PROFILE:-}" ]]; then
    xcrun notarytool submit "$DMG" --keychain-profile "$MAC_NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
  fi
  echo "Hazır: $DMG"
fi
