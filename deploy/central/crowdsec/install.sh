#!/usr/bin/env bash
# Installs CrowdSec on the central server next to fail2ban: it reads the SSH and Caddy logs, bans by its own
# scenarios plus the community blocklist (addresses attacking other CrowdSec users), and the firewall bouncer
# drops banned addresses in INPUT (SSH) and DOCKER-USER (ports 80/443 published by Docker).
#   sudo bash deploy/central/crowdsec/install.sh
# Never banned: the addresses of fail2ban's ignoreip (/etc/fail2ban/jail.d/zz-topaz-local.conf: own IPs, stations).
# Safe to re-run.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CADDY_LOG="$(cd "$HERE/.." && pwd)/logs/caddy/access.log"
F2B_LOCAL=/etc/fail2ban/jail.d/zz-topaz-local.conf

[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }

if ! command -v cscli >/dev/null; then
  # Official repository (packagecloud), see https://docs.crowdsec.net/docs/getting_started/install_crowdsec
  curl -fsSL https://install.crowdsec.net | sh
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq crowdsec
fi
dpkg -s crowdsec-firewall-bouncer-iptables >/dev/null 2>&1 \
  || DEBIAN_FRONTEND=noninteractive apt-get install -y -qq crowdsec-firewall-bouncer-iptables

cscli hub update >/dev/null
cscli collections install crowdsecurity/linux crowdsecurity/sshd crowdsecurity/caddy \
  crowdsecurity/base-http-scenarios crowdsecurity/http-cve >/dev/null

# Caddy's JSON access log (written by the container into deploy/central/logs/caddy).
mkdir -p "$(dirname "$CADDY_LOG")"; touch "$CADDY_LOG"
cat > /etc/crowdsec/acquis.d/topaz-caddy.yaml <<YAML
filenames:
  - $CADDY_LOG
labels:
  type: caddy
YAML

# Own addresses and stations: the same list fail2ban never bans.
IPS="$(sed -n 's/^ignoreip *= *//p' "$F2B_LOCAL" 2>/dev/null | tr ' ' '\n' | grep -vE '^(127\.0\.0\.1/8|::1|172\.16\.0\.0/12)?$' || true)"
{
  echo "name: topaz/whitelist"
  echo "description: own admin addresses and stations (from $F2B_LOCAL)"
  echo "whitelist:"
  echo "  reason: topaz own addresses"
  echo "  ip: []"
  echo "  cidr:"
  echo "    - 127.0.0.0/8"
  echo "    - 172.16.0.0/12"
  for ip in $IPS; do [[ "$ip" == */* ]] && echo "    - $ip" || echo "    - $ip/32"; done
} > /etc/crowdsec/parsers/s02-enrich/topaz-whitelist.yaml

# Bans must also cover traffic Docker forwards to Caddy.
CONF=/etc/crowdsec/bouncers/crowdsec-firewall-bouncer.yaml
if ! grep -qE '^\s*-\s*DOCKER-USER' "$CONF"; then
  sed -i -E 's/^(\s*)#\s*-\s*DOCKER-USER/\1- DOCKER-USER/' "$CONF"
  grep -qE '^\s*-\s*DOCKER-USER' "$CONF" || sed -i -E '/^iptables_chains:/a\  - DOCKER-USER' "$CONF"
fi

systemctl enable --now crowdsec crowdsec-firewall-bouncer >/dev/null
systemctl restart crowdsec
systemctl restart crowdsec-firewall-bouncer
sleep 5
cscli metrics show acquisition 2>/dev/null | head -15 || true
cscli bouncers list
iptables -S DOCKER-USER | grep -i crowdsec || echo "WARNING: no CrowdSec rule in DOCKER-USER" >&2
