# Recomendação automática pelo hardware (03/11/2026)

O NEXUS lê o PC (descoberta somente leitura) e, a partir disso, marca cada ajuste do catálogo e cada opção de jogo como
**recomendado / neutro / não recomendado para este PC**, com uma frase do motivo. Nada é aplicado sozinho por causa disso: a
recomendação só muda **o que vem marcado por padrão** (Ajustes rápidos, Automático) e **os selos** nas telas. Toda escrita continua
sendo uma ação de catálogo pelo aplicador, com confirmação, diário e desfazer.

Regra de honestidade: **só entra regra com mecanismo claro e fonte**. Onde não há base, a resposta é "neutro" e nada muda na tela.
Sem leitura (placa, RAM, formato ou disco não lidos), nenhuma regra dispara: "indisponível" nunca vira "fraco" nem "forte".

## Onde está

| Peça | Arquivo |
|---|---|
| Classificador puro (perfil do PC) | `src/Nexus.Assistant/HardwareProfile.cs` (`HardwareProfiler.Classify`, `HardwareProfile`, `GpuClass`, `GpuMaker`) |
| Regras puras (ação → veredito; público do cartão → veredito) | `src/Nexus.Assistant/HardwareRules.cs` (`HardwareRules.ForAction`, `ForAudience`, `Fit`, `FitReason`) |
| Automático | `AutoPilot.IsAutomatic(change, profile)` / `AutoPilot.Select(changes, profile)`: o que o hardware desaconselha não entra |
| Textos, seleção padrão, frase do perfil | `src/Nexus.Desktop/ViewModels/Simple/HardwareFitText.cs` (`FitText`, `QuickTweakSelection`, `HardwareProfileText`) |
| Perfil na janela | `MainViewModel.HardwareProfile` (calculado fora da thread da interface depois da leitura; `IModuleHost.HardwareProfile`) |
| Ajustes rápidos | `MainViewModel.BuildQuickTweaks`: recomendado = risco baixo de um clique **e** sem veto do hardware; selo "Recomendado para o seu PC: …"; vetado vai para "Mostrar mais opções" com "Não indicado para notebook: …" |
| Início | o cartão "N ajustes seguros ainda não foram feitos" usa `QuickTweaks.Pending` (só os recomendados para o perfil) |
| Assistente (passo a passo) | `ChangeRow`: desmarcado por padrão quando o hardware desaconselha; selo com o motivo |
| Jogos | `GameOption.Fit` (selo discreto por opção) e `SimpleGameRow.ApplyRecommended` ("Aplicar o recomendado": um clique, confirmação, desfazer; só aparece quando há opção recomendada ainda desligada) |
| Ajustes › Perfil do PC | `SettingsPage.ProfileHeadline` ("Notebook com placa de entrada (GTX 1650, 4 GB), 16 GB de RAM, SSD"), o que não foi lido, e a preferência "Prefiro menos atraso a imagem bonita" (`NexusSettings.PreferLowLatency`, padrão desligado) |
| Testes | `tests/Nexus.Tests/Modules/HardwareRecommendationTests.cs` |

## Perfil do PC

| Campo | Fonte | Observação |
|---|---|---|
| Placa principal | descoberta (`SystemFacts.Gpus`, registro `HardwareInformation.qwMemorySize` do driver) | a dedicada com mais VRAM; sem dedicada, a integrada; adaptadores de software (Basic Display, RDP, Hyper-V) ignorados |
| Dedicada × integrada | `PcProfiler.IsDiscrete` (NVIDIA = dedicada; AMD/Intel pelo nome: RX/R9/Arc = dedicada, "Radeon Graphics"/Vega/UHD/Iris = integrada) | |
| Classe da placa | tabela abaixo (família pelo nome; sem família, VRAM) | `GpuClassSource` guarda como foi decidido |
| Fornecedor | `Vendor` da descoberta ou nome | NVIDIA / AMD / Intel / desconhecido |
| RAM | `TotalMemoryBytes` arredondado | "RAM baixa" = **8 GB ou menos** |
| Notebook | gabinete SMBIOS tipo 3 (`PcTraits.ChassisType`, DMTF DSP0134 §7.4.1); sem gabinete, bateria do sistema = notebook | sem os dois: null |
| Na bateria | `GetSystemPowerStatus` (`ACLineStatus`) só quando há bateria | |
| SSD/HD | `PcTraits.SystemDriveIsSsd` (IOCTL seek penalty / NVMe) ou `SystemFacts.SystemDiskMedia` | |
| Competitivo | **preferência do usuário** (Ajustes), não hardware | padrão desligado |

### Tabela da classe da placa (`HardwareProfiler.ClassOf`)

