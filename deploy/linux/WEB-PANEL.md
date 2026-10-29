# Greatness Bot — веб-админка

Сайт с админ-панелью работает внутри бота (тот же процесс, те же данные).
Бот слушает только `127.0.0.1:5080`, наружу сайт отдаёт **Caddy** с бесплатным HTTPS (Let's Encrypt).
Вход — через Discord. Владелец бота (`Bot:OwnerId` в `appsettings.json`) видит всё, остальным он выдаёт
доступ в разделе **Access** — по людям и по разделам.

Всё бесплатно: поддомен DuckDNS, сертификат, Caddy.

Разделы панели: **Overview**, **LFG** (активности, панель, активные группы), **Tickets** (проекты, роли, категория,
текст тикета, открытые тикеты), **Temp Voice** (каналы «join to create», панель, активные комнаты),
**Panel texts** (заголовок, текст и подписи кнопок панелей LFG / Tickets / Room controls — уже опубликованные
панели обновляются при сохранении), **Access** (только владелец: кому и какие разделы открыты).

### Доступ (раздел Access)

- Открыт только владельцу бота, выдать его нельзя.
- Человека можно выбрать из участников сервера или вставить его Discord ID.
- Разделы: LFG, Tickets, Temp Voice, Panel texts, Posts — галочками. Overview видят все, у кого есть доступ.
- Права проверяются на каждый запрос: снятие доступа действует сразу, перелогиниваться не нужно.
- Хранится в `data/web-access.json`.

### Сообщения бота (раздел Posts)

- **Panels** — все известные боту панели (LFG / Tickets / Room controls): ссылка, обновить текущим текстом, удалить.
- **Bot messages in a channel** — последние 50–500 сообщений бота в выбранном канале с типом
  (панель, LFG-сбор, тикет, прочее). Удаление по одному или галочками.
- Удаление поста LFG-сбора закрывает сбор (как Force close). Удалённая панель забывается.
- Право выдаётся отдельно: **Posts**.

### Тексты панелей (раздел Panel texts)

- Пустое поле или текст по умолчанию = стандартный текст. **Reset to default** возвращает всё.
- В тексте LFG-панели `{hours}` заменяется на время автоудаления сборов.
- Бот запоминает панели, опубликованные после обновления (через сайт, `/admin` или команды).
  Панели, опубликованные раньше, найди один раз кнопкой **Find posted panels**
  (проверяются последние 50 сообщений каждого канала).
- Хранится в `data/panels.json`.

## 1. Бесплатный домен (DuckDNS)

1. Зайди на <https://www.duckdns.org> и войди (через Discord, GitHub или Google).
2. В поле **sub domain** введи имя, например `greatness-bot`, нажми **add domain**.
3. В строке домена в поле **current ip** впиши **IP своего VPS** и нажми **update ip**.

Адрес панели будет `https://greatness-bot.duckdns.org`. IP у VPS постоянный, обновлять его не нужно.

Проверка с ПК (PowerShell): `nslookup greatness-bot.duckdns.org` должен вернуть IP сервера.

## 2. Discord Developer Portal

<https://discord.com/developers/applications> → **Greatness Bot** → **OAuth2**:

1. **Client Secret** → **Reset Secret** → скопируй (понадобится на шаге 4). Токен бота при этом не меняется.
2. **Redirects** → **Add Redirect** → `https://greatness-bot.duckdns.org/signin-discord` → **Save Changes**.

## 3. Порты 80 и 443

Caddy нужен доступ к портам **80** и **443** (на 80 Let's Encrypt проверяет домен, на 443 работает сайт).
Если в панели TENET есть файрвол — открой там TCP 80 и 443. `ufw` на сервере скрипт откроет сам.

## 4. Обновить бота и включить панель

```bash
# обновить бота из git (как обычно)
bash /opt/GrtnsBot/deploy/linux/server-build.sh

# один раз: Caddy + Client Secret + HTTPS
sudo bash /opt/GrtnsBot/deploy/linux/setup-web.sh
```

`setup-web.sh` спросит домен и Client Secret (ввод скрыт), поставит Caddy, пропишет домен,
откроет порты в `ufw`, перезапустит бота и покажет Redirect-адрес для Developer Portal.

Секрет хранится в `/etc/greatnessbot.env` (строка `Web__ClientSecret=…`), как и токен бота. В git его нет.

Открой `https://greatness-bot.duckdns.org` → **Login with Discord**.

## Проверка и проблемы

| Симптом | Что делать |
| --- | --- |
| Сайт не открывается | `systemctl status caddy`, `journalctl -u caddy -n 50`. Чаще всего закрыты порты 80/443 или домен не указывает на IP сервера |
| «Discord login is not configured» | Нет `Web__ClientSecret` в `/etc/greatnessbot.env` — запусти `setup-web.sh` ещё раз |
| Discord пишет `Invalid OAuth2 redirect_uri` | В Developer Portal → OAuth2 → Redirects нет точного адреса `https://<домен>/signin-discord` |
| После входа «No access» | У аккаунта нет доступа — владелец выдаёт его в разделе Access |
| Бот не стартует, в логе `address already in use` | Порт 5080 занят — поменяй `Web:Urls` в `appsettings.json` и порт в `/etc/caddy/Caddyfile` |

Панель изнутри сервера: `curl -I http://127.0.0.1:5080/` → `200 OK`.

## Запуск на своём ПК (для теста)

1. В Developer Portal добавь ещё один Redirect: `http://localhost:5080/signin-discord`.
2. В папке проекта: `dotnet user-secrets set "Web:ClientSecret" "<секрет>"`.
3. Запусти бота и открой <http://localhost:5080>.

Не запускай бота на ПК, пока он работает на сервере: два экземпляра с одним токеном конфликтуют.

## Безопасность

- Панель слушает только `127.0.0.1`, напрямую из интернета к ней не попасть — только через Caddy (HTTPS).
- Вход только через Discord, пускаются владелец и те, кому он выдал доступ (по разделам). Cookie: HttpOnly, Secure, SameSite=Lax, 7 дней.
- Изменяющие запросы требуют заголовок `X-Panel` (защита от CSRF), страница отдаётся со строгим CSP.
- Ключи cookie лежат в `/opt/greatnessbot/data/web-keys` — после перезапуска бота входить заново не нужно.
  Удалить папку = разлогинить всех.
