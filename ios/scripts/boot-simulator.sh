#!/usr/bin/env bash
# Выбирает симулятор iPhone новейшей из установленных iOS, загружает его и печатает «udid=…» — для $GITHUB_OUTPUT:
#   ios/scripts/boot-simulator.sh >> "$GITHUB_OUTPUT"
# Имена симуляторов меняются от Xcode к Xcode, поэтому не зашиты. Всё, кроме строки «udid=…», идёт в stderr: в журнал
# шага, а не в выходы. Общий для ios-build и снимков экранов, чтобы тесты и снимки шли на одинаковом iPhone.
set -euo pipefail

udid=$(xcrun simctl list devices available --json | python3 -c '
import json, sys
runtimes = json.load(sys.stdin)["devices"]
ios = [key for key, devices in runtimes.items()
       if ".SimRuntime.iOS-" in key and any(d["name"].startswith("iPhone") for d in devices)]
if not ios:
    sys.exit("На раннере нет симулятора iPhone. Среды: " + ", ".join(runtimes))
newest = max(ios, key=lambda key: [int(part) for part in key.rsplit("iOS-", 1)[1].split("-")])
print(next(d["udid"] for d in runtimes[newest] if d["name"].startswith("iPhone")))
')
xcrun simctl list devices | grep "$udid" >&2
xcrun simctl bootstatus "$udid" -b >&2
echo "udid=$udid"