A classe segue a **segmentação dos próprios fabricantes** (o número da família) e os requisitos publicados pelos jogos: o Fortnite
pede vídeo integrado (Intel HD 4000) no mínimo, GTX 960 / R9 280 no recomendado e RTX 3070 / RX 6700 XT para "Épico"
([requisitos do Fortnite](https://www.pcgamesn.com/fortnite/system-requirements), espelho da página da Epic
`epicgames.com/help/.../a202300000012731`). Os três degraus da tabela (integrada → entrada → intermediária → forte) são esses degraus.

| Classe | NVIDIA | AMD | Intel | Sem família conhecida |
|---|---|---|---|---|
| Integrada | — | Radeon Graphics / Vega (APU) | UHD / Iris / Arc integrada | sem placa dedicada |
| Entrada | GTX/RTX **xx50** e abaixo (1650, 3050, 4050), **MX**, **GT** | RX **x400/x500** (6400, 6500 XT), RX 5**50/560**, RX 9**050** | Arc **A3xx** | ≤ 4 GB de VRAM |
| Intermediária | GTX/RTX **xx60** (1660, 3060, 4060), GTX xx70/xx80 antigas (1070, 1080) | RX **x600/x700** (6600, 6700 XT, 7700 XT), RX 5**70/580/590**, RX 9**060** | Arc A5xx/A7xx, **B5xx** | 5–11 GB |
| Forte | RTX **xx70/xx80/xx90** (3070, 4070, 5090) | RX **x800/x900** (6800, 7900 XTX), RX 9**070** | — (a Intel não tem placa nesse degrau) | ≥ 12 GB |

Decisões e limitações:
- GTX 1070/1080 ficam como intermediárias (não há RTX, e hoje elas rendem como uma RTX 3060 — a Epic coloca a RTX 3070 como piso do "Épico").
- A VRAM sozinha é critério fraco (uma RTX 3060 tem 12 GB e é intermediária): por isso só vale quando a família não é reconhecida,
  e `GpuClassSource` diz isso em texto.
- Variantes "Laptop GPU"/"Max-Q" recebem a mesma classe da família: é uma aproximação (a versão de notebook rende menos); o
  veredito "notebook" entra separado.
- Sem leitura da placa: classe `Unknown`, nenhuma regra de placa dispara.

## Regras (`HardwareRules.ForAction`)

Cada linha: condição → veredito, motivo (chave resx `Fit.*`), mecanismo e fonte. "Neutro" é o padrão para tudo que não está aqui.

| # | Ajuste(s) do catálogo | Condição | Veredito | Mecanismo e fonte |
|---|---|---|---|---|
| 1 | `power.active-scheme.high-performance.session`, `power.processor.min-cores-parking`, `fps.power.throttling.off`, `fps.power.min-processor-state.100.ac`, `fps.power.epp.performance.ac`, `fps.power.usb-selective-suspend.off.ac`, `fps.power.pcie-aspm.off.ac`, `hw.nvme.latency-tolerance.ac`, `cpulab.power.max-state.ac`, `cpulab.power.boost-mode.ac` | na bateria | **não** (`Fit.OnBattery`) | Na bateria o Windows aplica o perfil DC (limites de potência e EcoQoS); manter clock/núcleos/links acordados só gasta bateria. Microsoft, [Quality of Service / EcoQoS](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service); [Processor power management options](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-minperformance). Mesma regra já existente em `recommendations.v1.json` (`avoid.high-performance.on-battery`). |
| 1b | idem | notebook na tomada | **não** (`Fit.LaptopHeat`) | Notebook tem limite térmico/potência de fábrica (PL1/PL2): mais consumo parado = menos margem para o turbo; o catálogo marca "mais calor em notebook" nos riscos de cada uma. Regra `avoid.high-performance.laptop` já existente. |
| 1c | idem | desktop | neutro | Não há medição publicada de FPS a mais (o catálogo é honesto: "ganho raramente aparece"); fica para "Mostrar mais opções". |
| 2 | `net.wifi.power-saving.dc-max-perf` | notebook | **sim** (`Fit.LaptopWifi`) | Na bateria o adaptador dorme entre pacotes e o ping oscila; "Desempenho máximo" mantém acordado. Intel, [Wireless adapter power saving](https://www.intel.com/content/www/us/en/support/articles/000046272/wireless/legacy-intel-wireless-products.html). A ação só escreve o valor DC: em desktop não muda nada → neutro. |
| 3 | `svc.sysmain.manual.lab`, `boot.service.sysmain.manual` | HD **ou** RAM ≤ 8 GB | **não** (`Fit.SysMainHelpsHere`) | O SysMain (Superfetch) pré-carrega o que você mais usa; é em disco lento e pouca RAM que ele mais ajuda. Regra `avoid.sysmain.hdd` já existente; o próprio catálogo diz "em NVMe com 32 GB o efeito é pequeno" (hipótese). |
| 4 | `sys.ntfs.last-access.disable` | HD | **sim** (`Fit.HddFewerWrites`) | Menos gravações de metadados a cada leitura/listagem; em HD cada gravação é uma busca mecânica. Microsoft, [fsutil behavior disablelastaccess](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior). |
| 5 | `memorylab.compression.enable`, `memorylab.browser.edge-sleepingtabs-on`, `memorylab.browser.edge-background-off`, `memorylab.browser.chrome-background-off`, `memorylab.browser.edge-startupboost-off` | RAM ≤ 8 GB | **sim** (`Fit.LowRamRelief`) | Compressão: páginas pouco usadas ficam compactadas na RAM em vez de ir para o arquivo de paginação ([Enable-MMAgent](https://learn.microsoft.com/powershell/module/mmagent/enable-mmagent)). Guias suspensas e sem segundo plano: políticas documentadas do Edge/Chrome que liberam memória de abas paradas ([SleepingTabsEnabled](https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/sleepingtabsenabled)). Com 16 GB+: neutro (ganho real só quando falta RAM). |
| 6 | `gpu.hags.lab` | só vídeo integrado | **não** (`Fit.IntegratedGpu`) | O HAGS move o agendamento para o firmware da GPU; a Microsoft o lista como recurso de placas com driver WDDM 2.7 compatível e o próprio catálogo o classifica como experimental. Regra `avoid.hags.integrated` já existente. |
| 7 | `sys.ui.transparency.off` | vídeo integrado | **sim** (`Fit.WeakGpuRelief`); notebook: **sim** (`Fit.LaptopHeat`) | A Microsoft diz que o acrílico usa GPU e energia ([Acrylic material](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)). Placa dedicada em desktop: neutro (sem FPS a mais). |
| 8 | `win.gamedvr.background-capture.disable`, `fps.gamedvr.policy.off` | placa fraca ou notebook | **sim** (`Fit.BackgroundCapture`) | A gravação contínua mantém o codificador de vídeo e I/O ativos (mecanismo no catálogo; medir com VideoBusy do PresentMon). É onde a placa já está no limite que isso pesa. |
| 9 | `gpu.nv.*` (driver NVIDIA) | placa AMD/Intel | **não** (`Fit.NotNvidia`) | NVAPI/DRS só existe no driver NVIDIA (a ação responde NotApplicable). Sem leitura do fornecedor: neutro. |
| 9b | `gpu.nv.power.max.per-app` | na bateria / notebook | **não** (`Fit.OnBattery` / `Fit.LaptopHeat`) | "Preferir desempenho máximo" impede a placa de descer de P-state com o jogo aberto: o catálogo lista consumo, calor e autonomia nos riscos (NvApiDriverSettings.h, PREFERRED_PSTATE). |
| 9c | `gpu.nv.lowlatency.on.per-app`, `gpu.nv.power.max.per-app` (desktop) | preferência "menos atraso" | **sim** (`Fit.Competitive`) | Fila de 1 quadro = imagem chega antes ([Gamescom 2019 driver](https://www.nvidia.com/en-us/geforce/news/gamescom-2019-game-ready-driver/)); é troca de atraso por FPS médio, por isso só com a preferência do usuário. |
| 10 | `fortnite.render.performance-mode` | integrada/entrada | **sim** (`Fit.WeakGpuRelief`); forte: **não** (`Fit.StrongGpuNoNeed`) | Modo desempenho = caminho ES 3.1 (menos memória e carga); a Epic o anunciou para PCs no requisito mínimo, "mais de 100% de FPS em integrada a 720p" ([anúncio](https://www.techpowerup.com/275970/fortnites-new-performance-mode-significantly-boosts-performance-on-low-end-hardware)). Em placa forte tira Nanite/Lumen sem necessidade. Intermediária: neutro (depende da resolução/monitor; medir). |
| 11 | `fortnite.render.dx12-full` | forte | **sim** (`Fit.StrongGpuUse`); integrada/entrada: **não** (`Fit.WeakGpuRelief`) | SM6 com Nanite/Lumen e pré-compilação de PSO ([Unreal: shader stuttering](https://www.unrealengine.com/en-US/tech-blog/game-engines-and-shader-stuttering-unreal-engines-solution-to-the-problem)); a Epic pede RTX 3070 / RX 6700 XT para "Épico". |
| 12 | `fortnite.visual.shadows.off` | integrada/entrada | **sim** (`Fit.WeakGpuRelief`); preferência "menos atraso": **sim** (`Fit.Competitive`) | Sombras são a opção mais cara (Épico→Baixo ≈ +80% FPS, [PC Gamer](https://www.pcgamer.com/fortnite-system-requirements-and-settings/)); jogadores competitivos as desligam por visibilidade. |
| 13 | `fortnite.fps.limit` | notebook | **sim** (`Fit.LaptopCooler`) | Limitar abaixo da taxa do monitor evita quadros inúteis: menos calor e ruído (mecanismo no catálogo; [Blur Busters G-SYNC 101](https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/)). |
| 14 | `fortnite.latency.reflex` | placa AMD/Intel: **não** (`Fit.NotNvidia`); NVIDIA + preferência: **sim** (`Fit.Competitive`) | Reflex só faz algo em NVIDIA ([NVIDIA Reflex](https://www.nvidia.com/en-us/geforce/news/reflex-low-latency-apex-legends-valorant-fortnite-cs2/)). Sem a preferência: neutro (o jogador escolhe). |
| 15 | `fortnite.display.vsync.off`, `fortnite.visual.motion-blur.off` | preferência "menos atraso" | **sim** (`Fit.Competitive`) | V-Sync sem VRR soma atraso; desfoque atrapalha ver alvos (fontes no catálogo do Fortnite). Sem a preferência: neutro. |

Regras **não** criadas, de propósito (sem base para o PC): mouse 1:1 (não depende de hardware), MPO (só com sintoma), Otimização de
Entrega, privacidade, limpeza, inicialização (o Automático já cuida; nada muda por placa/RAM), "desempenho máximo em desktop"
(sem medição), core parking em desktop (hipótese), fps cap do driver NVIDIA (depende do monitor, não da classe).

### Público das opções de jogo (`HardwareRules.ForAudience`, `GameCardOption.Audience`)

| Flag | Casa quando | Não casa quando (veredito "não") |
|---|---|---|
| `WeakGpu` | integrada ou entrada | placa forte (`Fit.StrongGpuNoNeed`) |
| `StrongGpu` | forte | integrada/entrada (`Fit.WeakGpuRelief`) |
| `LowRam` | ≤ 8 GB | — (neutro) |
| `Laptop` | notebook | — |
| `Competitive` | preferência ligada | — |
| `Nvidia` / `Amd` / `Intel` | fornecedor igual | fornecedor diferente lido (`Fit.NotNvidia` etc.) |

Um "não" vence qualquer "sim" (ex.: `WeakGpu | Nvidia` em placa AMD fraca = não). `Everyone` ou perfil vazio = neutro.
Hoje nenhum cartão registrado usa `Audience` (o Fortnite ainda monta as opções direto em `SimpleGamesPage`, que usa
`ForAction` por ação pedida); a função fica pronta para os próximos cartões (VALORANT etc.).

## Como aparece

- **Ajustes rápidos** (Otimizar): por padrão só o que é risco baixo **e** não vetado pelo hardware; cada um com o selo verde
  "Recomendado para o seu PC: …" quando há regra "sim". O vetado vai para "Mostrar mais opções" com o selo "Não indicado para
  notebook: …" no lugar da palavra de risco.
- **Automático** ("Otimizar meu PC com segurança"): nunca aplica o vetado (`AutoPilot.Select(changes, profile)`).
- **Assistente (passo a passo)**: a mudança vetada vem desmarcada; selo com o motivo. "Aplicar recomendadas" respeita o mesmo padrão.
- **Início**: "N ajustes seguros ainda não foram feitos" conta só os recomendados para o perfil.
- **Jogos**: selo discreto por opção e, quando há opção recomendada ainda desligada, "Aplicar o recomendado" (um clique, uma frase
  de confirmação listando as opções, "✓ Feito — Desfazer" que volta todas).
- **Ajustes › Perfil do PC**: a frase do perfil, o que não foi lido ("Não deu para ler: tipo do disco…") e a preferência
  "Prefiro menos atraso a imagem bonita". Mudar a preferência recalcula os selos sem reler o PC.

## Validado por teste (Linux)

`HardwareRecommendationTests`: 15 famílias de placa contra a tabela; integradas; notebook (SMBIOS, só bateria), desktop, nada lido;
cada regra acima (sim/não/neutro) e o perfil vazio; público das opções; seleção padrão; `AutoPilot.Select` com perfil; frase do
perfil em pt/en/es; chaves resx dos 19 motivos nos três idiomas; `PreferLowLatency` no settings.json.

## Falta testar em máquina real

- Abrir Otimizar, Jogos e Ajustes no Windows do responsável (GTX 1650 notebook): conferir o selo "Não indicado para notebook" no
  plano Alto desempenho e EPP, "Recomendado" no Modo desempenho do Fortnite e no Wi-Fi na bateria, e a frase do perfil.
- "Aplicar o recomendado" no Fortnite com o jogo fechado (aplicador real, desfazer).
- VRAM lida do registro em placas AMD/Intel (campo `HardwareInformation.qwMemorySize` pode faltar → classe pela família).
