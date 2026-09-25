#!/usr/bin/env bash
# Разбор записанного забега одной командой (docs/guides/calibration-replay.md): точки из «Моих данных» → детектор петли
# телефона (Swift, ios/Tools/LoopReplay) → кольцо и контур сервера (C#, этот проект). Нужны Swift и .NET — на Linux,
# в WSL или на Mac. Итог — рядом с входным файлом: <имя>.loops.json и <имя>.report.html; таблицы — на экран.
#
#   bash backend/tools/Gorodki.Calibration/calibrate.sh <мои-данные.json> [--r-min 15,20,25] [--min-area 1500,2500,3500]
#        [--min-half-width 6,9,12] [--r-max 50] [--r-factor 1.62] [--min-path 150] [--min-est-area 1000] [--run <id>]
#        [--newcomer]
set -euo pipefail

if (($# < 1)) || [[ $1 == -* ]]; then
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
fi

[[ -f $1 ]] || { echo "Нет файла: $1" >&2; exit 2; }
input="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
shift
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
base=${input%.json}

# Числа детектора — половине телефона, A_min и R_min — половине сервера.
phone=()
server=()
while (($# > 0)); do
  case $1 in
    --r-min | --r-max | --r-factor | --min-path | --min-est-area | --run)
      (($# >= 2)) || { echo "После $1 нужно значение." >&2; exit 2; }
      phone+=("$1" "$2")
      shift 2
      ;;
    --newcomer)
      phone+=("$1")
      shift
      ;;
    --min-area | --min-half-width)
      (($# >= 2)) || { echo "После $1 нужно значение." >&2; exit 2; }
      server+=("$1" "$2")
      shift 2
      ;;
    *)
      echo "Неизвестный параметр: $1" >&2
      exit 2
      ;;
  esac
done

swift run -c release --package-path "$root/ios/Tools/LoopReplay" LoopReplay "$input" --out "$base.loops.json" "${phone[@]}"
# Из папки backend: там global.json с версией .NET.
cd "$root/backend"
dotnet run -c Release --project tools/Gorodki.Calibration -- "$base.loops.json" --out "$base.report.html" "${server[@]}"
