#!/usr/bin/env bash
# Скачивает XcodeGen фиксированной версии с GitHub (быстрее и воспроизводимее, чем brew).
#   ios/scripts/install-xcodegen.sh <версия> <папка>
# После установки: <папка>/bin/xcodegen
set -euo pipefail

version="${1:?Укажи версию XcodeGen, например 2.46.0}"
target="${2:?Укажи папку установки}"

archive="$(mktemp -d)/xcodegen.zip"
curl --fail --silent --show-error --location --retry 3 \
    "https://github.com/yonaskolb/XcodeGen/releases/download/${version}/xcodegen.zip" \
    --output "$archive"

rm -rf "$target"
mkdir -p "$target"
unzip -q "$archive" -d "$target.unpacked"
# В архиве лежит папка xcodegen/ с bin/ и share/.
mv "$target.unpacked/xcodegen/"* "$target/"
rm -rf "$target.unpacked"

"$target/bin/xcodegen" --version
