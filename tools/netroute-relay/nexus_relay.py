#!/usr/bin/env python3
# NEXUS — relay da rota de jogo (docs/modules/netroute.md).
#
# Roda num servidor Linux seu (VPS) com WireGuard. Só a biblioteca padrão do Python 3.8+ e as ferramentas do sistema
# (wireguard-tools, nftables, iproute2, ping). Comandos:
#
#   sudo python3 nexus_relay.py setup --endpoint <IP ou nome público> [--name "Fortaleza"] [--place fortaleza] [--port 51820]
#   sudo python3 nexus_relay.py add <código do PC> [--label "PC do Matheus"]   -> imprime o link nexus-relay://v1/...
#   sudo python3 nexus_relay.py remove <código do PC>
#   sudo python3 nexus_relay.py list
#   sudo python3 nexus_relay.py status
#   python3 nexus_relay.py serve        (agente de medição; o setup instala como serviço systemd)
#
# Segurança:
#  - O tráfego dos PCs sai pela internet com NAT, mas destinos privados/reservados (rede interna do servidor, metadados da
#    nuvem 169.254.169.254 etc.) são BLOQUEADOS no nftables: o relay não vira ponte para a rede de ninguém.
#  - O agente só escuta no endereço do relay DENTRO do túnel (ex.: 10.66.0.1:8787): só PCs autorizados falam com ele.
#    Ele só mede alvos públicos e tem limite de taxa por PC.
#  - Nenhuma chave privada de PC passa por aqui: o PC gera a própria chave e entrega só a pública ("código do PC").
import argparse
import base64
import hashlib
import ipaddress
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

VERSION = "1.0.0"
CONFIG_DIR = os.environ.get("NEXUS_RELAY_DIR", "/etc/nexus-relay")
CONFIG_FILE = os.path.join(CONFIG_DIR, "config.json")
WG_IFACE = os.environ.get("NEXUS_RELAY_IFACE", "wg-nexus")
WG_CONF = os.path.join(os.environ.get("NEXUS_RELAY_WG_DIR", "/etc/wireguard"), WG_IFACE + ".conf")
INSTALL_DIR = "/usr/local/lib/nexus-relay"
SCHEME = "nexus-relay://v1/"
MAX_PROBE = 30

# Mesmas cidades do NEXUS (src/Nexus.NetRoute/Geo/Places.cs): só ids conhecidos entram no link.
PLACES = {
    "sao-paulo", "campinas", "rio-de-janeiro", "belo-horizonte", "vitoria", "curitiba", "florianopolis", "porto-alegre",
    "brasilia", "goiania", "campo-grande", "cuiaba", "salvador", "aracaju", "maceio", "recife", "joao-pessoa", "natal",
    "fortaleza", "teresina", "sao-luis", "belem", "macapa", "manaus", "boa-vista", "porto-velho", "rio-branco", "palmas",
    "santiago", "buenos-aires", "miami", "ashburn", "chicago", "dallas", "los-angeles", "lisboa", "madrid", "frankfurt",
    "londres", "toquio", "singapura",
}

TUNNEL_NETS = [ipaddress.ip_network("10.0.0.0/8"), ipaddress.ip_network("100.64.0.0/10")]
# Destinos nunca encaminhados a partir do túnel (RFC 6890 + metadados de nuvem).
BLOCKED_V4 = [
    "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24",
    "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
    "224.0.0.0/4", "240.0.0.0/4",
]


# ---------------------------------------------------------------------------------------------------------------- chaves

def b64key_valid(text):
    if not isinstance(text, str) or len(text) != 44 or not text.endswith("="):
        return False
    try:
        return len(base64.b64decode(text, validate=True)) == 32
    except (ValueError, base64.binascii.Error):
        return False


def key_fingerprint(public_key_b64):
    """16 hex do SHA-256 dos 32 bytes da chave pública (o NEXUS confere se o link é para a chave dele)."""
    return hashlib.sha256(base64.b64decode(public_key_b64)).hexdigest()[:16]


def relay_id(server_public_key_b64):
    return "r" + hashlib.sha256(server_public_key_b64.encode("ascii")).hexdigest()[:10]


# ---------------------------------------------------------------------------------------------------------------- link

def _valid_host(host):
    if not host or len(host) > 60:
        return False
    try:
        ipaddress.ip_address(host)
        return True
    except ValueError:
        pass
    return re.fullmatch(r"([A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}", host) is not None


