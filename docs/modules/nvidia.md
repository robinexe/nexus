# Driver NVIDIA por jogo (perfis DRS) — `catalog/nvidia.v1.json`, handler `NvidiaDrsSetting`

Faz pelo NEXUS, com diário e Desfazer, os ajustes que o usuário faria em Painel de Controle NVIDIA › Gerenciar as
configurações 3D. O código está em `src/Nexus.Lab.Gpu/NvidiaDrs.cs` (lista fechada, handler, interfaces) e
`src/Nexus.Lab.Gpu/Windows/NvApiDrs.cs` (NVAPI). A tela é a seção "Driver NVIDIA por jogo" da página Placa de vídeo.

## O que faz

**Diretriz do responsável (06/11/2026): o NEXUS nunca limita o FPS.** Usuários relataram FPS "travado" depois de usar a
opção simples "Limitar FPS" (taxa do monitor − 3, ou 60 sem leitura do monitor). Agora a tela Jogos só oferece **FPS sem
limite** por jogo (`fps-cap.off` + `vsync.off`, recomendado em toda placa NVIDIA, entra no "Aplicar o recomendado") e um
cartão **Driver NVIDIA (todos os jogos)** que detecta limite de FPS ou V-Sync forçado no perfil global (posto pelo NVIDIA
App/Painel ou por versões antigas) e tira com um clique. `fps-cap.per-app` e `vsync.force-on.per-app` continuam só no
avançado, desmarcados, sem recomendação.

| Ação | Perfil | Ajuste (id) | Valor |
|---|---|---|---|
| `gpu.nv.lowlatency.on.per-app` | do jogo | Máximo de quadros pré-renderizados, `PRERENDERLIMIT_ID` 0x007BA09E | 1 (= "Modo de baixa latência: Ligado") |
| `gpu.nv.power.max.per-app` | do jogo | Modo de gerenciamento de energia, `PREFERRED_PSTATE_ID` 0x1057EB71 | 1 `PREFER_MAX` (padrão 5 `OPTIMAL_POWER`) |
| `gpu.nv.fps-cap.per-app` | do jogo | Taxa máxima de quadros, `FRL_FPS_ID` 0x10835002 | 30…240 da lista `allowedValues` (inclui "taxa − 3" de 60/75/90/100/120/144/165/180/200/240 Hz) |
| `gpu.nv.vsync.force-on.per-app` | do jogo | Sincronização vertical, `VSYNCMODE_ID` 0x00A879CF | `FORCEON` 0x47814940 = 1199655232 (padrão `PASSIVE` 0x60925292) |
| `gpu.nv.fps-cap.off.per-app` | do jogo | Taxa máxima de quadros, `FRL_FPS_ID` 0x10835002 | 0 (desligada: FPS sem limite) |
| `gpu.nv.vsync.off.per-app` | do jogo | Sincronização vertical, `VSYNCMODE_ID` 0x00A879CF | `FORCEOFF` 0x08416747 = 138504007 |
| `gpu.nv.fps-cap.off.global` | global (base) | Taxa máxima de quadros, `FRL_FPS_ID` 0x10835002 | 0 (só tirar: no global nenhum limite é aceito) |
| `gpu.nv.vsync.off.global` | global (base) | Sincronização vertical, `VSYNCMODE_ID` 0x00A879CF | `FORCEOFF` 138504007 (só desligar: forçar no global é recusado) |
| `gpu.nv.shadercache.max` | global (base) | Tamanho do cache de sombreador, `PS_SHADERDISKCACHE_MAX_SIZE_ID` 0x00AC8497 | 102400 (100 GB) ou 4294967295 (ilimitado); unidade MB |

Todas: `privileges: Admin` (gravar no banco do driver exige administrador: NVAPI responde `NVAPI_INVALID_USER_PRIVILEGE`
sem ele), `persistence: Persistent`, `reboot: GameRestart`, `classification: Conditional` (cada uma só ajuda num cenário,
escrito em `condition`). Por jogo, o usuário escolhe só o **nome do executável** (`userParameters.exe`,
`(?i)[A-Za-z0-9 ._()+\-]{1,64}\.exe`; o handler ainda recusa `..` e espaço nas pontas). Nunca um caminho.

Lista fechada (`NvidiaDrsSettings.All`): cada id tem o perfil permitido (energia e latência só por jogo; FPS e V-Sync por jogo e, no global, só para tirar limite;
cache só global), os valores que uma ação pode gravar e a faixa válida para devolver o valor antigo (do cabeçalho). Qualquer
outro id, valor, tipo ("UInt32" decimal) ou perfil é `Blocked` no Assess e recusado no Apply/Restore.

