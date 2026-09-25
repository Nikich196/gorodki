# Документация «Городков»

| Документ | Что внутри |
|---|---|
| [PLAN.md](PLAN.md) | Главный план: правила игры, архитектура, этапы, риски. Читать первым |
| [JOURNAL.md](JOURNAL.md) | Журнал работ: текущий этап, следующий шаг, блокеры, что сделано |
| [plan-deviations.md](plan-deviations.md) | Где код разошёлся с планом и почему — приняты 25.09 и перенесены в PLAN, файл — история |
| [adr/](adr/README.md) | Архитектурные решения (почему выбрано именно так) |
| [architecture/data-model.md](architecture/data-model.md) | Модель данных: таблицы, ER-диаграмма, проверки |
| [architecture/sync.md](architecture/sync.md) | Офлайн-синхронизация на телефоне: запись забега и доставка очереди |
| [architecture/realtime.md](architecture/realtime.md) | Реальное время (SignalR): подсказки «тайлы изменились» и «заявка решена», контракт для приложения |
| [architecture/ios-app.md](architecture/ios-app.md) | Приложение: клиент API, токены в Keychain, обновление при 401, адрес сервера |
| [architecture/osm-pipeline.md](architecture/osm-pipeline.md) | Конвейер OSM (проект v1): маски, «достижимая» площадь и % Бреста, граница и Арена, атрибуция ODbL |
| [decisions/osm-questions.md](decisions/osm-questions.md) | Вопросы по OSM и решения 25.09 — буферы масок, пешеходные пути, Арена, «Ничейные земли», ODbL |
| [architecture/run-hud.md](architecture/run-hud.md) | HUD забега, церемония захвата и итог: логика, что считает телефон и что сервер, решения Никиты |
| [legal/README.md](legal/README.md) | Черновики соглашения, политики и согласия (закон 99-З) — для проверки преподавателем |
| [guides/for-egor.md](guides/for-egor.md) | **Для Егора**: что готово, его задачи (#34–#41) и как их сделать, где что лежит |
| [guides/install-on-iphone.md](guides/install-on-iphone.md) | Установка на iPhone без Mac через Sideloadly |
| [guides/probe-walk.md](guides/probe-walk.md) | Пробная прогулка: спайки S1 (фоновый трекинг) и S4 (карта, туман) |
| [guides/field-test-1.md](guides/field-test-1.md) | Протокол полевого теста №1 (24–25.10): готовность, маршруты петель, отметки, калибровка, критерии |
| [guides/getting-started-windows.md](guides/getting-started-windows.md) | Запуск сервера и тестов на Windows |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Как мы работаем: ветки, коммиты, задачи, безопасность |

Будут добавлены по ходу работы (PLAN.md, §8):
- `product/` — геймдизайн с таблицей приоритета правил, покрытие требований, экраны;
- `architecture/` — обзор, iOS, сервер, геодвижок, античит, реальное время, модель данных, безопасность;
- `guides/` — старт на Mac, протокол полевого теста, чек-лист показа, включение платного аккаунта;
- `learn/` — «путь GPS-точки», «как считается захват», шпаргалка к защите;
- `report/` — пояснительная записка (LaTeX).
