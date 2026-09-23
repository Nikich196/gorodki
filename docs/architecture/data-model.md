# Модель данных (схема v1)

PostgreSQL 17 + PostGIS 3.3 на Supabase. Все таблицы — в схеме `app`, PostGIS — в схеме `extensions`.
Код — `backend/src/Gorodki.Api/Infrastructure/Persistence`, миграции — там же в `Migrations/`.
Правила игры в базе не живут: они в `Gorodki.Domain` (ADR 0003), таблицы только хранят результат.

```mermaid
erDiagram
    users ||--o{ runs : "бегает"
    users ||--o{ parcels : "владеет"
    game_configs ||--o{ runs : "правила забега"
    runs ||--o{ run_chunks : "точки кусками"
    runs ||--o{ captures : "петли"
    invites ||--o{ users : "по коду"
    users ||--o{ refresh_tokens : "входы"
    users {
        uuid id PK
        text google_subject UK
        text display_name
        smallint color_index "0..11"
        smallint role "игрок, демо, админ"
        bool public_profile "согласие показывать"
    }
    parcels {
        bigint id PK
        smallint league "1 бег, 2 вело"
        int tile_x "UTM, км"
        int tile_y "UTM, км"
        smallint level "1..3"
        geometry geometry "Polygon, EPSG:32634"
    }
    captures {
        uuid id PK "UUIDv5 забег+точка"
        int start_seq
        int end_seq
        geometry shape
    }
    run_chunks {
        uuid run_id PK
        int first_seq PK
        bytea content_hash UK
        bytea points
    }
```

| Таблица | Зачем | Главные правила |
|---|---|---|
| `users` | Игроки | Ник уникален без учёта регистра; 16+ и согласие — отдельными полями (закон 99-З) |
| `invites` | Инвайт-коды закрытой регистрации | Приглашение забирается одним `UPDATE` с условием — лишних не израсходовать даже при гонке |
| `refresh_tokens` | Токены входа ([вход](auth.md)) | Хранится только SHA-256; `family_id` — все токены одного входа, `replaced_by_id` — чем заменён |
| `game_configs` | Версии игрового конфига (все числа правил) | Забег проверяется той версией, с которой начат. Новая база сама получает версию 1 из кода при первом `GET /config`; её JSON — [контракт](../../contracts/README.md) |
| `runs` | Забеги ([приём](runs.md)) | `league` с первого дня (D16); `processed_seq` — до какой точки обработан; **один активный на игрока** (уникальный индекс); сдвиг часов телефона, установка, версия приложения — для доверия; счётчики кусков и байт — для лимитов |
| `run_chunks` | Точки забега кусками | Повтор того же содержимого не сохраняется (уникальный хэш); **куски одного забега не пересекаются** по номерам (`EXCLUDE` с `btree_gist`) |
| `parcels` | Куски земли | Один простой многоугольник внутри одного тайла 1×1 км; **база сама отвергает неправильную геометрию** (`st_isvalid`); GiST-индекс |
| `captures` | Заявки петель и их итог | Идентификатор UUIDv5 — повтор заявки не применяется дважды |
| `tile_versions` | Версии тайлов | Клиенты перезапрашивают только изменившиеся тайлы |

Следующие таблицы (устройства, кланы, лента, туман, фишки, тайники…) добавляются миграциями по ходу этапов (PLAN.md, §7.3).

## Проверка

- `tests/Gorodki.IntegrationTests` — на образе `supabase/postgres:17.6.1.175` (тот же PostgreSQL и PostGIS, что на Supabase):
  миграции применяются, PostGIS — 3.3, геометрия возвращается из базы точно, «бабочку» база не принимает, дубль куска точек не сохраняется.
- `PostgisCompatibilityTests` — в коде и миграциях нет функций PostGIS 3.4+, которых нет на Supabase.
- `MigrationsTests` — у каждой правки модели есть миграция (иначе тест падает, база не нужна).

## Особенности

- Плагин NetTopologySuite сам добавляет в миграцию `CREATE EXTENSION IF NOT EXISTS postgis` без схемы. После нашей строки
  `... SCHEMA extensions` она ничего не делает — это нормально.
- Путь поиска `app,extensions,public` подставляется автоматически, если его нет в строке подключения (`AppDbContext.WithSearchPath`):
  без схемы `extensions` PostgreSQL не найдёт тип `geometry`.