## Como funciona (NVAPI)

- `nvapi64.dll` carregada SÓ de `%WINDIR%\System32` (`NativeLibrary.TryLoad` com caminho fixo), na primeira chamada.
  `nvapi_QueryInterface(id)` devolve cada função; ids de `nvapi_interface.h`: Initialize 0x0150E828, DRS_CreateSession
  0x0694D52E, DestroySession 0xDAD9CFF8, LoadSettings 0x375DBD6B, SaveSettings 0xFCBC7E14, CreateProfile 0xCC176068,
  DeleteProfile 0x17093206, FindProfileByName 0x7E4A9A0B, GetProfileInfo 0x61CD6FD6, CreateApplication 0x4347A9DE,
  FindApplicationByName 0xEEE566B2, SetSetting 0x577DD202, GetSetting 0x73BF8338, DeleteProfileSetting 0xE4A26362,
  GetBaseProfile 0xDA8466A0. Convenção `__cdecl` (`NVAPI_INTERFACE` em `nvapi_lite_salstart.h`).
- Sem DLL, processo 32 bits, função ausente ou `NvAPI_Initialize` ≠ OK → `Unavailable()` = "Sem placa/driver NVIDIA…" e
  o handler responde `NotApplicable`. Nada mais é chamado.
- Estruturas montadas byte a byte (memória nativa zerada) com os deslocamentos de `nvapi.h`:
  `NVDRS_SETTING_V1` (`#pragma pack(4)`) = 12320 bytes, versão 0x13020 (id em 4100, tipo 4104, local 4108,
  isCurrentPredefined 4112, valor predefinido 4120, valor atual 8220); `NVDRS_APPLICATION_V4` = 20492 bytes, 0x4500C
  (nome do app em 8), com recuo para V3 = 16396 bytes, 0x3400C se o driver responder `INCOMPATIBLE_STRUCT_VERSION`;
  `NVDRS_PROFILE_V1` = 4116 bytes, 0x11014 (nome em 4, numOfApps 4108, numOfSettings 4112). Conferido compilando os
  typedefs copiados do cabeçalho oficial com gcc x86-64 (mesmo layout do MSVC para estes tipos).
- Cada operação: CreateSession → LoadSettings → (perfil) → Get/Set/DeleteProfileSetting → SaveSettings (só escrita) →
  DestroySession, sob um lock do processo.
- Perfil do jogo: `FindApplicationByName(exe)`. Achou → usa esse perfil (muitas vezes o predefinido da NVIDIA para o jogo,
  como o Painel faz). Não achou (`EXECUTABLE_NOT_FOUND`) → na gravação cria o perfil **`NEXUS - exe`** com o executável.
  `EXECUTABLE_PATH_IS_AMBIGUOUS` (dois perfis com o mesmo nome de exe) → `Blocked` com o motivo.

## Detect / Apply / Restore

- **Detect**: `UInt32:<valor>` quando o perfil tem o ajuste (valor do usuário, origem `Explicit`; ou o predefinido da NVIDIA,
  origem `Inherited` e `Detail["predefined"]="1"`). Ausente quando o perfil não tem o ajuste ou o jogo não está em perfil
  nenhum (vale o global/padrão do driver).
