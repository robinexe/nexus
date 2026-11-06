#!/usr/bin/env bash
# Ponta a ponta do relay num runner Linux descartável do GitHub Actions (como root). Nada disto roda no PC de ninguém.
#  1. setup do relay neste runner (WireGuard + NAT + bloqueio de destinos privados + agente);
#  2. um "PC" num namespace de rede separado, ligado por um par veth, com a chave dele;
#  3. add do PC -> link; o cliente monta o túnel a partir do link (mesmo formato que o NEXUS lê);
#  4. confere: ping ao relay pelo túnel, /health do agente, /probe recusando alvo privado, NAT até a internet por HTTPS
#     (só o destino do AllowedIPs passa pelo túnel), destino privado bloqueado e PC removido perde o acesso.
set -euo pipefail
cd "$(dirname "$0")"
export NEXUS_RELAY_DIR=/tmp/nexus-relay-e2e
R="python3 $(pwd)/nexus_relay.py"
NS=nexus-pc

cleanup() { set +e; ip netns del $NS 2>/dev/null; wg-quick down wg-nexus 2>/dev/null; kill %1 2>/dev/null; nft delete table inet nexus_relay 2>/dev/null; }
trap cleanup EXIT

# Rede de teste: veth host 10.200.0.1 <-> namespace 10.200.0.2 (o "PC").
ip netns add $NS
ip link add veth-host type veth peer name veth-pc
ip link set veth-pc netns $NS
ip addr add 10.200.0.1/24 dev veth-host && ip link set veth-host up
ip netns exec $NS ip addr add 10.200.0.2/24 dev veth-pc
ip netns exec $NS ip link set veth-pc up && ip netns exec $NS ip link set lo up
ip netns exec $NS ip route add default via 10.200.0.1

# 1. Relay (host) — endpoint 10.200.0.1 só porque o "PC" está no namespace; o NEXUS exige endereço público no link real.
$R setup --endpoint relay.e2e.example --name "Relay CI" --place sao-paulo --no-systemd
# O Docker do runner deixa a política FORWARD do iptables em DROP; num VPS comum isso não existe.
iptables -P FORWARD ACCEPT || true
$R serve & sleep 2

# 2-3. Chave do "PC" e link.
PRIV=$(wg genkey); PUB=$(echo "$PRIV" | wg pubkey)
LINK=$($R add "$PUB" --label "PC de teste" | tail -n 1)
echo "link: $LINK (${#LINK} caracteres)"
[[ ${#LINK} -le 260 ]] || { echo "link longo demais"; exit 1; }
TARGET=$(getent ahostsv4 example.com | awk 'NR==1{print $1}')
echo "destino de teste pela rota: example.com = $TARGET"
$R client-conf "$LINK" "$PRIV" --allowed "$TARGET/32" | sed 's/^Endpoint = .*/Endpoint = 10.200.0.1:51820/' > /tmp/nexus-pc.conf
cat /tmp/nexus-pc.conf | grep -v PrivateKey
ip netns exec $NS ip link add wg-pc type wireguard
ip netns exec $NS wg setconf wg-pc <(grep -v -E '^(Address|DNS)' /tmp/nexus-pc.conf)
ADDR=$(grep '^Address' /tmp/nexus-pc.conf | awk '{print $3}')
ip netns exec $NS ip addr add "$ADDR" dev wg-pc
ip netns exec $NS ip link set wg-pc up
ip netns exec $NS ip route add 10.66.0.1/32 dev wg-pc
ip netns exec $NS ip route add "$TARGET/32" dev wg-pc

# 4. Conferências.
ip netns exec $NS ping -c 3 -W 2 10.66.0.1
ip netns exec $NS curl -fsS --max-time 5 http://10.66.0.1:8787/health | tee /dev/stderr | grep -q '"ok": true'
CODE=$(ip netns exec $NS curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://10.66.0.1:8787/probe?target=192.168.1.1&count=3")
[[ "$CODE" == "400" ]] || { echo "probe aceitou alvo privado ($CODE)"; exit 1; }
ip netns exec $NS curl -fsS --max-time 10 "http://10.66.0.1:8787/probe?target=$TARGET&count=3" | tee /dev/stderr | grep -q '"samples"'
ip netns exec $NS curl -fsS --max-time 10 -o /dev/null --resolve "example.com:443:$TARGET" https://example.com/ && echo "NAT pelo relay: OK"
wg show wg-nexus transfer
# Destino privado pelo túnel: bloqueado pelo nftables do relay.
ip netns exec $NS ip route add 192.168.250.1/32 dev wg-pc
SERVER_PUB=$(wg show wg-nexus public-key)
ip netns exec $NS wg set wg-pc peer "$SERVER_PUB" allowed-ips "10.66.0.1/32,$TARGET/32,192.168.250.1/32"
if ip netns exec $NS ping -c 2 -W 1 192.168.250.1 >/dev/null 2>&1; then echo "destino privado passou"; exit 1; fi
echo "destino privado bloqueado: OK"
# PC removido perde o acesso.
$R remove "$PUB"
sleep 1
if ip netns exec $NS curl -fsS --max-time 3 http://10.66.0.1:8787/health >/dev/null 2>&1; then echo "PC removido ainda acessa"; exit 1; fi
echo "PC removido sem acesso: OK"
echo "E2E do relay: TUDO OK"
