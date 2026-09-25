-- Два игрока для сквозного прогона — как после регистрации (AuthEndpoints.SignInWithGoogle): согласие v1
-- (AuthOptions.ConsentVersion), роль 0 = Player. Инвайт не нужен: invite_code допускает NULL, внешнего ключа нет.
-- game_configs и seasons не нужны: версию 1 заводит GameConfigStore при старте забега, сезоны засевает миграция.
-- Переменные psql: player_a, player_b (uuid). Цвета 3 и 7 — тест проверяет colorIndex своего участка.
INSERT INTO app.users (id, google_subject, display_name, normalized_name, color_index, role,
                       age_confirmed_at, consent_version, consented_at, public_profile, created_at)
VALUES (:'player_a', 'e2e:' || :'player_a', 'Бегун-E2EA', 'бегун-e2ea', 3, 0, now(), 1, now(), false, now()),
       (:'player_b', 'e2e:' || :'player_b', 'Бегун-E2EB', 'бегун-e2eb', 7, 0, now(), 1, now(), false, now());
