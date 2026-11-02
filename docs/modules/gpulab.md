# Laboratório da placa de vídeo (`src/Nexus.Lab.Gpu`, página "Placa de vídeo" no grupo Laboratório)

"Engenharia reversa da GPU" sem driver de kernel: descobrir **o que a placa é** e **por que ela não entrega tudo**, com
evidência medida e fonte. Toda leitura é feita pela biblioteca/página; toda mudança passa pelo aplicador (broker) como ação de
catálogo, com diário e Desfazer. Nenhum pacote NuGet foi adicionado (DXGI e NVML por ponteiros de função).

## O que lê (e de onde)

| Área | O que mostra | Fonte |
|---|---|---|
| Identidade | todas as GPUs, VRAM dedicada/compartilhada, LUID, vendor/device/subsystem, qual é a de **alto desempenho** e a de economia | DXGI: `CreateDXGIFactory1` → `IDXGIFactory1::EnumAdapters1` + `GetDesc1`; `IDXGIFactory6::EnumAdapterByGpuPreference` (1803+); `IDXGIAdapter3::QueryVideoMemoryInfo` (orçamento/uso local). COM cru pela vtable (`Windows/Dxgi.cs`) |
| Arquitetura | NVIDIA Maxwell…Blackwell, AMD GCN/Polaris/Vega/RDNA 1–4 (inclui iGPUs Vega/RDNA2/RDNA3), Intel Gen9–11/Xe-LP/Xe-LPG/Xe2/Alchemist/Battlemage | Tabela de faixas de Device ID (`GpuIdentity`), deduzida de pci.ids e dos INFs públicos dos drivers. Fora das faixas: "não identificada" (nunca chuta) |
| Driver | versão Windows (32.0.15.6636), versão do fabricante (NVIDIA **566.36**, Intel 101.5590), data, idade | `DEVPKEY_Device_DriverVersion/DriverDate/DriverProvider` via `CM_Get_DevNode_PropertyW`. Regra NVIDIA: 5 últimos dígitos de c+d. AMD: o número do Windows **não** mapeia para o nome do Adrenalin; dizemos isso |
| Notebook híbrido | iGPU + dGPU; qual GPU desenha a tela interna (Optimus via iGPU × MUX/Advanced Optimus direto na dGPU) | `QueryDisplayConfig` (adaptador de cada caminho) + tipo de saída (eDP/LVDS/interna) |
| PCIe | geração e largura **atuais e máximas** da placa e o máximo da **porta pai** (teto da plataforma) | `DEVPKEY_PciDevice_CurrentLinkSpeed(9)/CurrentLinkWidth(10)/MaxLinkSpeed(11)/MaxLinkWidth(12)` da placa e do `DEVPKEY_Device_Parent`; NVML (`nvmlDeviceGetCurrPcieLinkGeneration` etc.) quando o Windows não expõe |
| HAGS | estado **efetivo** do driver e o valor configurado | `D3DKMTOpenAdapterFromLuid` + `D3DKMTQueryAdapterInfo(KMTQAITYPE_WDDM_2_7_CAPS = 70)` (bits HwSchSupported/HwSchEnabled); registro `HwSchMode` |
| Resizable BAR | ligado/desligado (só NVIDIA) | NVML `nvmlDeviceGetBAR1MemoryInfo`: BAR1 ≤ ~256 MB = desligado; ≥ metade da VRAM = ligado (heurística documentada: com ReBAR a janela cobre a VRAM) |
| MPO | desligado por registro | `HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5` |
| NVIDIA | clocks atuais/máximos (gráfico, memória), limite de potência atual/padrão/faixa/aplicado, consumo, temperatura, limiar de redução e desligamento, ventoinha, uso, P-state, VRAM, **motivos de limitação** traduzidos | NVML (`nvml.dll` do driver) |
| Monitores | cada monitor: nome, conexão, quem desenha, modo atual, maior taxa na mesma resolução, maior resolução listada, HDR (suportado/ligado), bits por cor | `EnumDisplayDevices`, `EnumDisplaySettingsEx` (modos filtrados pelo que o monitor anuncia), `DisplayConfigGetDeviceInfo` (nome, `GET_ADVANCED_COLOR_INFO`) |
| GPU por app | preferências existentes e jogos instalados | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`; Steam `libraryfolders.vdf` + `appmanifest_*.acf` (exe = maior .exe raso da pasta, `-Shipping.exe` do Unreal; marcado "provável"); Epic `ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` (`LaunchExecutable`, exato) |
| Energia | na bateria / tem bateria | `GetSystemPowerStatus` |

### NVML: como é carregada
Só de caminho fixo: `%SystemRoot%\System32\nvml.dll` e, se não houver, `System32\DriverStore\FileRepository\nv*\nvml.dll`
(a mais recente). O caminho precisa estar dentro de `Environment.SystemDirectory` (protegido contra escrita de usuário comum).
`NativeLibrary.TryLoad(caminho completo)`; **nunca** PATH nem a pasta do NEXUS. Só funções de leitura são resolvidas
(nenhuma `nvmlDeviceSet*`). Funções ausentes em drivers antigos ficam nulas e o campo aparece como "—".

### Motivos de limitação (bitmask `nvmlDeviceGetCurrentClocksEventReasons`, antigo `...ThrottleReasons`)
| Bit | Texto |
|---|---|
| 0x1 GpuIdle | ociosa (normal) |
| 0x2 ApplicationsClocksSetting | clock fixado por aplicativo |
| 0x4 SwPowerCap | **limite de potência** |
| 0x8 HwSlowdown | redução por hardware (temperatura/fonte) |
| 0x10 SyncBoost | sincronia com outra GPU |
| 0x20 SwThermalSlowdown | **limite térmico** do driver |
| 0x40 HwThermalSlowdown | proteção térmica por hardware |
| 0x80 HwPowerBrakeSlowdown | freio de potência externo |
| 0x100 DisplayClockSetting | clock limitado pela configuração de vídeo |

Categoria dominante de uma amostra: térmico > potência > hardware > configuração > ociosa.

## Gravação "enquanto jogo" (60 s)

Gerar carga de GPU sem pacote (D3D11 compute) não compensou o risco/peso; a alternativa honesta: o usuário abre o jogo, clica
em **Gravar**, tem 10 s para voltar ao jogo e o NEXUS lê 1×/s por 60 s (NVML; em AMD/Intel, uso e VRAM pelos contadores PDH
"GPU Engine" de `Nexus.Monitoring`, casados pelo LUID). Análise (`RecordingAnalyzer`, pura e testada):

- "sob carga" = uso ≥ 60%; menos de 10 s sob carga → "grave de novo com o jogo aberto" (e a dica de gargalo fora da GPU).
- potência/temperatura/hardware = fração das amostras sob carga com aquele motivo (≥ 30% potência; ≥ 10% térmico/hardware).
- PCIe abaixo do possível sob carga (geração menor que o mínimo entre placa e porta em ≥ 50% do tempo; largura menor que a possível).
- clock oscilando (coeficiente de variação > 8%); clock < 70% do máximo **sem** motivo informado → o jogo não pede mais (FPS limitado, V-Sync, CPU).
- VRAM ≥ 95% em ≥ 10% do tempo.
- Notebook: o texto explica que limite de potência é desenho de fábrica (carregador, modo Desempenho do fabricante — NitroSense no Acer —, base ventilada).
- O gráfico mostra clock (do máximo), uso, temperatura (0–100 °C) e potência (do limite), com faixas de "limitada por potência" e "por temperatura/hardware".

## Diagnóstico (`GpuDiagnostics.Evaluate`) e ações

| Achado | Evidência | Ação |
|---|---|---|
| Monitor abaixo da taxa máxima (ex.: 144 Hz a 60 Hz) | modo atual × maior taxa listada na mesma resolução | **`gpulab.display.mode@1`** (Usar N Hz) |
| Jogo sem preferência / na integrada (só em híbrido) | `UserGpuPreferences` + manifesto da loja | **`gpulab.app.gpu-preference@1`** (handler do núcleo `DirectXUserGpuPreference`) |
| Driver com mais de 6 meses / 1 ano / 2 anos | data do driver | abre a página **oficial** (NVIDIA/AMD/Intel) depois de confirmação; nunca baixa nada |
| PCIe mais estreito que placa e porta permitem | link atual × máximos (teto da GPU corrigido pela arquitetura: uma Turing informada como 4.0 vira 3.0) | conselho (slot/encaixe); em notebook é **informação**: largura é fiação de fábrica |
| Tela interna pela iGPU | `QueryDisplayConfig` | conselho (MUX/modo dedicado do fabricante, monitor na porta da dGPU) |
| HAGS desligado numa RTX 40/50 | D3DKMT / registro | `gpu.hags.lab@1` = 2 (Admin, reinício) — só aqui, porque a geração de quadros do DLSS exige; nas outras GPUs HAGS é só informação (efeito pequeno e variável) |
| Modo de Jogo desligado | `AutoGameModeEnabled = 0` | `win.gamemode.enable@1` |
| ReBAR desligado/ligado (RTX 30+, RX 6000+, Arc) | NVML BAR1 (NVIDIA) ou D3D12 `GPUUploadHeapSupported` (qualquer fabricante; só `true` prova "ligado") | conselho (BIOS: Above 4G + Re-Size BAR); alerta forte só na Intel Arc, inclusive "confira na BIOS" sem afirmar estado |
| Limite de potência abaixo do padrão | NVML | conselho (voltar no programa que mudou) |
| Na bateria | `GetSystemPowerStatus` | conselho |
| MPO desligado | `OverlayTestMode = 5` | só informação (não mexemos) |

### Resizable BAR: como é lido (`HardwareCaps.ResizableBar`)

- **NVIDIA:** tamanho da janela BAR1 pelo NVML (`nvmlDeviceGetBAR1MemoryInfo`, o mesmo número de "BAR1 Memory Usage" do
  `nvidia-smi -q`). ≤ 300 MB = desligado (256 MB é a janela clássica); ≥ metade da VRAM = ligado.
- **Qualquer GPU dedicada:** um dispositivo D3D12 temporário (`D3D12CreateDevice` no adaptador, feature level 11_0) e
  `CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16 = 45)`. A especificação das GPU upload heaps
  (microsoft.github.io/DirectX-Specs/d3d/D3D12GPUUploadHeaps.html) só expõe o recurso quando a VRAM inteira é visível pela
  CPU, ou seja, ReBAR ligado numa dGPU. `false` não prova nada (runtime/driver antigos não oferecem). A consulta acorda a
  dGPU do notebook por um instante; por isso `GpuLabScanner.Scan(..., probeD3D12: true)` é opcional (Placa de vídeo,
  Assistente e "só você pode fazer" ligam).

### "Só você pode fazer" (`OwnerChecks`, em Nexus.HardwareDiscovery)

Pontos que dependem de BIOS, hardware ou software do fabricante. Só informam (nenhuma ação do catálogo), com explicação,
ganho medido e fonte; aparecem no Diagnóstico, no Hardware e na lista do Assistente. Montados por `OwnerFactsBuilder`
(Nexus.Assistant) a partir do relatório da GPU e do SMBIOS.

| Id | Regra | Ganho citado | Fonte |
|---|---|---|---|
| `owner.rebar` | Arc desligado/desconhecido = atenção; NVIDIA/AMD desligado = informação; sem leitura = nada | Arc A770: 76–80% do normal sem ReBAR; NVIDIA 0–2%; AMD ~7% | Tom's Hardware (TechPowerUp); TechSpot |
| `owner.xmp` | em uso < 97% e ≥ 200 MT/s abaixo do anunciado (SMBIOS ou número de peça). Desktop = atenção; notebook = informação | depende do jogo | Custom PC (EXPO) |
| `owner.mux` | notebook com iGPU + dGPU NVIDIA/AMD; quem desenha a tela interna (QueryDisplayConfig) | +9% em média (ASUS), varia muito (Laptop Mag) | ASUS ROG; Laptop Mag |
| `owner.pcie` | largura atual < largura da **própria GPU**; notebook = informação ("projeto do notebook"); slot estreito = informação; desktop = atenção | ~2% numa RTX 4090 em 4.0 x8 | TechPowerUp |

Limitação: o Windows só vê a velocidade que o firmware anuncia (em geral JEDEC). Sem número de peça decodificado e com o
anunciado igual ao em uso, `owner.xmp` não acusa nada.

### Ajustes por jogo (seção "Ajustes por jogo")

- **Otimizações para jogos em janela neste jogo:** `win.windowed-game-optimizations.per-app@1` com `gameExePath` (mesma regra
  de `GameLibraries.ExePattern`). Só DX10/DX11 em janela/sem bordas. Separado da opção global do Windows.
- **CS2 fps_max:** `cs2.cfg.fps_max@1` com `cs2CfgDir` (só `...\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg`,
  sem `.`/`..`) e um valor de `allowedValues`. O aplicador só grava com um dump do `cvarlist` da versão instalada: o
  `console.log` em `game\csgo` (gravado com `-condebug`) com o rodapé "N total convars/concommands". Sem ele: bloqueado, com
  instrução. Formato do dump e leitura automática do `autoexec.cfg` pelo CS2 ainda a confirmar num PC real.

## Handler `GpuLabDisplayMode`

- Alvo: `device` (`\\.\DISPLAYn`, regex `\\\\\.\\DISPLAY[0-9]{1,2}`) e `mode` (`LxA@Hz`, regex `[0-9]{3,5}x[0-9]{3,5}@[0-9]{2,3}`;
  `DisplayMode.TryParse` ainda limita 640×480..16384² e 23..500 Hz). `desiredValue = "aplicado"`, `desiredType = DisplayMode`.
- **Assess** recusa: aplicador em outra conta (o modo de vídeo é da sessão do usuário), monitor ausente (não aplicável), e
  **qualquer modo que o monitor não lista** em `EnumDisplaySettingsEx` (mesma resolução e taxa, progressivo).
- **Detect**: `"aplicado"` quando o modo atual é o alvo; senão o modo atual (`"1920x1080@60"`), que é o que vai para o diário.
- **Apply**: `ChangeDisplaySettingsEx(..., CDS_TEST)` primeiro; só com sucesso chama de novo com `CDS_UPDATEREGISTRY`. Profundidade
  de cor e posição mantidas (parte do `DEVMODE` atual). A página só oferece trocar a **taxa** na resolução atual.
- **Restore**: volta ao modo do diário, também com `CDS_TEST` antes, e só se o monitor ainda o lista (`IRestoreValidator`);
  monitor ausente no plano → nada a fazer.
- Nível **User** (aplicador do próprio usuário, na mesma sessão).

## Riscos

- Taxa alta com cabo antigo (HDMI 1.4, adaptador passivo): tela piscando/preta. Mitigação: só modos que o monitor anuncia,
  teste do driver antes, texto de confirmação explica e Desfazer volta. **Reversão automática em 15 s** (desde 02/11), como a
  janela do próprio Windows: depois de trocar, um cartão por cima da página pergunta "Manter esta taxa? Volta sozinho em 15 s"
  (Manter / Voltar agora); sem resposta, a página desfaz pelo aplicador com `IModuleHost.RevertWithoutConfirmationAsync`
  (mesmo diário e releitura do Desfazer, só sem a segunda caixa de confirmação, já aceita ao trocar). Lógica pura e testada em
  `KeepChangeCountdown` (a primeira resposta vence). Desfazer manual durante a contagem cancela a reversão automática.
- Preferência de GPU por app: mais consumo de bateria; jogos OpenGL/Vulkan antigos podem seguir o painel do driver.
- HAGS: exige reinício; efeito varia por driver (por isso só é oferecido onde é requisito).
- PCIe de placas AMD RX 6000/7000: o chip tem um switch PCIe interno; o link lido pode ser o interno (sempre x16). Documentado
  como limitação; a análise sob carga continua válida para NVIDIA (NVML lê o link externo).

## O que NÃO faz (e por quê)

- **Não** faz overclock/undervolt nem muda limite de potência (NVML `Set*` nem é resolvida): risco de instabilidade e garantia.
- **Não** muda resolução nem escala (só taxa). Resolução abaixo da nativa é só informada (pode ser DSR/VSR ou escolha do usuário).
- **Não** baixa nem instala driver; só abre a página oficial depois de confirmação.
- **Não** lê clocks/potência/motivos de AMD e Intel: ADLX (AMD) e IGCL (Intel) exigem SDK/redistribuível próprio. A página diz o
  que falta em vez de inventar; uso e VRAM vêm dos contadores do Windows.
- **Não** liga ReBAR, MUX ou Advanced Optimus (BIOS / software do fabricante).
- **Não** desliga MPO nem mexe em `OverlayTestMode` (é correção de flicker, não otimização).
- VRR/G-Sync: o Windows não expõe o estado de forma pública e confiável sem a API do fabricante; não é mostrado.

## Testes

`tests/Nexus.Tests/Modules/GpuLabTests.cs` (Unit, roda em Linux): tabela de arquiteturas (23 ids reais), id desconhecido,
iGPU × dGPU, hardware id, versões de driver (NVIDIA/Intel/AMD), datas e idade, PCIe (códigos, repouso, teto da plataforma),
bitmask de motivos, análise de gravações sintéticas (potência + faixas, térmico + PCIe + VRAM + oscilação, sem carga, limpa),
melhor modo do monitor, regex de modo/dispositivo, handler de modo com backend falso (Detect/Apply/Restore, CDS_TEST antes,
recusa de modo não listado, outra conta, teste do driver falhando, restauração inválida, ida e volta pelo `ActionExecutor`),
preferência de GPU pelo handler do núcleo preservando tokens, regra de caminho de exe igual à do catálogo, manifestos
Steam/Epic, escolha do exe, diagnóstico do Acer Nitro do responsável, HAGS só para RTX 40/50, catálogo.
`tests/Nexus.IntegrationTests/Modules/GpuLabIntegrationTests.cs` (VmIntegration): leitura real sem exceção, preferência de
GPU gravada e removida pelo broker, modo de vídeo recusado quando não listado e ida e volta de uma taxa listada (se a VM tiver).

## Como validar no Windows

1. Abrir NEXUS → Laboratório → Placa de vídeo. O topo deve dizer algo como
   "NVIDIA GeForce RTX 3050 Laptop GPU · Ampere · 4 GB · driver 566.36 de 2024-12" e "A tela interna é desenhada pela AMD Radeon(TM) Graphics…".
2. Conferir o cartão PCIe (em repouso pode estar em 1.0; placa 4.0 x8, porta 3.0 x8 no Ryzen 5600H = teto da plataforma).
3. Se o painel do notebook for de 144 Hz e estiver a 60 Hz, aparece "Usar 144 Hz": aplicar, conferir em Configurações > Tela, Desfazer.
4. Abrir um jogo, voltar, "Gravar enquanto jogo", voltar ao jogo por 60 s. Esperado num notebook: limite de potência na maior parte do tempo.
5. Na lista de jogos, "Usar a dedicada" num jogo sem preferência; conferir em Configurações > Tela > Elementos gráficos; Desfazer.
