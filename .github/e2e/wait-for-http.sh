#!/usr/bin/env bash
# Ждать, пока адрес ответит 200. Использование: wait-for-http.sh <url> <секунды>.
url=$1
limit=${2:-120}
for _ in $(seq 1 "$limit"); do
  if curl -fs -o /dev/null "$url"; then
    echo "$url отвечает"
    exit 0
  fi
  sleep 1
done
echo "::error::$url не ответил за $limit с"
exit 1
