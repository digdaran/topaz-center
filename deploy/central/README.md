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

## Бэкапы

Контейнер `backup` раз в сутки делает `pg_dump` в `./backups` и хранит 14 дней. Копируйте эту папку за пределы сервера. Восстановление:

```bash
docker compose stop central
docker compose exec -T postgres pg_restore -U topaz -d topaz --clean --if-exists < backups/topaz-YYYYMMDD-HHMM.dump
docker compose start central
```

Даже если центральная БД потеряна безвозвратно, данные можно собрать заново: на каждой АЗС выполнить `TopazAgent.exe` с очищенным состоянием (`TopazAgent.exe uninstall /removestate`, затем установщик заново и одобрение заявки как «существующая АЗС»), и агенты перечитают всё из Топаза.

## Обновление

```bash
git pull && docker compose up -d --build     # миграции применятся при старте
```
