#!/usr/bin/env bash
# Installs fail2ban on the central server: SSH + web (Caddy access log).
#   sudo bash deploy/central/fail2ban/install.sh [IP ...]
# IPs/networks given as arguments are never banned (own addresses, stations);
# they are written once to /etc/fail2ban/jail.d/zz-topaz-local.conf (edit it
# later by hand, then `fail2ban-client reload`). Safe to re-run.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CADDY_LOG="$(cd "$HERE/.." && pwd)/logs/caddy/access.log"
LOCAL=/etc/fail2ban/jail.d/zz-topaz-local.conf

[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }

if ! command -v fail2ban-client >/dev/null; then
  DEBIAN_FRONTEND=noninteractive apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq fail2ban
fi

# The jails need the log file to exist; Caddy creates it on its first request.
mkdir -p "$(dirname "$CADDY_LOG")"
touch "$CADDY_LOG"

install -m 0644 "$HERE"/filter.d/*.conf /etc/fail2ban/filter.d/
sed "s|@CADDY_LOG@|$CADDY_LOG|g" "$HERE/jail.d/topaz.conf" > /etc/fail2ban/jail.d/topaz.conf
chmod 0644 /etc/fail2ban/jail.d/topaz.conf

if [ ! -f "$LOCAL" ]; then
  cat > "$LOCAL" <<CONF
# Never banned: localhost, Docker networks (IPv6 clients reach Caddy through the
# docker proxy with such an address), own admin addresses and station IPs.
[DEFAULT]
ignoreip = 127.0.0.1/8 ::1 172.16.0.0/12 $*
CONF
  echo "created $LOCAL"
elif [ $# -gt 0 ]; then
  echo "$LOCAL exists, arguments ignored: edit it by hand" >&2
fi

fail2ban-client -t
systemctl enable --now fail2ban
systemctl restart fail2ban
sleep 3
fail2ban-client status
