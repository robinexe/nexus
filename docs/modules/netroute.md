# Rota de jogo (catalog/netroute.v1.json)

Pesquisa de 05/11/2026 (doc "NEXUS Network Intelligence — Pesquisa técnica": GitHub, fóruns, Riot, Valve, Microsoft, IETF,
estudos de overlay). Regra do módulo: **o túnel só liga quando a medição prova que ganha da rota direta.** Nenhum relay baixa o
ping abaixo do tempo da luz na fibra; ele só remove o excesso que a rota do provedor acrescenta.

## O que faz

1. **Diagnóstico (só leitura, sem custo):** acha o servidor da partida pelo tráfego do processo do jogo (ETW
   Microsoft-Windows-Kernel-Network), mede (mediana, p95, p99, jitter, perda), faz o traceroute com nome reverso de cada salto e
   compara com a **régua física** da sua cidade até a cidade do servidor (~1 ms a cada 100 km em linha reta; rota boa até
   2× o piso + 5 ms). Achados: desvio pelo exterior (você e o servidor no Brasil, mas um roteador do meio em Miami), salto grande
   que se mantém, perda que começa num salto e chega ao destino, rede de casa lenta no primeiro salto.
2. **Relay do usuário:** um servidor Linux seu (VPS) com o script `tools/netroute-relay/nexus_relay.py` (WireGuard + NAT +
   bloqueio de destinos privados + agente de medição). O PC gera a própria chave; o relay autoriza só a parte pública e
   devolve um link `nexus-relay://v1/...`.
3. **Teste A/B:** rodadas alternadas "direto" e "pelo relay" (PC → agente pelo túnel + relay → servidor). O túnel liga em
   **modo medição** (só o agente passa por ele), então a rota direta continua a do provedor durante o teste. Decisão
   conservadora (`TunnelDecision`): ganho ≥ 5 ms na mediana em ≥ 2/3 das rodadas, sem piorar p95 em mais de 5 ms nem a perda;
   ou corte de perda (direto ≥ 2%, relay ≤ metade) com ping no máximo 3 ms pior. Na dúvida, rota direta.
4. **Túnel:** WireGuard **oficial** (instalador MSI da WireGuard LLC), `AllowedIPs` = agente do relay + **só os blocos do
   jogo**. O resto do PC continua na rota normal. Sem trocar DNS, sem 0.0.0.0/0, sem driver próprio, sem injeção.

| id | título | classe | o que muda |
|---|---|---|---|
| `netroute.client.install` | Instalar o cliente WireGuard oficial | SupportedDocumented | Baixa `latest.sig` + MSI de download.wireguard.com, confere o BLAKE2b-256 da lista e o Authenticode "WireGuard LLC", `msiexec /qn DO_NOT_LAUNCH=1` |
| `netroute.identity.create` | Criar a chave da rota deste PC | SupportedDocumented | X25519 (RFC 7748) gerado no aplicador; `%ProgramData%\NEXUS\netroute\identity.json` só Administradores/SYSTEM; `client.pub` legível |
| `netroute.relay.add` | Cadastrar um relay | SupportedDocumented | `relays\<id>.link` (link validado e gerado para a chave deste PC) |
| `netroute.tunnel.on` | Ligar a rota pelo relay | Conditional | `NexusRota.conf` (só Administradores/SYSTEM) + serviço `WireGuardTunnel$NexusRota` |

Todas são de administrador, persistentes, com diário e desfazer. Desfazer na ordem inversa: túnel → relay → chave → cliente
(remove o WireGuard só se foi o NEXUS que instalou e não há túnel). Um túnel por vez: outro pedido com relay, jogo ou blocos
diferentes é recusado até desligar o atual. A impressão digital (relay + jogo + blocos, sem a chave) fica na 1ª linha do
`.conf`, e Detect/Restore só mexem no túnel que corresponde à entrada do histórico.

## Jogos

| Perfil | Processo | Blocos do túnel | Observação |
|---|---|---|---|
| VALORANT | `VALORANT-Win64-Shipping.exe` | aprendidos (servidores vistos nas partidas, em /24) | Riot Direct: ganho provável pequeno no Brasil; o teste decide |
| Counter-Strike 2 | `cs2.exe` | relays da Valve em São Paulo (`GetSDRConfig`, POP `gru`) | SDR: o jogo fala com relays da Valve, não com o servidor; experimental |
| Fortnite | `FortniteClient-Win64-Shipping.exe` | aprendidos | nuvem comum: bom candidato |
| Qualquer outro | processo da janela em primeiro plano (fora navegadores, chat, lançadores) | aprendidos | id `game-<processo>` |

Blocos aprendidos: `%LocalAppData%\NEXUS\netroute\learned.json`, /24 por servidor, 30 dias, até 64 por jogo; no túnel entram
até 16 blocos que caibam nos 260 caracteres do parâmetro da ação (agrupa em /24 e prioriza os mais vistos).

**Ligue o túnel ANTES da partida.** Trocar a rota no meio de uma partida muda o IP de origem visto pelo servidor e derruba a
conexão; por isso a escolha de relay é feita antes, nunca no meio.

## Como usar (hoje: linha de comando; a tela entra depois do merge do redesign)