def encode_link(name, host, port, server_key, client_fp, client_ip, agent_ip, agent_port, place=None):
    """Formato binário compacto (igual a RelayLink.ToLink no C#)."""
    name_b = name.encode("utf-8")
    host_b = host.encode("ascii")
    place_b = (place or "").encode("ascii")
    if not (1 <= len(name_b) <= 40) or any(c in name for c in "[]=#") or any(ord(c) < 32 for c in name):
        raise ValueError("nome do relay: 1 a 40 bytes, sem [ ] = #")
    if not _valid_host(host):
        raise ValueError("host inválido (IPv4, IPv6 ou nome DNS até 60 caracteres)")
    if place and place not in PLACES:
        raise ValueError("cidade desconhecida: " + place)
    if not b64key_valid(server_key):
        raise ValueError("chave do relay inválida")
    if not re.fullmatch(r"[0-9a-f]{16}", client_fp):
        raise ValueError("impressão da chave do PC inválida")
    buf = bytearray([1])
    buf += base64.b64decode(server_key)
    buf += bytes.fromhex(client_fp)
    buf += ipaddress.IPv4Address(client_ip).packed
    buf += ipaddress.IPv4Address(agent_ip).packed
    buf += int(agent_port).to_bytes(2, "big")
    buf += int(port).to_bytes(2, "big")
    buf += bytes([len(host_b)]) + host_b
    buf += bytes([len(name_b)]) + name_b
    buf += bytes([len(place_b)]) + place_b
    link = SCHEME + base64.urlsafe_b64encode(bytes(buf)).decode("ascii").rstrip("=")
    if len(link) > 260:
        raise ValueError("link longo demais (encurte o nome ou o host)")
    return link


def decode_link(link):
    if not link.startswith(SCHEME):
        raise ValueError("não é um link nexus-relay://v1/")
    payload = link[len(SCHEME):]
    data = base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4))
    pos = 0

    def take(n):
        nonlocal pos
        if pos + n > len(data):
            raise ValueError("link incompleto")
        chunk = data[pos:pos + n]
        pos += n
        return chunk

    if take(1)[0] != 1:
        raise ValueError("versão desconhecida")
    out = {
        "server_key": base64.b64encode(take(32)).decode("ascii"),
        "client_fp": take(8).hex(),
        "client_ip": str(ipaddress.IPv4Address(take(4))),
        "agent_ip": str(ipaddress.IPv4Address(take(4))),
        "agent_port": int.from_bytes(take(2), "big"),
        "port": int.from_bytes(take(2), "big"),
    }
    out["host"] = take(take(1)[0]).decode("ascii")
    out["name"] = take(take(1)[0]).decode("utf-8")
    plen = take(1)[0]
    out["place"] = take(plen).decode("ascii") if plen else None
    if pos != len(data):
        raise ValueError("dados a mais no link")
    return out


# ---------------------------------------------------------------------------------------------------------------- config

def load_config():
    with open(CONFIG_FILE, "r", encoding="utf-8") as f:
        return json.load(f)


def save_config(cfg):
    os.makedirs(CONFIG_DIR, mode=0o700, exist_ok=True)
    tmp = CONFIG_FILE + ".tmp"
    with open(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)
    os.replace(tmp, CONFIG_FILE)


def allocate_ip(subnet, used, agent_ip):
    """Próximo endereço livre da sub-rede (pula rede, broadcast, o próprio relay e os já usados)."""
    net = ipaddress.ip_network(subnet)
    taken = set(used) | {agent_ip}
    for host in net.hosts():
        if str(host) not in taken:
            return str(host)
    raise RuntimeError("sub-rede cheia: remova PCs antigos ou use --subnet maior")


def render_wg_conf(cfg):
    lines = [
        "# NEXUS relay — gerado por nexus_relay.py; edite pelos comandos (add/remove).",
        "[Interface]",
        "Address = %s/%d" % (cfg["agent_ip"], ipaddress.ip_network(cfg["subnet"]).prefixlen),
        "ListenPort = %d" % cfg["port"],
        "PrivateKey = %s" % cfg["server_private_key"],
        "",
    ]
    for peer in cfg.get("peers", []):
        lines += ["# " + peer.get("label", ""), "[Peer]", "PublicKey = " + peer["public_key"], "AllowedIPs = %s/32" % peer["ip"], ""]
    return "\n".join(lines)


