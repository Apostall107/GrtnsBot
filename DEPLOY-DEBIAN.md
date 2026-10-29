# Greatness Bot — запуск на Debian (24/7)

Бот работает на сервере как служба systemd: стартует при загрузке сервера, перезапускается при падении,
данные хранит в `/opt/greatnessbot/data`. .NET на сервер ставить не нужно — он внутри бота.

## Что понадобится

| Что | Требования |
| --- | --- |
| Сервер (VPS) | Debian 12 или 13 (Ubuntu 22.04 / 24.04 тоже подходит), 1 vCPU, 1 ГБ RAM, 10 ГБ диска |
| Доступ | SSH под root (или пользователь + `su -`), программа WinSCP для копирования файлов |
| На своём ПК | .NET 8 SDK (уже есть — ты собираешь бота) |
| Discord | Токен бота, включён **Server Members Intent** (Developer Portal → Бот) |

Тип процессора сервера: подключись по SSH и выполни `uname -m`.
`x86_64` — обычная сборка; `aarch64` — ARM-сборка (см. шаг 1).

## 1. Сборка на ПК

В папке проекта запусти:

- `publish-linux.bat` — для сервера `x86_64` (Intel/AMD, большинство VPS);
- `publish-linux.bat arm64` — для сервера `aarch64` (Hetzner CAX, Oracle Ampere).

Получится папка **`deploy-linux`**:

```
deploy-linux/
  app/                  бот (LfgBot) и appsettings.json
  data/                 текущие данные бота с ПК (используются только при первой установке)
  install.sh            первая установка
  update.sh             обновление
  backup.sh             резервная копия данных
  greatnessbot.service  описание службы
  README-DEBIAN.md      эта инструкция
```

## 2. Копирование на сервер

1. Открой WinSCP → протокол **SFTP**, IP сервера, пользователь `root`, пароль → Войти.
2. Перетащи **всю папку `deploy-linux`** в `/root/`. На сервере получится `/root/deploy-linux`.

## 3. Первая установка

1. **Выключи бота на ПК** (закрой окно `start-bot.bat`). Два экземпляра с одним токеном работать не должны.
2. Подключись по SSH (в PowerShell: `ssh root@IP_СЕРВЕРА`).
3. Выполни:

```bash
cd /root/deploy-linux
bash install.sh
```

Скрипт:

1. поставит системные библиотеки (ICU, OpenSSL, сертификаты, часовые пояса);
2. создаст системного пользователя `greatnessbot` и папку `/opt/greatnessbot`;
3. скопирует бота и данные (`data` — только если на сервере её ещё нет);
4. **спросит токен бота** — вставь его (правый клик в PowerShell) и нажми Enter, символы не отображаются;
5. зарегистрирует и запустит службу, покажет последние строки лога.

В логе должны быть `[Gateway] Ready` и `Commands registered on Greatness`.

## 4. Управление

| Задача | Команда |
| --- | --- |
| Лог в реальном времени | `journalctl -u greatnessbot -f` (выход — Ctrl+C, бот продолжает работать) |
| Последние 100 строк лога | `journalctl -u greatnessbot -n 100 --no-pager` |
| Статус | `systemctl status greatnessbot` |
| Перезапуск | `systemctl restart greatnessbot` |
| Остановить | `systemctl stop greatnessbot` |
| Запустить | `systemctl start greatnessbot` |

## 5. Обновление бота

1. На ПК: `publish-linux.bat` (или `publish-linux.bat arm64`).
2. В WinSCP загрузи новую папку `deploy-linux` в `/root/` с заменой.
3. На сервере:

```bash
cd /root/deploy-linux
bash update.sh
```

`update.sh` останавливает бота, заменяет программу и `appsettings.json`, запускает снова.
Папку `/opt/greatnessbot/data` и токен он **не трогает**: данные с ПК при обновлении не переносятся.

## 6. Резервные копии

```bash
bash /root/deploy-linux/backup.sh
```

Архив появится в `/root/greatnessbot-backups/` (хранятся последние 30). Скачать — через WinSCP.

Автоматически каждый день в 04:00: `crontab -e` → добавить строку

```
0 4 * * * bash /root/deploy-linux/backup.sh
```

Восстановление: `systemctl stop greatnessbot` → распаковать архив в `/opt/greatnessbot`
(`tar -xzf ФАЙЛ -C /opt/greatnessbot`) → `chown -R greatnessbot:greatnessbot /opt/greatnessbot` → `systemctl start greatnessbot`.

## 7. Токен и настройки

- Токен хранится в `/etc/greatnessbot.env` (виден только root). Сменить: `nano /etc/greatnessbot.env` →
  `Discord__Token=НОВЫЙ_ТОКЕН` → `systemctl restart greatnessbot`.
- Настройки сервера лучше задавать там же, а не в `appsettings.json` (его перезаписывает `update.sh`).
  Формат — ключ с `__` вместо `:`, например:

```
Discord__Token=...
Lfg__AutoDeleteHours=4
Lfg__TimeZone=Europe/Kyiv
```

## 8. Если что-то не так

| Симптом | Что сделать |
| --- | --- |
| `Exec format error` в логе | Собрана не та архитектура: пересобери `publish-linux.bat arm64` (или без `arm64`) и выполни `update.sh` |
| `Discord:Token is not set` | Проверь `/etc/greatnessbot.env`, затем `systemctl restart greatnessbot` |
| `4014 Disallowed intent(s)` | Developer Portal → Бот → включить Server Members Intent |
| `Couldn't find a valid ICU package` | `apt-get install -y libicu-dev` и перезапуск |
| `$'\r': command not found` при запуске скрипта | Скрипт сохранён с переводами строк Windows: `sed -i 's/\r$//' *.sh` и запусти снова |
| Команды срабатывают дважды | Бот запущен и на ПК, и на сервере — закрой бота на ПК |
| `Permission denied` при записи данных | `chown -R greatnessbot:greatnessbot /opt/greatnessbot` и перезапуск |

## 9. Веб-админка (сайт)

Админ-панель в браузере с HTTPS и входом через Discord — см. **`WEB-PANEL.md`** (в `deploy/linux/`).
Коротко: DuckDNS-домен → Redirect в Developer Portal → `sudo bash setup-web.sh`.

## 10. Удаление

```bash
systemctl disable --now greatnessbot
rm /etc/systemd/system/greatnessbot.service /etc/greatnessbot.env
systemctl daemon-reload
rm -rf /opt/greatnessbot      # вместе с данными! сначала сделай backup.sh
userdel greatnessbot
```
