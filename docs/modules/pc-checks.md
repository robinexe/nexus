# Checagens do PC (somente leitura)

Cinco verificações que só **leem** e explicam em palavras simples. Nenhuma grava nada; a única correção oferecida (taxa do
monitor) usa a ação reversível que já existia (`gpulab.display.mode`) com a contagem "Manter esta taxa?" de 15 s.

| Checagem | Onde aparece | O que lê | Fonte |
|---|---|---|---|
| Rede para jogos | Ferramentas do sistema › "Rede para jogos"; Problemas; Diagnóstico | ICMP (`System.Net.NetworkInformation.Ping`) até o roteador e até o servidor digitado (padrão `1.1.1.1`): 30 pings, média, variação (jitter = média da diferença entre pings seguidos), perda, p95. Wi-Fi ou cabo por `NetworkInterface`; sinal pelo `netsh wlan show interfaces` (linha "NN%", em qualquer idioma). Bufferbloat: ping parado, ping baixando, ping enviando | [Oppedahl: bufferbloat](https://blog.oppedahl.com/do-you-have-bufferbloat/) |
| Saúde dos discos | Disco › "Saúde dos discos"; Início (disco falhando); Problemas; Diagnóstico | PowerShell oculto: `MSFT_PhysicalDisk` (tipo, barramento, HealthStatus, OperationalStatus) e `MSFT_StorageReliabilityCounter` (temperatura, desgaste, erros não corrigidos, horas ligado). Campo ausente = "não informado" (muitos exigem administrador). Jogos (Steam/Epic/Battle.net/Riot/Xbox, o mesmo leitor do Medir) colocados no disco pela letra da unidade | [Get-StorageReliabilityCounter](https://learn.microsoft.com/en-us/powershell/module/storage/get-storagereliabilitycounter); [TechSpot: tempo de carregamento HD/SATA/NVMe](https://www.techspot.com/article/3023-ssd-gaming-comparison-load-times/) |
| Monitor | Início ("Usar 144 Hz"); Problemas; Placa de vídeo (já existia); Diagnóstico | `QueryDisplayConfig` + `EnumDisplaySettingsEx` pela leitura leve `GpuLabScanner.MonitorsOnly()` (sem DXGI/NVML): taxa atual × maior taxa listada na MESMA resolução; HDR (cores avançadas) suportado/ligado | [Blur Busters](https://www.blurbusters.com/oh-no-im-at-the-wrong-refresh-rate/) |
| Notebook na bateria | Notificação da bandeja + barra de status ao abrir um jogo; Ajustes › "Avisar quando eu abrir um jogo com o notebook na bateria" | Detecção de jogo do Medir (`MeasurePage.GameStarted`) + `GetSystemPowerStatus`. No máximo um aviso a cada 30 min | [TechSpot: notebooks gamer na bateria](https://www.techspot.com/article/1571-gaming-laptop-battery-performance/) |
| Limitação pelo firmware | Problemas; Diagnóstico | Eventos 37 do `Microsoft-Windows-Kernel-Processor-Power` no log Sistema (últimos 7 dias; não exige administrador) e `Win32_PerfFormattedData_Counters_ProcessorInformation.PercentPerformanceLimit` (o nome da classe não muda com o idioma, ao contrário do caminho "\Processor Information\% Performance Limit"); 3 amostras, vale a menor | [Intel: evento 37](https://www.intel.com/content/www/us/en/support/articles/000090904.html) |

## Regras (Nexus.HardwareDiscovery.PcChecks, testadas em PcChecksTests)

- **Rede:** perda ≥ 2% ou jitter ≥ 15 ms até a internet = atenção; até o roteador, perda ≥ 2%, jitter ≥ 10 ms ou média ≥ 20 ms =
  "problema dentro de casa". Ping alto (≥ 100 ms) e estável = só informação (distância). Roteador/servidor que não responde a
  ping = informação, nunca alarme. Wi-Fi = informação; vira atenção com sinal < 50% ou quando a conexão está instável.
- **Bufferbloat:** nota pelo aumento de latência com carga (A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, F). ≥ 60 ms = atenção, com
  o conselho de SQM/QoS no roteador ("internet mais rápida não resolve").
- **Discos:** não íntegro, erros não corrigidos ou "falha prevista" = "faça backup agora"; alerta ou desgaste ≥ 90% = perto do fim;
  ≥ 70 °C (SSD) / ≥ 55 °C (HD) = quente. Jogos num HD = atenção (carregamento, não FPS); num SSD SATA com NVMe no PC = informação.
- **Monitor:** abaixo da maior taxa listada na mesma resolução = atenção; HDR disponível e desligado = informação (escolha sua).
- **Limitação:** algum evento 37 ou limite atual < 90% = aviso neutro ("pode ser temperatura, energia ou limite do fabricante");
  atenção com ≥ 3 eventos ou limite < 80%, e só informação na bateria (limitar na bateria é esperado).

## O teste com carga (bufferbloat)

- Só roda depois de **confirmar** a frase "usa até ~120 MB de dados" (4 downloads de até 25 MB + 2 uploads de até 10 MB, ~8 s cada
  fase). Dá para cancelar a qualquer momento e para pular (o teste de ping funciona sem ele).
- Servidor: **speed.cloudflare.com** (`/__down?bytes=N` e `/__up`), o mesmo que a biblioteca oficial de código aberto da Cloudflare,
  [@cloudflare/speedtest](https://github.com/cloudflare/speedtest) (licença MIT), usa por padrão para qualquer site/app que a
  embute. A página não traz termos de uso específicos para os endpoints; a escolha se apoia nesse uso público pela própria
  Cloudflare. Recebe bytes aleatórios (nunca executáveis), descartados na memória; nada é gravado no disco e nenhum resultado é
  enviado (a biblioteca da Cloudflare envia; o NEXUS não).
- A leitura automática (a cada leitura do PC) só faz Wi-Fi/cabo e 10 pings ao **roteador**: nada sai da casa sem o usuário clicar.

## O que NÃO faz e por quê

- Não muda configuração de rede, roteador, QoS, energia nem BIOS: tudo isso fica com o usuário (o NEXUS explica o caminho).
- Não aumenta `TdrDelay`, não mexe em limites de energia do processador e não "desliga o throttling": esconder o sintoma não resolve.
- Não mostra SMART bruto nem "nota de vida" inventada: só o que o Windows informa; o resto é "não informado".
- O monitor nunca muda de resolução (só a taxa), como na página Placa de vídeo.

## Validar no Windows

1. Ferramentas do sistema › Rede para jogos › Testar (cabo e Wi-Fi; um servidor do jogo); depois "Testar com a conexão cheia".
2. Disco › Saúde dos discos, aberto como usuário comum e como administrador (os contadores mudam de "não informado" para números).
3. Monitor a 60 Hz num painel de 144 Hz: cartão no Início → "Usar 144 Hz" → Placa de vídeo pergunta, troca e volta sozinho em 15 s.
4. Notebook fora da tomada, abrir um jogo da biblioteca: notificação "Notebook na bateria"; desligar em Ajustes e repetir.
5. Diagnóstico mostra "Processador sem limitação do firmware" (ou o aviso com a contagem de eventos 37).
