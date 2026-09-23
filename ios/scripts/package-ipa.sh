#!/usr/bin/env bash
# Упаковывает собранный Gorodki.app в два IPA для установки через Sideloadly (спайк S2):
#   gorodki-free-unsigned.ipa — без подписи; Sideloadly подпишет своим сертификатом;
#   gorodki-free-adhoc.ipa    — ad-hoc подпись с entitlements (App Group), чтобы установщик
#                               их увидел и перенёс в свою подпись.
# Какой из двух вариантов лучше переживает установку на iOS 27 — и есть вопрос спайка.
#
# Запуск (только на macOS, нужен codesign):
#   ios/scripts/package-ipa.sh <путь к Gorodki.app> <папка для IPA>
set -euo pipefail

app_source="${1:?Укажи путь к Gorodki.app}"
out_dir="${2:?Укажи папку для IPA}"

mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

app_name="$(basename "$app_source")"

# 1. Без подписи: просто кладём .app в Payload/ и сжимаем.
mkdir -p "$work/unsigned/Payload"
cp -R "$app_source" "$work/unsigned/Payload/"
(cd "$work/unsigned" && zip -qry "$out_dir/gorodki-free-unsigned.ipa" Payload)

# 2. Ad-hoc подпись. Сборка без подписи не содержит entitlements, поэтому собираем их сами:
#    идентификатор App Group берём из Info.plist приложения (ключ GorodkiAppGroupID).
mkdir -p "$work/adhoc/Payload"
cp -R "$app_source" "$work/adhoc/Payload/"
app="$work/adhoc/Payload/$app_name"

group_id="$(/usr/libexec/PlistBuddy -c 'Print :GorodkiAppGroupID' "$app/Info.plist")"
entitlements="$work/app.entitlements"
cat > "$entitlements" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.application-groups</key>
  <array>
    <string>$group_id</string>
  </array>
</dict>
</plist>
PLIST

# Подписываем изнутри наружу: сначала вложенные библиотеки и расширения, потом само приложение.
find "$app" -depth \( -name '*.framework' -o -name '*.dylib' \) -print0 \
    | xargs -0 -I{} codesign --force --sign - --timestamp=none {}
for extension in "$app"/PlugIns/*.appex; do
    [ -e "$extension" ] || continue
    codesign --force --sign - --timestamp=none --entitlements "$entitlements" "$extension"
done
codesign --force --sign - --timestamp=none --entitlements "$entitlements" "$app"

echo "Entitlements приложения после подписи:"
codesign -d --entitlements - --xml "$app" | plutil -p - || true
codesign --verify --deep --strict "$app"

(cd "$work/adhoc" && zip -qry "$out_dir/gorodki-free-adhoc.ipa" Payload)

echo "Готово:"
ls -lh "$out_dir"/*.ipa
