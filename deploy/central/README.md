# Центральный сервис: установка

Нужно: Ubuntu 22.04/24.04, Docker Engine 24+ с плагином compose, DNS-запись `A` домена на сервер, открытые порты 80 и 443.

**Быстрый способ** — одна команда. Скрипт спросит только почту для сертификата и пароль владельца (логин `admin`), сам сгенерирует остальные секреты в `.env`, скачает готовый образ, запустит сервис, и проверит HTTPS. АЗС подключаются без ключей: по заявке, которую одобряет суперадминистратор (раздел «АЗС → Подключение»). Существующий `.env` он не перезаписывает, так что повторный запуск безопасен (так же обновляется версия после `git pull`).

```bash
git clone https://github.com/digdaran/topaz-center.git   # если ещё не скачан
cd ~/topaz-center && git pull && bash deploy/central/setup.sh   # домен по умолчанию topaz.08u.ru; другой: setup.sh my.domain.ru
```

**Вручную:**

```bash
git clone https://github.com/digdaran/topaz-center.git && cd topaz-center/deploy/central
cp .env.example .env
# заполните .env: DOMAIN, ACME_EMAIL и два секрета, например:
#   POSTGRES_PASSWORD=$(openssl rand -base64 32)
#   TOPAZ_ADMIN_TOKEN=$(openssl rand -hex 32)
chmod 600 .env
docker compose up -d --build          # или CENTRAL_IMAGE=ghcr.io/<owner>/topaz-central:<версия> без --build
docker compose ps
curl https://$DOMAIN/health/live      # 200
```

При первом запуске Caddy сам получит сертификат Let's Encrypt, а сервис создаст схему БД.

**Веб-интерфейс** открывается по `https://$DOMAIN/`. Первый владелец создаётся из `TOPAZ_BOOTSTRAP_OWNER_LOGIN/PASSWORD` в `.env` (только пока пользователей нет; после первого входа удалите пароль из `.env`) или командой:

```bash
docker compose exec -it central dotnet Topaz.Central.dll create-user --login admin --role superadmin
```

Для уведомлений в Telegram впишите токен бота в `TOPAZ_TELEGRAM_BOT_TOKEN` и выполните `docker compose up -d`, затем добавьте чаты в разделе «Контроль → Уведомления». Подробности — в [docs/web.md](../../docs/web.md).

## Работа

```bash
ADMIN="Authorization: Bearer $TOPAZ_ADMIN_TOKEN"
curl -H "$ADMIN" https://$DOMAIN/api/v1/admin/stations                 # АЗС и их состояние
curl -H "$ADMIN" https://$DOMAIN/api/v1/admin/batches?status=failed    # пакеты с ошибкой обработки
curl -H "$ADMIN" -X POST https://$DOMAIN/api/v1/admin/batches/123/reprocess
curl -H "$ADMIN" -X POST https://$DOMAIN/api/v1/admin/stations/<id>/reset    # перед переустановкой агента
curl -H "$ADMIN" "https://$DOMAIN/api/v1/export/flOrders?format=csv&updated_since=2026-10-01T00:00:00Z" -o orders.csv

docker compose logs -f central        # JSON-логи
tail -f logs/caddy/access.log         # журнал HTTP-запросов (Caddy, JSON)
docker compose exec postgres psql -U topaz topaz -c "select * from v_station_status"
```

## Мониторинг

`docker compose --profile monitoring up -d` поднимает Prometheus на `127.0.0.1:9090` (открывать через `ssh -L 9090:localhost:9090`) с алертами из `monitoring/alerts.yml`: АЗС молчит, растёт очередь агента, есть dead-letter, обработка упала или отстаёт. Чтобы алерты приходили в Telegram или на почту, добавьте Alertmanager.

## Fail2Ban

Банит IP за подбор пароля по SSH и за атаки на 80/443 (по журналу Caddy `logs/caddy/access.log`). Ставится на хост:

```bash
sudo bash fail2ban/install.sh <свой IP> <IP станций> ...   # эти адреса не банятся никогда
```

| Джейл | За что | Бан |
|---|---|---|
| `sshd` | 5 неудачных входов по SSH за 10 мин | 1 ч, растёт при повторах до недели |
| `topaz-web-probe` | 3 запроса за час к «приманкам» сканеров: `/.env`, `/.git/`, `*.php`, `wp-*`… | сутки |
| `topaz-web-login` | 10 неудачных входов в веб-интерфейс за 10 мин | 1 ч |
| `topaz-web-4xx` | 60 ответов 4xx за 10 мин | 1 ч |
| `recidive` | 5 банов за сутки в любых джейлах | неделя, на все порты |

Запросы агентов (User-Agent `TopazAgent/…`) не считаются, `/api/v1/*` не бывает «приманкой». Порты 80/443 публикует Docker, поэтому веб-баны стоят в цепочке `DOCKER-USER` (ufw её не видит), SSH-баны — в `INPUT`.

```bash
fail2ban-client status                       # джейлы
fail2ban-client status topaz-web-probe       # кто сейчас в бане
fail2ban-client set sshd unbanip 1.2.3.4     # разбанить
iptables -S DOCKER-USER                      # веб-баны в фаерволе
```

