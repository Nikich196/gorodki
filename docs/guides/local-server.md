# Локальный сервер для проверки приложения на Mac

Для Никиты и Егора: как поднять сервер с базой на своём Mac и пройти игру в симуляторе iPhone без Render, Supabase и
Google. Нужны Xcode 27, .NET 10 (`brew install dotnet`), XcodeGen (`brew install xcodegen`) и OrbStack (Docker).

## 1. База — PostGIS 17 в OrbStack

```sh
mkdir -p ~/.gorodki-local && chmod 700 ~/.gorodki-local
openssl rand -hex 16 > ~/.gorodki-local/db-pass && chmod 600 ~/.gorodki-local/db-pass
docker run -d --platform linux/amd64 --name gorodki-db --restart unless-stopped \
  -e POSTGRES_PASSWORD="$(cat ~/.gorodki-local/db-pass)" -p 127.0.0.1:5433:5432 \
  -v gorodki-db-data:/var/lib/postgresql/data postgis/postgis:17-3.5-alpine
docker exec gorodki-db psql -U postgres -c "CREATE DATABASE gorodki TEMPLATE template0;"
```

Образ PostGIS выпущен только для x86 — OrbStack запускает его через Rosetta. Тяжёлый образ `supabase/postgres` (~1 ГБ)
локально не нужен: им пользуется только CI.

## 2. Сервер со входом тестового игрока

```sh
cd backend/src/Gorodki.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5080 \
ConnectionStrings__Gorodki="Host=127.0.0.1;Port=5433;Database=gorodki;Username=postgres;Password=$(cat ~/.gorodki-local/db-pass)" \
Auth__SigningKey="$(openssl rand -base64 32)" Auth__DevSignIn=true Database__MigrateOnStartup=true Jobs__Hangfire=false \
dotnet run --no-launch-profile
```

- `Auth__DevSignIn=true` — вход без Google: приложение присылает `dev:<имя>` вместо ID-токена (`DevGoogleTokenValidator`).
  Работает **только** в окружении Development; включённый на Render — сервер не стартует (тест `StartupTests`).
- Проверка: `curl http://127.0.0.1:5080/health` → `"status":"ok"`.
- Код приглашения для регистрации:
  `docker exec gorodki-db psql -U postgres -d gorodki -c "INSERT INTO app.invites (code, max_uses, used_count, expires_at, note, created_at) VALUES ('TEST-2026', 50, 0, now() + interval '60 days', 'локально', now());"`

## 3. Приложение в симуляторе

```sh
xcodegen generate --spec ios/project.yml
xcodebuild build -project ios/Gorodki.xcodeproj -scheme "Gorodki Free" -configuration Debug-Free \
  -destination 'generic/platform=iOS Simulator' -derivedDataPath /tmp/gorodki-dd \
  -skipPackagePluginValidation CODE_SIGNING_ALLOWED=NO 'GORODKI_SERVER_URL=http://127.0.0.1:5080'
```

Запуск с тестовым входом: аргумент `-GorodkiDevSignIn <имя>` (только Debug). Онбординг: код `TEST-2026` → 16+ →
правила → согласие → «Войти через Google» (кнопка работает без Google). Симулятор в Xcode 27 открывается через
`Xcode → Open Developer Tool → DeviceHub`.

Без сервера всё видно через **«Посмотреть демо»** на первом экране — встроенные образцы, в любой сборке.

## 4. Автотесты на живом сервере

```sh
TEST_RUNNER_GORODKI_LOCAL_SMOKE=1 xcodebuild test -project ios/Gorodki.xcodeproj -scheme "Gorodki Snapshots" \
  -configuration Debug-Free -destination 'platform=iOS Simulator,name=iPhone 18 Pro' \
  -only-testing:GorodkiUITests/LocalServerSmokeTests -skipPackagePluginValidation CODE_SIGNING_ALLOWED=NO \
  'GORODKI_SERVER_URL=http://127.0.0.1:5080'
```

`LocalServerSmokeTests` регистрируется по коду, входит и проверяет, что профиль пришёл с сервера. Без флага пропускается.

## Что так не проверить

Захват петли в симуляторе: датчиков движения там нет, а без «Движения» сервер честно отказывает каждой петле
(`motion_not_authorized`). Захват проверяется на телефоне или в демо-режиме (церемония на образцах).
