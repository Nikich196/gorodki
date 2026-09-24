# Городки

[![backend](https://github.com/Nikich196/gorodki/actions/workflows/backend.yml/badge.svg)](https://github.com/Nikich196/gorodki/actions/workflows/backend.yml)
[![ios-core](https://github.com/Nikich196/gorodki/actions/workflows/ios-core.yml/badge.svg)](https://github.com/Nikich196/gorodki/actions/workflows/ios-core.yml)
[![ios](https://github.com/Nikich196/gorodki/actions/workflows/ios.yml/badge.svg)](https://github.com/Nikich196/gorodki/actions/workflows/ios.yml)

**Спортивная iOS-игра о захвате Бреста.** Обеги участок — и он твой, ровно по контуру твоего следа.
Отбивай чужую землю, укрепляй свою, открывай город из тумана, ищи спрятанные тайники и соревнуйся с друзьями.

> Статус: **этап 0 — старт проекта** (сентябрь 2026). Готов каркас сервера и iOS-приложения, идёт проверка установки на iPhone.
> Семестровый проект по мобильной разработке, БрГТУ. Показ — конец декабря 2026.

## Что будет в игре

- **Захват** — замкнул петлю пешком или бегом, и участок внутри становится твоим. Чужая земля ослабевает и переходит к тебе.
- **Исследование** — весь город под туманом, он рассеивается там, где ты прошёл. Рейтинг «кто открыл больше Бреста».
- **Фишки и тайники** — полезные находки на маршруте и спрятанные значки разной редкости.
- **Кланы, дуэли 1×1, «Короли участков»** — соревнование с друзьями и одногруппниками.
- **Честная игра** — засчитываются только ходьба, бег и (в своей лиге) велосипед. Машина — никогда.

## Стек

| Часть | Технологии |
|---|---|
| iOS-приложение | Swift 6, SwiftUI, iOS 26+ (дизайн iOS 27 — Liquid Glass), MapKit, Core Location, ActivityKit |
| Сервер | C#, ASP.NET Core 10, EF Core, PostgreSQL + PostGIS, SignalR; фоновые задачи — свой обработчик (Hangfire — решение в #54) |
| Инфраструктура | GitHub Actions, Render, Supabase, GitHub Pages |

## Документация

- [План проекта](docs/PLAN.md) — правила игры, архитектура, этапы.
- [Журнал работ](docs/JOURNAL.md) — что сделано и что дальше.
- [Контракты сервера и телефона](contracts/README.md) — игровой конфиг, который проверяют обе стороны.
- [Как мы работаем](CONTRIBUTING.md) — ветки, коммиты, задачи, безопасность.
- [Установка на iPhone без Mac](docs/guides/install-on-iphone.md) · [Сервер на Windows](docs/guides/getting-started-windows.md)

## Команда

- **Никита** — продукт, дизайн, iOS
- **Одногруппник** — сервер на C#

---

© 2026 Авторы проекта «Городки». Все права защищены.