```powershell
nexus rota cidade fortaleza
nexus rota diagnosticar            # com o jogo numa partida; mostra excesso, saltos e o veredito
nexus rota relay codigo            # cria a chave (pede administrador) e mostra o código do PC
# no VPS: sudo python3 nexus_relay.py setup --endpoint <IP público> --name "Fortaleza" --place fortaleza
#         sudo python3 nexus_relay.py add <código do PC>   → imprime nexus-relay://v1/...
nexus rota relay adicionar "nexus-relay://v1/..."
nexus rota testar                  # A/B: diz se o relay ganha e por quanto
nexus rota ligar                   # antes da partida (só os blocos do jogo vão pelo relay)
nexus rota desligar
```

## Relay (tools/netroute-relay)

- `setup`: chave do relay (`wg genkey`), `/etc/wireguard/wg-nexus.conf`, `ip_forward`, nftables (tabela `inet nexus_relay`):
  NAT de saída, **bloqueio de destinos privados/reservados** a partir do túnel (inclui 169.254.169.254, metadados de nuvem),
  agente só pelo túnel; serviços systemd (firewall, wg-quick, agente com `NoNewPrivileges`, `ProtectSystem=strict`).
- `add <código>`: aloca 10.66.0.x, autoriza a chave pública e imprime o link (formato binário compacto, até 260 caracteres).
- `remove`, `list`, `status` (último handshake de cada PC).
- Agente: `GET /health`, `GET /probe?target=<ip>&count=<n>` (ping a partir do relay; só alvos públicos; limite de 240 sondas por
  minuto e 2 medições simultâneas por PC; sem log de endereços).

Formato do link (`RelayLink`, igual no Python): versão(1)=1 · chave do relay(32) · impressão da chave do PC(8 = SHA-256[0..8]) ·
IPv4 do PC no túnel(4) · IPv4 do agente(4) · porta do agente(2) · porta do WireGuard(2) · host(1+n ≤ 60) · nome(1+n ≤ 40 bytes
UTF-8) · cidade(1+n ≤ 20). Vetor comum em `test_nexus_relay.py` e `NetRouteTests`.

## Segurança

- Chave privada do PC nunca sai do PC; `identity.json` e `NexusRota.conf` com ACL só de Administradores e SYSTEM; pasta
  `netroute` com Usuários só leitura (outro programa da conta não troca relay nem blocos).
- Validação em todas as pontas: link (host público, rede do túnel 10/8 ou 100.64/10, agente diferente do PC, cidade conhecida),
  blocos (só públicos, /12 ou menores no IPv4, nunca a rede de casa), ids sem caminho (`r` + 10 hex; jogo `[a-z][a-z0-9-]`).
- Antitrapaça: nenhum fabricante publica lista de ferramentas permitidas. O túnel é uma VPN comum por destino (driver oficial
  WireGuard), sem injeção, hook, leitura de memória ou manipulação de pacotes do jogo. O WinDivert (base de "lag switches") não é
  usado. Compatibilidade só pode ser afirmada depois de testada jogo a jogo.
- Privacidade: sem telemetria. O relay é do usuário; ele vê para quais servidores o jogo fala e o volume (o tráfego entre PC e
  relay vai cifrado pelo WireGuard). A descoberta só lê endereços e portas dos pacotes do processo do jogo, localmente.

## Evidências

- Relays em data centers venceram a rota direta em ~76% dos pares (mediana ~12–14 ms): <https://ar5iv.labs.arxiv.org/html/1710.04422>
- Caminho alternativo melhor em 30–80% dos pares: <https://homes.cs.washington.edu/~tom/pubs/sigcomm99.html>
- Overlays desviam de quedas em < 20 s: <https://angio.net/res/ron-sosp2001.html>
- Provedores escolhem a rota mais barata, não a mais rápida (Riot): <https://technology.riotgames.com/node/25>
- SDR pode melhorar ou piorar conforme o data center (Valve): <https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay>
- São Paulo–Miami: ~74 ms medidos, piso ~65 ms: <https://hatsnet.io/docs/network/latency/pairs/gru-mia-rtt>

## Testes

- `tests/Nexus.Tests/Modules/NetRouteTests.cs`: blocos, régua, nomes de roteador, estatística, diagnóstico, decisão A/B, vigia,
  descoberta (contagem e bytes do ETW), perfis, blocos aprendidos, SDR, X25519 (vetores do RFC 7748), chaves, link (vetor comum),
  configuração do túnel, lista do WireGuard, handlers com backends falsos (fluxo completo e desfazer), catálogo.
- `tests/Nexus.IntegrationTests/Modules/NetRouteIntegrationTests.cs` (VM do CI): ETW real enxerga UDP deste processo; WireGuard
  oficial instalado, chave, relay, serviço do túnel em execução e tudo desfeito.
- `tools/netroute-relay/test_nexus_relay.py` + `e2e-ci.sh` (runner Linux): link, alocação, agente, limites; ponta a ponta com
  namespace de rede (handshake, /health, /probe recusando alvo privado, NAT por HTTPS, destino privado bloqueado, PC removido).
- Workflow `.github/workflows/netroute.yml`.

## O que falta

- Tela "Rota de jogo" na interface (entra depois do merge do redesign, para não conflitar com a outra sessão).
- Vigia durante a partida (`TunnelHealthMonitor` pronto; falta o laço que mede o agente a cada 10 s e avisa/desliga).
- Relays oficiais (infraestrutura própria por região) e banco colaborativo opt-in: só se a fase de validação com usuários
  reais provar ganho (doc de pesquisa, seção 10).
- Testar com jogos de verdade, com cada antitrapaça, em contas de teste.
