# Вход через Google: Client ID для приложения

Для Никиты (issue [#4](https://github.com/Nikich196/gorodki/issues/4)). Код входа готов (08.10, без SDK —
[ios-app.md, «Вход»](../architecture/ios-app.md#вход)); не хватает только Client ID из Google Cloud — его создаёт владелец
аккаунта Google. Займёт 5–10 минут, бесплатно.

## Шаги

1. Открой https://console.cloud.google.com под своим Google-аккаунтом → вверху выбор проекта → **New project** →
   имя `Gorodki` → **Create**.
2. Меню → **Google Auth Platform** → **Get started** (экран согласия):
   - App name — `Городки`, User support email — твоя почта;
   - Audience — **External**;
   - Contact information — твоя почта → **Create**.
3. **Audience** → **Test users** → **Add users**: твоя почта и почта Егора (и всех, кто будет играть до показа, до 100).
   Пока приложение в режиме **Testing**, войти могут только они. Перевести в **In production** — этап 3a плана; приложение
   просит только `openid`, поэтому проверка Google для этого не нужна.
4. **Clients** → **Create client**:
   - Application type — **iOS**;
   - Name — `Gorodki Free`;
   - Bundle ID — `io.github.nikich196.gorodki.dev` (сборка Free; у Paid будет `io.github.nikich196.gorodki` — второй
     клиент, когда появится платный аккаунт);
   - Team ID — можно оставить пустым → **Create**.
5. Скопируй **Client ID** (вид `123456789012-abc….apps.googleusercontent.com`) и пришли Claude. Это не секрет — он
   лежит в любой сборке приложения. **Client secret** у iOS-клиента нет и не нужен.

## Что сделает Claude

- Впишет Client ID в `ios/Config/Free.xcconfig` (`GORODKI_GOOGLE_CLIENT_ID`) — кнопка «Войти через Google» включится.
- Попросит тебя (или сам, если будет доступ) добавить его в Render: `Auth__GoogleClientIds__0`
  ([deploy-render.md](deploy-render.md)) — без этого сервер на вход отвечает 503.

## Если что-то не так

- «Доступ заблокирован: приложение не прошло проверку» — твоей почты нет в Test users (шаг 3).
- «redirect_uri_mismatch» — Client ID не от iOS-клиента (выбран Web) или bundle ID другой: создай клиент типа iOS.
- Окно Google открылось и сразу закрылось — нет сети или VPN режет accounts.google.com; приложение скажет «Google не
  ответил».
