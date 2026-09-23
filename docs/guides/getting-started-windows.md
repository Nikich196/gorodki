# Начало работы на Windows (сервер на C#)

Инструкция для одногруппника, чтобы запустить сервер «Городков» и тесты на своём компьютере.

## 1. Установить

```powershell
winget install Microsoft.DotNet.SDK.10   # .NET 10 SDK (нужна версия 10.0.401 или новее)
winget install Git.Git                   # если Git ещё нет
```

Среда разработки — любая из этих:
- **JetBrains Rider** (бесплатен для некоммерческого использования): `winget install JetBrains.Rider`;
- **Visual Studio Community** с нагрузкой «ASP.NET и веб-разработка» (нужна версия с поддержкой .NET 10);
- **VS Code** + расширение C# Dev Kit.

## 2. Доступ к репозиторию

1. Прими приглашение: письмо от GitHub или страница https://github.com/Nikich196/gorodki/invitations.
2. Включи двухфакторную проверку в GitHub (Settings → Password and authentication).
3. Склонируй:

```powershell
git clone https://github.com/Nikich196/gorodki.git
cd gorodki
```

## 3. Запустить тесты и сервер

```powershell
dotnet test --solution backend/Gorodki.slnx
dotnet run --project backend/src/Gorodki.Api
```

Открой http://localhost:5080/scalar — интерактивное описание API. Попробуй `GET /health`:
сервер ответит версией и текущим игровым днём по Минску.

### База данных (по желанию)

Большинство тестов базы не требуют. Интеграционные тесты (`tests/Gorodki.IntegrationTests`) запускают PostgreSQL + PostGIS
в Docker — если Docker Desktop не установлен, они просто пропускаются (в CI выполняются всегда).

Миграции:

```powershell
cd backend
dotnet tool restore                                   # ставит dotnet-ef нужной версии (из dotnet-tools.json)
dotnet ef migrations add ИмяМиграции --project src/Gorodki.Api --output-dir Infrastructure/Persistence/Migrations
dotnet ef migrations script --project src/Gorodki.Api # посмотреть SQL
```

Модель данных — [docs/architecture/data-model.md](../architecture/data-model.md).

## 4. Как устроен сервер

- `backend/src/Gorodki.Domain` — правила игры на чистом C#: время по Минску, потом геометрия участков,
  античит, очки. Без ASP.NET и базы данных, поэтому всё проверяется быстрыми тестами.
- `backend/src/Gorodki.Api` — веб-сервер: `Program.cs` собирает приложение,
  каждая возможность лежит в своей папке `Features/<Название>/` (вертикальные срезы).
- `backend/tests/*` — тесты. `Gorodki.Api.Tests` запускает весь сервер в памяти через `WebApplicationFactory`.

С чего начать чтение: `Program.cs` → `Features/Health/HealthEndpoints.cs` → `Domain/Time/GameClock.cs`
и тесты к ним.

## 5. Как сдавать работу

Правила — в [CONTRIBUTING.md](../../CONTRIBUTING.md): ветка → коммиты → Pull Request → зелёный CI → слияние.
Задачи для тебя помечены меткой [`для-друга`](https://github.com/Nikich196/gorodki/labels/для-друга).