def write_wg_conf(cfg):
    os.makedirs(os.path.dirname(WG_CONF), mode=0o700, exist_ok=True)
    tmp = WG_CONF + ".tmp"
    with open(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as f:
        f.write(render_wg_conf(cfg))
    os.replace(tmp, WG_CONF)


# ---------------------------------------------------------------------------------------------------------------- sistema

def run(cmd, check=True, input_text=None):
    r = subprocess.run(cmd, input=input_text, capture_output=True, text=True)
    if check and r.returncode != 0:
        raise RuntimeError("%s falhou (%d): %s" % (" ".join(cmd), r.returncode, (r.stderr or r.stdout).strip()))
    return r


def require_root():
    if os.geteuid() != 0:
        sys.exit("Precisa de root: rode com sudo.")


def require_tools(names):
    missing = [n for n in names if shutil.which(n) is None]
    if missing:
        sys.exit("Faltam ferramentas: %s. No Debian/Ubuntu: sudo apt-get install -y wireguard-tools nftables iproute2 iputils-ping"
                 % ", ".join(missing))


def default_egress_iface():
    r = run(["ip", "-o", "route", "get", "1.1.1.1"])
    m = re.search(r"\bdev\s+(\S+)", r.stdout)
    if not m:
        raise RuntimeError("não achei a interface de saída para a internet")
    return m.group(1)


def nft_rules(subnet, egress, agent_ip, agent_port):
    blocked = ", ".join(BLOCKED_V4)
    return f"""table inet nexus_relay {{
  chain forward {{
    type filter hook forward priority 0; policy accept;
    iifname "{WG_IFACE}" ip daddr {{ {blocked} }} drop
    iifname "{WG_IFACE}" ip6 daddr != 2000::/3 drop
    iifname "{WG_IFACE}" oifname "{egress}" accept
    oifname "{WG_IFACE}" ct state established,related accept
    oifname "{WG_IFACE}" drop
  }}
  chain input {{
    type filter hook input priority 0; policy accept;
    iifname "{WG_IFACE}" ip daddr {agent_ip} tcp dport {agent_port} accept
    iifname "{WG_IFACE}" icmp type echo-request accept
    iifname "{WG_IFACE}" ct state established,related accept
    iifname "{WG_IFACE}" drop
  }}
  chain postrouting {{
    type nat hook postrouting priority 100; policy accept;
    ip saddr {subnet} oifname "{egress}" masquerade
  }}
}}
"""


def apply_firewall(cfg):
    run(["nft", "delete", "table", "inet", "nexus_relay"], check=False)
    run(["nft", "-f", "-"], input_text=nft_rules(cfg["subnet"], cfg["egress"], cfg["agent_ip"], cfg["agent_port"]))


SYSTEMD_FW = """[Unit]
Description=NEXUS relay: regras de firewall/NAT
After=network-online.target
Before=wg-quick@{iface}.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/bin/env python3 {lib}/nexus_relay.py firewall

[Install]
WantedBy=multi-user.target
"""

SYSTEMD_AGENT = """[Unit]
Description=NEXUS relay: agente de medição (só dentro do túnel)
After=wg-quick@{iface}.service
Requires=wg-quick@{iface}.service

[Service]
ExecStart=/usr/bin/env python3 {lib}/nexus_relay.py serve
Restart=always
RestartSec=3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
ReadOnlyPaths=/
AmbientCapabilities=CAP_NET_RAW
CapabilityBoundingSet=CAP_NET_RAW

[Install]
WantedBy=multi-user.target
"""


def cmd_setup(a):
    require_root()
    require_tools(["wg", "wg-quick", "nft", "ip", "ping"])
    if os.path.exists(CONFIG_FILE) and not a.force:
        sys.exit("O relay já está configurado (%s). Use --force para refazer (os PCs cadastrados perdem o acesso)." % CONFIG_FILE)
    if a.place and a.place not in PLACES:
        sys.exit("Cidade desconhecida: %s. Opções: %s" % (a.place, ", ".join(sorted(PLACES))))
    net = ipaddress.ip_network(a.subnet, strict=True)
    if not any(net.subnet_of(t) for t in TUNNEL_NETS) or net.prefixlen > 28:
        sys.exit("--subnet precisa estar em 10.0.0.0/8 ou 100.64.0.0/10, com /28 ou maior.")
    if not _valid_host(a.endpoint):
        sys.exit("--endpoint inválido (IP público ou nome DNS do servidor).")
    priv = run(["wg", "genkey"]).stdout.strip()
    pub = run(["wg", "pubkey"], input_text=priv + "\n").stdout.strip()
    agent_ip = str(next(net.hosts()))
    cfg = {
        "version": 1, "name": a.name, "place": a.place, "endpoint": a.endpoint, "port": a.port, "subnet": str(net),
        "agent_ip": agent_ip, "agent_port": a.agent_port, "server_private_key": priv, "server_public_key": pub,
        "egress": a.egress or default_egress_iface(), "peers": [],
    }
    # Testa o link antes de gravar qualquer coisa (nome/host longos demais seriam recusados pelo NEXUS).
    encode_link(cfg["name"], cfg["endpoint"], cfg["port"], pub, "0" * 16, agent_ip, agent_ip, cfg["agent_port"], cfg["place"])
    save_config(cfg)
    write_wg_conf(cfg)
    with open("/etc/sysctl.d/90-nexus-relay.conf", "w", encoding="utf-8") as f:
        f.write("net.ipv4.ip_forward=1\n")
    run(["sysctl", "-q", "-w", "net.ipv4.ip_forward=1"])
    apply_firewall(cfg)
    if not a.no_systemd and shutil.which("systemctl"):
        os.makedirs(INSTALL_DIR, exist_ok=True)
        shutil.copy2(os.path.abspath(__file__), os.path.join(INSTALL_DIR, "nexus_relay.py"))
        with open("/etc/systemd/system/nexus-relay-firewall.service", "w", encoding="utf-8") as f:
            f.write(SYSTEMD_FW.format(iface=WG_IFACE, lib=INSTALL_DIR))
        with open("/etc/systemd/system/nexus-relay-agent.service", "w", encoding="utf-8") as f:
            f.write(SYSTEMD_AGENT.format(iface=WG_IFACE, lib=INSTALL_DIR))
        run(["systemctl", "daemon-reload"])
        run(["systemctl", "enable", "--now", "nexus-relay-firewall.service"])
        run(["systemctl", "enable", "--now", "wg-quick@%s.service" % WG_IFACE])
        run(["systemctl", "enable", "--now", "nexus-relay-agent.service"])
    else:
        run(["wg-quick", "up", WG_IFACE])
    print("Relay pronto: %s (%s:%d). Libere a porta UDP %d no firewall do provedor (painel do VPS)." % (a.name, a.endpoint, a.port, a.port))
    print("Agora, no NEXUS, copie o código deste PC e rode aqui: sudo python3 nexus_relay.py add <código>")


def live_set_peer(cfg, public_key, ip, remove=False):
    if run(["ip", "link", "show", WG_IFACE], check=False).returncode != 0:
        return
    if remove:
        run(["wg", "set", WG_IFACE, "peer", public_key, "remove"], check=False)
    else:
        run(["wg", "set", WG_IFACE, "peer", public_key, "allowed-ips", ip + "/32"])


def cmd_add(a):
    require_root()
    cfg = load_config()
    if not b64key_valid(a.public_key):
        sys.exit("Código do PC inválido: copie o código inteiro (44 caracteres terminando em '=') da tela Rota de jogo do NEXUS.")
    if a.public_key == cfg["server_public_key"]:
        sys.exit("Esse é o código do próprio relay, não do PC.")
    peer = next((p for p in cfg["peers"] if p["public_key"] == a.public_key), None)
    if peer is None:
        ip = allocate_ip(cfg["subnet"], [p["ip"] for p in cfg["peers"]], cfg["agent_ip"])
        peer = {"public_key": a.public_key, "ip": ip, "label": (a.label or "PC")[:60], "added": int(time.time())}
        cfg["peers"].append(peer)
        save_config(cfg)
        write_wg_conf(cfg)
        live_set_peer(cfg, a.public_key, ip)
    link = encode_link(cfg["name"], cfg["endpoint"], cfg["port"], cfg["server_public_key"], key_fingerprint(a.public_key),
                       peer["ip"], cfg["agent_ip"], cfg["agent_port"], cfg.get("place"))
    print("Cole este link no NEXUS (Rota de jogo > Cadastrar relay):")
    print(link)


def cmd_remove(a):
    require_root()
    cfg = load_config()
    before = len(cfg["peers"])
    cfg["peers"] = [p for p in cfg["peers"] if p["public_key"] != a.public_key]
    if len(cfg["peers"]) == before:
        sys.exit("Esse PC não está cadastrado.")
    save_config(cfg)
    write_wg_conf(cfg)
    live_set_peer(cfg, a.public_key, None, remove=True)
    print("PC removido.")


def cmd_list(_a):
    cfg = load_config()
    print("Relay %s (%s) %s:%d, id %s" % (cfg["name"], cfg.get("place") or "sem cidade", cfg["endpoint"], cfg["port"], relay_id(cfg["server_public_key"])))
    for p in cfg["peers"]:
        print("  %-15s %s  %s" % (p["ip"], p["public_key"], p.get("label", "")))


def cmd_status(_a):
    cfg = load_config()
    cmd_list(_a)
    r = run(["wg", "show", WG_IFACE, "latest-handshakes"], check=False)
    if r.returncode == 0:
        now = int(time.time())
        for line in r.stdout.splitlines():
            parts = line.split()
            if len(parts) == 2:
                age = now - int(parts[1]) if parts[1] != "0" else None
                print("  %s: %s" % (parts[0][:12] + "...", "nunca conectou" if age is None else "último contato há %d s" % age))
    else:
        print("Interface %s não está ativa." % WG_IFACE)


def cmd_firewall(_a):
    require_root()
    apply_firewall(load_config())


# ---------------------------------------------------------------------------------------------------------------- agente

PING_LINE = re.compile(r"icmp_seq=(\d+).*?time[=<]([0-9.]+)\s*ms")


def parse_ping(output, count):
    """Saída do ping do Linux -> lista de RTT por sequência (None = perdida)."""
    samples = [None] * count
    for m in PING_LINE.finditer(output):
        seq = int(m.group(1))
        if 1 <= seq <= count:
            samples[seq - 1] = round(float(m.group(2)), 3)
    return samples


def target_allowed(text):
    try:
        ip = ipaddress.ip_address(text)
    except ValueError:
        return None
    if ip.version == 4 and any(ip in ipaddress.ip_network(n) for n in BLOCKED_V4):
        return None
    if not ip.is_global:
        return None
    return ip


class RateLimiter:
    """Por PC: no máximo `per_minute` sondas por minuto e 2 medições ao mesmo tempo."""

    def __init__(self, per_minute=240, concurrent=2):
        self.per_minute = per_minute
        self.concurrent = concurrent
        self.lock = threading.Lock()
        self.windows = {}
        self.active = {}

    def acquire(self, client, cost, now=None):
        now = time.monotonic() if now is None else now
        with self.lock:
            stamps = [t for t in self.windows.get(client, []) if now - t[0] < 60]
            used = sum(c for _, c in stamps)
            if used + cost > self.per_minute or self.active.get(client, 0) >= self.concurrent:
                self.windows[client] = stamps
                return False
            stamps.append((now, cost))
            self.windows[client] = stamps
            self.active[client] = self.active.get(client, 0) + 1
            return True

    def release(self, client):
        with self.lock:
            self.active[client] = max(0, self.active.get(client, 1) - 1)


LIMITER = RateLimiter()


def probe(target, count):
    flag = "-6" if target.version == 6 else "-4"
    r = subprocess.run(["ping", flag, "-n", "-c", str(count), "-i", "0.2", "-W", "1", str(target)],
                       capture_output=True, text=True, timeout=count * 1.5 + 5)
    return parse_ping(r.stdout, count)


class AgentHandler(BaseHTTPRequestHandler):
    server_version = "nexus-relay/" + VERSION
    relay_name = "relay"

    def log_message(self, fmt, *args):  # sem log de endereços por padrão (privacidade)
        pass

    def _json(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802 (nome exigido pelo http.server)
        url = urlparse(self.path)
        if url.path == "/health":
            return self._json(200, {"ok": True, "name": self.relay_name, "version": VERSION})
        if url.path == "/probe":
            q = parse_qs(url.query)
            target = target_allowed((q.get("target") or [""])[0])
            if target is None:
                return self._json(400, {"error": "alvo precisa ser um endereço público"})
            try:
                count = max(1, min(MAX_PROBE, int((q.get("count") or ["10"])[0])))
            except ValueError:
                return self._json(400, {"error": "count inválido"})
            client = self.client_address[0]
            if not LIMITER.acquire(client, count):
                return self._json(429, {"error": "muitas medições; tente em instantes"})
            try:
                return self._json(200, {"target": str(target), "samples": probe(target, count)})
            except (subprocess.TimeoutExpired, OSError):
                return self._json(500, {"error": "ping falhou"})
            finally:
                LIMITER.release(client)
        return self._json(404, {"error": "não encontrado"})


def cmd_serve(a):
    cfg = load_config() if a.listen is None else None
    host, port = (a.listen.rsplit(":", 1) if a.listen else (cfg["agent_ip"], str(cfg["agent_port"])))
    AgentHandler.relay_name = (cfg or {}).get("name", "relay")
    # Espera a interface do túnel ter o endereço (o serviço sobe logo depois do wg-quick).
    for _ in range(30):
        try:
            srv = ThreadingHTTPServer((host, int(port)), AgentHandler)
            break
        except OSError:
            time.sleep(1)
    else:
        sys.exit("não consegui escutar em %s:%s" % (host, port))
    srv.daemon_threads = True
    print("agente em http://%s:%s" % (host, port), flush=True)
    srv.serve_forever()


# ---------------------------------------------------------------------------------------------------------------- testes

def cmd_client_conf(a):
    """Só para testes automatizados: configuração wg-quick de um cliente a partir do link e da chave privada dele."""
    info = decode_link(a.link)
    allowed = [info["agent_ip"] + "/32"] + [x for x in (a.allowed or "").split(",") if x]
    print("[Interface]\nPrivateKey = %s\nAddress = %s/32\n\n[Peer]\nPublicKey = %s\nEndpoint = %s:%d\nAllowedIPs = %s\nPersistentKeepalive = 25"
          % (a.private_key, info["client_ip"], info["server_key"], info["host"], info["port"], ", ".join(allowed)))


def main(argv=None):
    p = argparse.ArgumentParser(prog="nexus_relay.py", description="Relay da rota de jogo do NEXUS (WireGuard).")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("setup", help="configura este servidor como relay")
    s.add_argument("--endpoint", required=True, help="IP público ou nome DNS deste servidor")
    s.add_argument("--name", default="Relay NEXUS")
    s.add_argument("--place", default=None, help="cidade (ex.: sao-paulo, fortaleza, miami)")
    s.add_argument("--port", type=int, default=51820)
    s.add_argument("--subnet", default="10.66.0.0/24")
    s.add_argument("--agent-port", type=int, default=8787)
    s.add_argument("--egress", default=None, help="interface de saída (padrão: a da rota para a internet)")
    s.add_argument("--force", action="store_true")
    s.add_argument("--no-systemd", action="store_true")
    s.set_defaults(func=cmd_setup)
    s = sub.add_parser("add", help="autoriza um PC pelo código dele e imprime o link")
    s.add_argument("public_key")
    s.add_argument("--label", default=None)
    s.set_defaults(func=cmd_add)
    s = sub.add_parser("remove", help="tira o acesso de um PC")
    s.add_argument("public_key")
    s.set_defaults(func=cmd_remove)
    sub.add_parser("list", help="lista os PCs").set_defaults(func=cmd_list)
    sub.add_parser("status", help="estado do túnel e último contato de cada PC").set_defaults(func=cmd_status)
    sub.add_parser("firewall", help="(re)aplica as regras de NAT e bloqueio").set_defaults(func=cmd_firewall)
    s = sub.add_parser("serve", help="agente de medição")
    s.add_argument("--listen", default=None, help="host:porta (padrão: endereço do relay no túnel)")
    s.set_defaults(func=cmd_serve)
    s = sub.add_parser("client-conf", help=argparse.SUPPRESS)
    s.add_argument("link")
    s.add_argument("private_key")
    s.add_argument("--allowed", default="")
    s.set_defaults(func=cmd_client_conf)
    a = p.parse_args(argv)
    try:
        a.func(a)
    except (RuntimeError, ValueError, FileNotFoundError) as e:
        sys.exit("erro: %s" % e)


if __name__ == "__main__":
    main()