- **Apply**: `SetSetting` (DWORD) e `SaveSettings`.
- **Restore**: original ausente ou predefinido → `DeleteProfileSetting` (a doc da NVIDIA: "deletes a setting or sets it
  back to predefined value"); se o perfil é `NEXUS - exe`, ficou com 0 ajustes e só o próprio executável, é apagado.
  Original do usuário → `SetSetting(original)`, depois de validar a faixa do cabeçalho (`IRestoreValidator.CheckRestore`).
- Duas ações no mesmo jogo dividem o perfil `NEXUS - exe`; ele só some quando a última é desfeita.

## Evidências

- `github.com/NVIDIA/nvapi`: `NvApiDriverSettings.h` (ids e valores; licença MIT), `nvapi.h` (estruturas, versões,
  assinaturas), `nvapi_interface.h` (ids das funções), `nvapi_lite_common.h` (códigos de erro, `MAKE_NVAPI_VERSION`).
- nvidia.com/en-us/geforce/news/gamescom-2019-game-ready-driver/ — Low Latency "On: Limits the number of queued frames
  to 1"; DX9/DX11 (em DX12/Vulkan o jogo decide); mais efeito com GPU no limite entre 60 e 100 FPS.
- Notas do driver 441.87 (nvidia.com/de-de/drivers/details/156395): Max Frame Rate. Notas do 496.13: Shader Cache Size.
- blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/ — G-SYNC: V-Sync ligado no painel NVIDIA, limite
  "(a minimum of) 3 FPS below" a taxa do monitor, Low Latency "On" com limitador.
- Unidade do 0x00AC8497 (MB) e os valores 10 GB = 0x2800, 100 GB = 0x19000, ilimitado = 0xFFFFFFFF: NVIDIA Profile
  Inspector (`CustomSettingNames.xml`), fonte comunitária. O cabeçalho só dá MIN 0, MAX 0xFFFFFFFF e padrão 0x4000
  (= 16 GB em MB).

## Riscos

- Energia máxima: mais calor/consumo com o jogo aberto (nunca no global: recusado no catálogo e no handler).
- Baixa latência "Ligado" com CPU no limite pode baixar o FPS médio. Sem efeito em DX12/Vulkan ou com Reflex.
- Limite de FPS abaixo do que a placa entrega reduz FPS de propósito; limitador do próprio jogo tem menos atraso.
- V-Sync ligado SEM G-SYNC (ou sem limite) prende o FPS na taxa do monitor e aumenta o atraso. A página só envia o V-Sync
  junto de um limite de FPS (marcado agora ou já presente no perfil).
- Cache ilimitado: `%LOCALAPPDATA%\NVIDIA\DXCache` pode crescer dezenas de GB. Atualizar o driver limpa o cache.
- Reinstalar o driver NVIDIA com "instalação limpa" apaga o banco de perfis; depois disso o Desfazer pode encontrar o
  perfil diferente do que o diário registrou (o executor relê com Detect e avisa).

## O que NÃO faz (e por quê)

- "Ultra" do Modo de baixa latência: não há id público no cabeçalho (rejeitado em `rejected`).
- Energia máxima no perfil global: deixaria a placa sem descansar fora dos jogos.
- Ids arbitrários: só os 5 da lista. Nada de importar/exportar perfis inteiros, nem `RestoreAllDefaults`/`RestoreProfileDefault`.
- Caminho completo do executável: o perfil usa o nome do arquivo, como o Painel e o Profile Inspector.
- AMD/Intel: não têm equivalente público estável sem SDK; a seção mostra o motivo e fica inativa.

## Interface

A página lê os valores atuais pelo `INvidiaDrsReader` (`NvidiaDrsFactory.Reader()`); as escritas existem só em
`INvidiaDrs`, usadas pelo handler no aplicador. `NvidiaDrsTests.Desktop_never_references_the_drs_writes` inspeciona a dll
do Desktop e falha se aparecer referência a `SetUserValue`/`ClearUserValue`/`Apply`/`Restore` desses tipos.

## Testes

- `tests/Nexus.Tests/Modules/NvidiaDrsTests.cs` (Unit, DRS falso): perfil criado e removido, perfil compartilhado por duas
  ações, predefinido da NVIDIA de volta, valor do usuário de volta, cache no global, "já configurado" sem escrita, sem NVIDIA
  → NotApplicable, executável ambíguo → Blocked, lista fechada (ids/valores/perfis/tipos), faixas do Restore, regex do
  executável, sugestão "taxa − 3", catálogo carregando/instanciando e listas da página iguais ao catálogo.
- `tests/Nexus.IntegrationTests/Modules/NvidiaDrsIntegrationTests.cs` (VmIntegration): na VM sem NVIDIA a NVAPI falha com
  elegância e as três ações terminam NotApplicable (Blocked sem administrador). Com NVIDIA, só lê.

## Como validar num PC com NVIDIA

1. Abra NEXUS › Laboratório › Placa de vídeo. A seção "Driver NVIDIA por jogo" deve mostrar o seletor de jogos (Steam/Epic).
2. Escolha um jogo: a lista mostra o perfil do driver (nome do perfil NVIDIA ou "nenhum") e os 4 valores atuais.
3. Marque "Modo de baixa latência" e "Limitar o FPS" (a sugestão vem da taxa do monitor principal), clique em
   "Aplicar no driver", aceite o UAC. A mensagem deve dizer "verificado".
4. Confira no Painel de Controle NVIDIA › Gerenciar as configurações 3D › Configurações de programa › (o jogo ou
   "NEXUS - jogo.exe"): "Modo de baixa latência = Ligado" e "Taxa máxima de quadros = N FPS". Ou no NVIDIA Profile Inspector.
5. Clique em Desfazer (na página ou em Aplicar). Os valores voltam; se o NEXUS criou o perfil, ele some.
6. Cache: escolha 100 GB, aplique, confira "Tamanho do cache de sombreador" nas Configurações globais; Desfazer volta a
   "Padrão do driver".
