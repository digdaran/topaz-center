#!/usr/bin/env python3
"""
Builds the xray config of the telegram-proxy container from a Remnawave (or any xray) subscription.

    python3 deploy/central/telegram-proxy/make-config.py            # URL from TOPAZ_TELEGRAM_PROXY_SUBSCRIPTION in .env
    python3 deploy/central/telegram-proxy/make-config.py --list     # show the servers of the subscription
    python3 deploy/central/telegram-proxy/make-config.py --pick 3,5 # only these servers (numbers from --list)

The container listens as a plain HTTP proxy on port 3128 inside Docker (and 127.0.0.1:3128 on the host for
the watchdog). By default every server of the subscription is used: xray probes api.telegram.org through
each one every few minutes and sends through the fastest one that works (a server that cannot reach
Telegram, e.g. a gateway inside Russia, drops out by itself). Only Telegram traffic is pointed at it.
Writes telegram-proxy/config.json (secret: server keys; git-ignored, mode 600). Standard library only.
"""
import base64
import json
import os
import sys
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ENV = os.path.join(HERE, "..", ".env")
OUT = os.path.join(HERE, "config.json")


def env_value(name):
    try:
        with open(ENV, encoding="utf-8") as f:
            for line in f:
                if line.startswith(name + "="):
                    return line.split("=", 1)[1].strip().strip("'\"")
    except FileNotFoundError:
        pass
    return os.environ.get(name, "")


