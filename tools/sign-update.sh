#!/usr/bin/env bash
# Подписывает сборку OfficeChat для обновления по сети: дописывает подпись в конец файла.
#
#   UPDATE_SIGNING_KEY="$(cat key.pem)" tools/sign-update.sh <файл> <версия> <система>
#
# Система — win-x64 или linux-x64. Ключ — закрытый ключ ECDSA P-256 в формате PEM
# (в GitHub Actions — секрет UPDATE_SIGNING_KEY). Программа проверяет подпись открытым ключом
# из Services/UpdateService.cs и без верной подписи обновление не примет.
#
# Формат хвоста: [JSON {"version","platform","signature"}][длина JSON, 8 байт LE]["OCSIGv01"].
# Подписана строка «система\nверсия\nsha256 файла до подписи».
set -euo pipefail

file=$1 version=$2 platform=$3
if [ -z "${UPDATE_SIGNING_KEY:-}" ]; then
    echo "UPDATE_SIGNING_KEY не задан — подпись невозможна" >&2
    exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
umask 077
printf '%s\n' "$UPDATE_SIGNING_KEY" > "$work/key.pem"

hash=$(sha256sum "$file" | cut -d' ' -f1)
printf '%s\n%s\n%s' "$platform" "$version" "$hash" > "$work/data"
openssl dgst -sha256 -sign "$work/key.pem" -out "$work/sig.der" "$work/data"
signature=$(base64 -w0 "$work/sig.der")

block=$(printf '{"version":"%s","platform":"%s","signature":"%s"}' "$version" "$platform" "$signature")
printf '%s' "$block" >> "$file"
python3 -c 'import struct, sys; sys.stdout.buffer.write(struct.pack("<q", int(sys.argv[1])) + b"OCSIGv01")' \
    "${#block}" >> "$file"
echo "Подписано: $file ($platform $version)"
