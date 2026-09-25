#!/usr/bin/env bash
# База «стабильно отвечает» — снаружи, через опубликованный порт, как к ней подключится сервер: образ Supabase гоняет
# свои скрипты на временном сервере и перезапускает его, поэтому одна удача ещё ничего не значит (DatabaseFixture).
# Нужно 3 успеха подряд за 120 с. Использование: wait-for-db.sh <host> <port>; пароль — PGPASSWORD.
host=${1:-127.0.0.1}
port=${2:-54322}
ok=0
for _ in $(seq 1 120); do
  if psql "host=$host port=$port user=supabase_admin dbname=postgres sslmode=disable connect_timeout=3" -tAc 'select 1' >/dev/null 2>&1; then
    ok=$((ok + 1))
    [ "$ok" -ge 3 ] && echo "база готова" && exit 0
  else
    ok=0
  fi
  sleep 1
done
echo "::error::база не отвечает стабильно за 120 с"
exit 1