def fetch(url):
    # Remnawave answers by client: an xray-like User-Agent gets the xray format (JSON or links).
    req = urllib.request.Request(url, headers={"User-Agent": "Xray/26 topaz-telegram-proxy"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode("utf-8", "replace").strip()


def b64(s):
    s = s.strip().replace("-", "+").replace("_", "/")
    return base64.b64decode(s + "=" * (-len(s) % 4)).decode("utf-8", "replace")


def q1(params, key, default=None):
    v = params.get(key)
    return v[0] if v else default


def stream_settings(p, host):
    """streamSettings from the query of a vless/trojan link (transport + tls/reality)."""
    net = q1(p, "type", "tcp")
    sec = q1(p, "security", "none")
    ss = {"network": net, "security": sec}
    sni = q1(p, "sni") or q1(p, "host") or host
    fp = q1(p, "fp", "chrome")
    if sec == "reality":
        ss["realitySettings"] = {"serverName": sni, "fingerprint": fp, "publicKey": q1(p, "pbk", ""),
                                 "shortId": q1(p, "sid", ""), "spiderX": q1(p, "spx", "")}
    elif sec == "tls":
        tls = {"serverName": sni, "fingerprint": fp}
        if q1(p, "alpn"):
            tls["alpn"] = q1(p, "alpn").split(",")
        ss["tlsSettings"] = tls
    path = q1(p, "path", "/")
    hhost = q1(p, "host", "")
    if net == "ws":
        ss["wsSettings"] = {"path": path, "headers": {"Host": hhost} if hhost else {}}
    elif net == "grpc":
        ss["grpcSettings"] = {"serviceName": q1(p, "serviceName", ""), "multiMode": q1(p, "mode") == "multi"}
    elif net in ("xhttp", "splithttp"):
        x = {"path": path, "mode": q1(p, "mode", "auto")}
        if hhost:
            x["host"] = hhost
        if q1(p, "extra"):
            x["extra"] = json.loads(q1(p, "extra"))
        ss["network"] = "xhttp"
        ss["xhttpSettings"] = x
    elif net == "httpupgrade":
        ss["httpupgradeSettings"] = {"path": path, "host": hhost}
    elif net == "tcp" and q1(p, "headerType") == "http":
        ss["tcpSettings"] = {"header": {"type": "http", "request": {"path": [path], "headers": {"Host": [hhost]}}}}
    return ss


def outbound_from_link(link):
    u = urllib.parse.urlsplit(link)
    p = urllib.parse.parse_qs(u.query)
    name = urllib.parse.unquote(u.fragment) or u.hostname
    if u.scheme == "vless":
        user = {"id": urllib.parse.unquote(u.username), "encryption": q1(p, "encryption", "none")}
        if q1(p, "flow"):
            user["flow"] = q1(p, "flow")
        ob = {"protocol": "vless", "settings": {"vnext": [{"address": u.hostname, "port": u.port, "users": [user]}]},
              "streamSettings": stream_settings(p, u.hostname)}
    elif u.scheme == "trojan":
        ob = {"protocol": "trojan", "settings": {"servers": [{"address": u.hostname, "port": u.port,
                                                               "password": urllib.parse.unquote(u.username)}]},
              "streamSettings": stream_settings(p, u.hostname)}
    elif u.scheme == "ss":
        userinfo = urllib.parse.unquote(u.username or "")
        method, password = (b64(userinfo) if ":" not in userinfo else userinfo).split(":", 1)
        ob = {"protocol": "shadowsocks", "settings": {"servers": [{"address": u.hostname, "port": u.port,
                                                                    "method": method, "password": password}]}}
    else:
        return None
    return name, ob


def servers(text):
    """(name, outbound) of every server in the subscription, in its order."""
    try:
        data = json.loads(text)
    except ValueError:
        data = None
    if data is not None:
        configs = data if isinstance(data, list) else [data]
        result = []
        for cfg in configs:
            for ob in cfg.get("outbounds", []):
                if ob.get("protocol") in ("vless", "trojan", "shadowsocks", "vmess"):
                    result.append((cfg.get("remarks") or ob.get("tag") or "server", ob))
        return result
    if "://" not in text:
        text = b64(text)
    found = (outbound_from_link(line.strip()) for line in text.splitlines() if "://" in line)
    return [s for s in found if s]


def config(outbounds):
    obs = []
    for i, outbound in enumerate(outbounds, 1):
        ob = dict(outbound)
        ob["tag"] = f"proxy-{i}"
        obs.append(ob)
    return {
        "log": {"loglevel": "warning"},
        "inbounds": [{"tag": "http-in", "listen": "0.0.0.0", "port": 3128, "protocol": "http", "settings": {}}],
        "outbounds": obs + [{"tag": "block", "protocol": "blackhole"}],
        # Which servers reach Telegram and how fast: the balancer uses the fastest working one.
        "observatory": {"subjectSelector": ["proxy-"], "probeURL": "https://api.telegram.org/",
                        "probeInterval": "5m", "enableConcurrency": True},
        "routing": {
            "balancers": [{"tag": "telegram", "selector": ["proxy-"], "strategy": {"type": "leastPing"}}],
            "rules": [{"type": "field", "inboundTag": ["http-in"], "balancerTag": "telegram"}],
        },
    }


def main():
    url = env_value("TOPAZ_TELEGRAM_PROXY_SUBSCRIPTION")
    if not url:
        sys.exit("Set TOPAZ_TELEGRAM_PROXY_SUBSCRIPTION in deploy/central/.env (the subscription link).")
    found = servers(fetch(url))
    if not found:
        sys.exit("No vless/trojan/shadowsocks servers in the subscription.")
    if "--list" in sys.argv:
        for i, (name, ob) in enumerate(found, 1):
            print(f"{i}. {name} ({ob['protocol']})")
        return
    picks = ([int(x) for x in sys.argv[sys.argv.index("--pick") + 1].split(",")] if "--pick" in sys.argv
             else list(range(1, len(found) + 1)))
    chosen = [found[i - 1] for i in picks]
    old = os.umask(0o077)
    try:
        with open(OUT, "w", encoding="utf-8") as f:
            json.dump(config([ob for _, ob in chosen]), f, ensure_ascii=False, indent=2)
    finally:
        os.umask(old)
    if hasattr(os, "chown") and os.geteuid() == 0:
        os.chown(OUT, 65532, 65532)  # the xray image runs as this user; nobody else may read the server keys
    print(f"telegram-proxy/config.json: {len(chosen)} of {len(found)} servers — " + ", ".join(n for n, _ in chosen))


if __name__ == "__main__":
    main()
