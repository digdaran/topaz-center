#!/usr/bin/env bash
# Host-side watchdog of the central server, run every 10 minutes by a systemd timer (installed by setup.sh).
# Checks what the service cannot report about itself: free disk space, PostgreSQL health, the HTTPS answer
# and the age of the last backup. Problems go to syslog (journalctl -t topaz-watchdog) and, when
# TOPAZ_WATCHDOG_TELEGRAM_CHAT is set in .env (chat ids, comma-separated), to Telegram through
# TOPAZ_TELEGRAM_BOT_TOKEN. A message is sent when the set of problems changes (also "all clear")
# and repeated every 6 hours while it lasts.
#   bash deploy/central/watchdog.sh          check and notify
#   bash deploy/central/watchdog.sh --test   send a test message
set -uo pipefail
cd "$(dirname "$0")"

envval() { grep -E "^$1=" .env 2>/dev/null | tail -1 | cut -d= -f2- | sed -E "s/^['\"]//; s/['\"]$//"; }
DOMAIN="$(envval DOMAIN)"
TOKEN="$(envval TOPAZ_TELEGRAM_BOT_TOKEN)"
# Telegram through the telegram-proxy container when it is configured (published on 127.0.0.1:3128).
TG_PROXY=(); [[ -n "$(envval TOPAZ_TELEGRAM_PROXY)" ]] && TG_PROXY=(-x http://127.0.0.1:3128)
CHATS="$(envval TOPAZ_WATCHDOG_TELEGRAM_CHAT)"
DISK_PCT="$(envval TOPAZ_WATCHDOG_DISK_PCT)"; DISK_PCT="${DISK_PCT:-85}"
STATE_DIR=/var/lib/topaz-watchdog
REPEAT_SECONDS=$((6 * 3600))

notify() {
  logger -t topaz-watchdog -- "$1"
  [[ -n "$TOKEN" && -n "$CHATS" ]] || return 0
  local chat
  for chat in ${CHATS//,/ }; do
    curl -fsS -m 20 "${TG_PROXY[@]}" -o /dev/null "https://api.telegram.org/bot${TOKEN}/sendMessage" \
      --data-urlencode "chat_id=${chat}" --data-urlencode "text=$1" \
      || logger -t topaz-watchdog "telegram send to ${chat} failed"
  done
}

HOST="${DOMAIN:-$(hostname)}"
if [[ "${1:-}" == "--test" ]]; then notify "✅ ${HOST}: проверка уведомлений сервера"; exit 0; fi

problems=()

used="$(df -P / | awk 'NR==2{gsub("%","",$5); print $5}')"
avail="$(df -Ph / | awk 'NR==2{print $4}')"
(( used >= DISK_PCT )) && problems+=("диск занят на ${used}% (свободно ${avail})")

pg="$(docker inspect -f '{{.State.Health.Status}}' topaz-postgres-1 2>/dev/null || echo missing)"
[[ "$pg" == healthy ]] || problems+=("PostgreSQL: ${pg}")

for c in topaz-central-1 topaz-caddy-1 topaz-backup-1; do
  st="$(docker inspect -f '{{.State.Status}}' "$c" 2>/dev/null || echo missing)"
  [[ "$st" == running ]] || problems+=("контейнер ${c}: ${st}")
done

if [[ -n "$DOMAIN" ]]; then
  code="$(curl -s -m 20 -o /dev/null -w '%{http_code}' "https://${DOMAIN}/health/live" || true)"
  [[ "$code" == 200 ]] || problems+=("https://${DOMAIN}/health/live отвечает ${code:-ничего}")
fi

last="$(ls -1t backups/topaz-*.dump 2>/dev/null | head -1)"
if [[ -z "$last" ]]; then
  problems+=("нет ни одного бэкапа")
elif (( $(date +%s) - $(stat -c %Y "$last") > 36 * 3600 )); then
  problems+=("последний бэкап старше 36 ч: $(basename "$last")")
fi

mkdir -p "$STATE_DIR"
now="$(date +%s)"
prev="$(cat "$STATE_DIR/problems" 2>/dev/null || true)"
sent="$(cat "$STATE_DIR/sent_at" 2>/dev/null || echo 0)"
# signature without numbers: "disk 86%" -> "disk 87%" is the same problem, not a new message
cur="$(printf '%s\n' "${problems[@]+"${problems[@]}"}" | sed 's/[0-9][0-9.,]*//g')"

if (( ${#problems[@]} )); then
  if [[ "$cur" != "$prev" ]] || (( now - sent >= REPEAT_SECONDS )); then
    notify "⚠️ ${HOST}: проблемы сервера"$'\n'"$(printf -- '• %s\n' "${problems[@]}")"
    echo "$now" > "$STATE_DIR/sent_at"
  fi
elif [[ -n "$prev" ]]; then
  notify "✅ ${HOST}: сервер снова в порядке"
fi
printf '%s' "$cur" > "$STATE_DIR/problems"
