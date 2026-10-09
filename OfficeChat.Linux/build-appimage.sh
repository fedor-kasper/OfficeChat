#!/usr/bin/env bash
# Сборка OfficeChat для Linux в один файл AppImage (x86_64). .NET на целевом компьютере не нужен.
#
#   ./build-appimage.sh            → dist/OfficeChat-<версия>-x86_64.AppImage
#
# Нужно: .NET 10 SDK (только для сборки), curl. appimagetool скачивается сам при первой сборке.
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' OfficeChat.Linux.csproj)
DIST=dist
APPDIR=$DIST/OfficeChat.AppDir
TOOLS=$DIST/tools
OUTPUT=$DIST/OfficeChat-$VERSION-x86_64.AppImage

echo "==> Публикация (self-contained, linux-x64)"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/lib/officechat"
AVALONIA_TELEMETRY_OPTOUT=1 dotnet publish OfficeChat.Linux.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none -o "$APPDIR/usr/lib/officechat" --nologo

echo "==> Сборка AppDir"
cp Assets/officechat.png "$APPDIR/officechat.png"
ln -sf officechat.png "$APPDIR/.DirIcon"
cat > "$APPDIR/officechat.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=OfficeChat
Comment=Локальный чат для офиса
Exec=officechat
Icon=officechat
Terminal=false
Categories=Network;Chat;
StartupWMClass=officechat
DESKTOP
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/officechat/officechat" "$@"
APPRUN
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/lib/officechat/officechat"

echo "==> Упаковка в AppImage"
mkdir -p "$TOOLS"
TOOL=${APPIMAGETOOL:-$TOOLS/appimagetool}
if [ ! -x "$TOOL" ]; then
    curl -fsSL -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$TOOL"
fi
RUNTIME_ARGS=()
if [ -n "${APPIMAGE_RUNTIME:-}" ]; then RUNTIME_ARGS=(--runtime-file "$APPIMAGE_RUNTIME"); fi
# APPIMAGE_EXTRACT_AND_RUN — чтобы appimagetool работал и без FUSE (в контейнерах, на CI).
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "${RUNTIME_ARGS[@]}" "$APPDIR" "$OUTPUT"

echo
echo "Готово: $OUTPUT"
echo "Запуск: chmod +x $(basename "$OUTPUT") && ./$(basename "$OUTPUT")"
