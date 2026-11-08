#!/usr/bin/env bash
# First start (or update) of the central service in one command:
#   bash deploy/central/setup.sh [domain]          (default domain: topaz.08u.ru)
# Creates .env with generated secrets (asks only for an e-mail and the owner password),
# starts the prebuilt image from GHCR and waits until https://<domain>/health/live answers.
# An existing .env is never overwritten: secrets stay, only the image version is updated.
set -euo pipefail
umask 077   # .env holds secrets
cd "$(dirname "$0")"

DOMAIN="${1:-topaz.08u.ru}"
say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m! %s\033[0m\n' "$*"; }
die() { printf '\033[31mОшибка: %s\033[0m\n' "$*" >&2; exit 1; }

# --- prerequisites ---------------------------------------------------------
command -v docker >/dev/null || die "Docker не установлен: curl -fsSL https://get.docker.com | sh"
if docker compose version >/dev/null 2>&1; then DC=(docker compose)
elif command -v docker-compose >/dev/null; then DC=(docker-compose)
else die "нет docker compose: apt-get install -y docker-compose-plugin"; fi
command -v openssl >/dev/null || die "нет openssl: apt-get install -y openssl"

# Image version = the latest release tag of this checkout (falls back to latest).
VERSION="$(git describe --tags --abbrev=0 2>/dev/null || true)"; VERSION="${VERSION#v}"
IMAGE="ghcr.io/digdaran/topaz-central:${VERSION:-latest}"

# --- .env --------------------------------------------------------------------
set_env() { # set_env KEY VALUE  (value is written single-quoted, i.e. literally)
  KEY="$1" VAL="$2" awk 'BEGIN{k=ENVIRON["KEY"]; v=ENVIRON["VAL"]; done=0}
    index($0, k"=")==1 {print k"='\''"v"'\''"; done=1; next} {print}
    END{if(!done) print k"='\''"v"'\''"}' .env > .env.tmp
  cat .env.tmp > .env && rm -f .env.tmp   # keep the file's 600 mode
}

if [[ -f .env ]]; then
  say "Файл .env уже есть — секреты не меняю, обновляю только образ ($IMAGE)."
  set_env CENTRAL_IMAGE "$IMAGE"
  NEW=0
else
  say "Настройка сервера $DOMAIN"
  while :; do
    read -rp "Почта для сертификата Let's Encrypt: " EMAIL
    [[ "$EMAIL" =~ ^[^@[:space:]\']+@[^@[:space:]\']+\.[^@[:space:]\']+$ ]] && break
    echo "Похоже, это не адрес почты, попробуйте ещё раз."
  done
  while :; do
    read -rsp "Пароль владельца веб-интерфейса (логин admin, не короче 10 символов): " P1; echo
    if (( ${#P1} < 10 )); then echo "Слишком короткий."; continue; fi
    if [[ "$P1" == *"'"* ]]; then echo "Без символа ' пожалуйста."; continue; fi
    read -rsp "Повторите пароль: " P2; echo
    [[ "$P1" == "$P2" ]] && break
    echo "Пароли не совпали."
  done
  cp .env.example .env
  chmod 600 .env
  set_env DOMAIN "$DOMAIN"
  set_env ACME_EMAIL "$EMAIL"
  set_env POSTGRES_PASSWORD "$(openssl rand -hex 24)"
  set_env TOPAZ_ADMIN_TOKEN "$(openssl rand -hex 32)"
  set_env TOPAZ_BOOTSTRAP_OWNER_LOGIN admin
  set_env TOPAZ_BOOTSTRAP_OWNER_PASSWORD "$P1"
  set_env CENTRAL_IMAGE "$IMAGE"
  unset P1 P2
  NEW=1
  echo "Создан .env (права 600), секреты сгенерированы."
fi
chmod 600 .env
DOMAIN="$(grep -E '^DOMAIN=' .env | head -1 | cut -d= -f2- | tr -d "'\"")"

# --- network checks (warnings only) ----------------------------------------
MYIP="$(curl -s -4 -m 8 https://ifconfig.me || true)"
DNSIP="$(getent ahostsv4 "$DOMAIN" | awk 'NR==1{print $1}' || true)"
if [[ -z "$DNSIP" ]]; then warn "$DOMAIN не находится в DNS — создайте A-запись на ${MYIP:-IP этого сервера}."
elif [[ -n "$MYIP" && "$DNSIP" != "$MYIP" ]]; then warn "$DOMAIN указывает на $DNSIP, а у этого сервера IP $MYIP — сертификат не выпустится."
fi
BUSY="$(ss -ltnpH 2>/dev/null | awk '$4 ~ /:(80|443)$/' | grep -v -E 'docker-proxy|caddy' || true)"
[[ -n "$BUSY" ]] && warn "порты 80/443 уже заняты другой программой:"$'\n'"$BUSY"
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw status | grep -qE '^(80|443)(/tcp)?\s+ALLOW' || { warn "ufw включён — открываю 80 и 443"; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null; }
fi

# --- start -------------------------------------------------------------------
say "Скачиваю образы и запускаю"
"${DC[@]}" pull
"${DC[@]}" up -d --no-build --remove-orphans

# Old central images pile up with every release (~600 MB each): keep the current and the previous one.
docker images --format '{{.Repository}}:{{.Tag}}' ghcr.io/digdaran/topaz-central | grep -vx "$IMAGE" \
  | sort -V | head -n -1 | xargs -r docker rmi >/dev/null 2>&1 || true
docker image prune -f >/dev/null 2>&1 || true

# SSH hardening (key-only root, no forwarding); applied only if sshd accepts the result.
if [[ -d /etc/ssh/sshd_config.d ]] && command -v sshd >/dev/null; then
  install -m 0644 ssh/11-topaz-hardening.conf /etc/ssh/sshd_config.d/11-topaz-hardening.conf
  if sshd -t; then systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
  else rm -f /etc/ssh/sshd_config.d/11-topaz-hardening.conf; warn "sshd отверг 11-topaz-hardening.conf, оставил как было"; fi
fi

# Watchdog (disk, PostgreSQL, HTTPS, backups) every 10 minutes: see watchdog.sh.
if command -v systemctl >/dev/null; then
  cat > /etc/systemd/system/topaz-watchdog.service <<UNIT
[Unit]
Description=Topaz central server watchdog
[Service]
Type=oneshot
ExecStart=/usr/bin/env bash $PWD/watchdog.sh
UNIT
  cat > /etc/systemd/system/topaz-watchdog.timer <<'UNIT'
[Unit]
Description=Topaz central server watchdog every 10 minutes
[Timer]
OnBootSec=5min
OnUnitActiveSec=10min
[Install]
WantedBy=timers.target
UNIT
  systemctl daemon-reload && systemctl enable --now topaz-watchdog.timer >/dev/null 2>&1 \
    || warn "не удалось включить topaz-watchdog.timer"
  grep -qE "^TOPAZ_WATCHDOG_TELEGRAM_CHAT=.+" .env \
    || warn "уведомления о проблемах сервера (диск, база, бэкапы) не уходят в Telegram: задайте TOPAZ_WATCHDOG_TELEGRAM_CHAT в .env"
fi

say "Жду, пока сервис ответит на https://$DOMAIN/health/live (до 3 минут)"
ok=0
for _ in $(seq 1 36); do
  if curl -fsS -m 5 "https://$DOMAIN/health/live" >/dev/null 2>&1; then ok=1; break; fi
  sleep 5; printf '.'
done
echo
"${DC[@]}" ps

if (( ok )); then
  say "Готово: https://$DOMAIN/"
  (( NEW )) && echo "Вход: admin и пароль, который вы ввели. После первого входа можно удалить TOPAZ_BOOTSTRAP_OWNER_PASSWORD из .env."
  echo "Агенты на АЗС ставятся без ключа: после установки АЗС появится в «АЗС → Подключение»,"
  echo "одобрите её, сверив код подтверждения с тем, кто устанавливал агент."
else
  warn "сервис пока не отвечает по HTTPS. Последние строки логов:"
  "${DC[@]}" logs --tail 30 caddy central || true
  echo "Частые причины: DNS ещё не обновился или закрыт порт 80 у хостера. Повторный запуск скрипта безопасен."
  exit 1
fi