Новая станция или сменился свой IP — добавьте адрес в `ignoreip` в `/etc/fail2ban/jail.d/zz-topaz-local.conf` и выполните `fail2ban-client reload`. Станция в бане перестаёт присылать данные (в интерфейсе она станет «молчащей»): разбаньте её IP и добавьте в исключения.

## CrowdSec

Дополнительно к fail2ban: `sudo bash crowdsec/install.sh`. CrowdSec разбирает логи SSH и Caddy своими сценариями (сканеры, перебор, известные CVE) и подтягивает общий список атакующих адресов от сообщества; баны ставит firewall-bouncer в цепочки INPUT и DOCKER-USER. Адреса из `ignoreip` fail2ban переносятся в белый список (`/etc/crowdsec/parsers/s02-enrich/topaz-whitelist.yaml`). Добавили станцию или свой IP — допишите его в оба файла и перезапустите скрипт.

```bash
cscli decisions list                 # кто сейчас в бане
cscli decisions delete --ip 1.2.3.4  # разбанить
cscli metrics                        # что читается и сколько срабатываний
```

## Бэкапы

Контейнер `backup` раз в сутки делает `pg_dump` в `./backups` и хранит три последних (`TOPAZ_BACKUP_KEEP` в `.env`). Перед дампом лишние старые удаляются, и если места всё равно не хватает, бэкап пропускается, а не заполняет диск (`docker compose logs backup`). Копируйте эту папку за пределы сервера. Восстановление:

```bash
docker compose stop central
docker compose exec -T postgres pg_restore -U topaz -d topaz --clean --if-exists < backups/topaz-YYYYMMDD-HHMM.dump
docker compose start central
```

**Копия вне сервера.** Бэкапы на том же диске не спасут при потере сервера или взломе. Раз в сутки копируйте их в другое место, например в S3-совместимое хранилище (Yandex Object Storage, Selectel), с шифрованием:

```bash
apt-get install -y rclone age && rclone config            # хранилище "s3backup"
age-keygen -o /root/backup.key                            # ключ храните ВНЕ сервера, на сервере нужен только публичный
# /etc/cron.d/topaz-offsite:
# 30 6 * * * root f=$(ls -1t /root/topaz-center/deploy/central/backups/topaz-*.dump | head -1); age -r <публичный ключ> "$f" | rclone rcat s3backup:topaz/$(basename "$f").age
```

Даже если центральная БД потеряна безвозвратно, данные можно собрать заново: на каждой АЗС выполнить `TopazAgent.exe` с очищенным состоянием (`TopazAgent.exe uninstall /removestate`, затем установщик заново и одобрение заявки как «существующая АЗС»), и агенты перечитают всё из Топаза.

## Доступ администратора

- SSH: только по ключу (`ssh/11-topaz-hardening.conf`, ставит `setup.sh`: root по ключу, без паролей и форвардинга, 3 попытки).
- API администратора и выгрузки (`/api/v1/admin/*`, `/api/v1/export/*`, токен `TOPAZ_ADMIN_TOKEN`) Caddy пропускает только с адресов из `TOPAZ_ADMIN_ALLOW_IPS`; без него всем отвечает 403. После изменения выполните `docker compose up -d caddy`.
- Смена токена: `openssl rand -hex 32` → `TOPAZ_ADMIN_TOKEN` в `.env` → `docker compose up -d central`.

## Двухфакторный вход

Для суперадминистратора обязателен: после обновления до 1.8 он при первом входе попадает на экран включения (приложение Яндекс Ключ / Google Authenticator). Потерян телефон и запасные коды:

```bash
docker compose exec central dotnet Topaz.Central.dll reset-totp --login admin
```

Секреты кодов зашифрованы ключами из тома `keys`; бэкап базы их не содержит. Сохраните копию тома (`docker run --rm -v topaz_keys:/k -v $PWD:/b alpine tar czf /b/keys.tgz -C /k .`) вместе с бэкапами; без неё после переезда все сессии и коды придётся сбросить командой выше.

## Сторож сервера

`setup.sh` ставит systemd-таймер `topaz-watchdog.timer`: раз в 10 минут `watchdog.sh` проверяет заполнение диска (порог `TOPAZ_WATCHDOG_DISK_PCT`, 85%), здоровье PostgreSQL и контейнеров, ответ `https://<домен>/health/live` и свежесть бэкапа (не старше 36 ч). Проблемы пишутся в `journalctl -t topaz-watchdog` и, если в `.env` задан `TOPAZ_WATCHDOG_TELEGRAM_CHAT`, приходят в Telegram ботом `TOPAZ_TELEGRAM_BOT_TOKEN`: при появлении, раз в 6 часов, пока не исправлено, и когда всё снова в порядке. Проверка отправки: `bash watchdog.sh --test`.

У всех контейнеров ротация логов (5 × 20 МБ). Без неё Postgres, упавший в цикл перезапусков, однажды заполнил диск своим логом. `setup.sh` при обновлении оставляет только текущий и предыдущий образ central.

## Обновление

```bash
git pull && docker compose up -d --build     # миграции применятся при старте
```
