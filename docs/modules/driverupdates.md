# Módulo Atualizar drivers (`Nexus.DriverUpdates`)

Página **Atualizar drivers** (grupo "Sistema", logo depois de Ferramentas do sistema). Responde ao pedido "aonde está a
página de atualização de drivers". Duas seções:

1. **Windows Update**: procura atualizações de driver oferecidas pela Microsoft para este PC (inclusive as "atualizações
   opcionais de driver"), mostra o que cada uma troca e instala as escolhidas pelo aplicador, com backup do driver atual e
   Desfazer.
2. **Placa de vídeo**: versão e idade do driver de cada GPU NVIDIA/AMD/Intel (leitura do Laboratório de GPU). Para NVIDIA e
   AMD o NEXUS **procura a versão mais nova no serviço oficial do fabricante, baixa e instala no próprio app** (pedido do
   responsável em 01/11: "baixado e instalado direto no nosso app, sem precisar ir para página externa"), com progresso no
   cartão, assinatura digital conferida, backup do driver atual e Desfazer. A página oficial e o app oficial ficam como links
   pequenos. Intel: só o link do assistente oficial. Explica quando vale atualizar e quando não mexer.

Só fontes oficiais. Nada de "driver booster", nada de baixar de site agregador (está em `rejected` no catálogo).

- Leitura (busca no Windows Update, inventário de dispositivos, GPUs, reinício pendente): direto da biblioteca, somente
  leitura (`DriverUpdatesReader.Scan`), em `Task.Run`, com tempo limite (3 min) e botão Cancelar.
- Escrita: só pelo aplicador, ação `driverupdates.install` do catálogo `catalog/driverupdates.v1.json` (`Host.ApplyAsync` /
  `Host.RevertAsync`). Administrador: UAC + ponto de restauração automático do aplicador.
- Escrita do driver de vídeo: só pelo aplicador, ação `driverupdates.gpu.install` (seção "Driver de vídeo pelo NEXUS" abaixo).
  A interface só consulta (rede, leitura) e grava o instalador na pasta de downloads do usuário.
- Exceção controlada (abrir o que o usuário vê e controla, depois de `Host.Confirm`): página oficial do fabricante da GPU
  (`DriverInfo.OfficialDownloadPage` do Lab.Gpu), app oficial em caminho fixo de Program Files, "mais informações" da
  atualização (só https em domínios da Microsoft), Configurações > Windows Update > Atualizações opcionais
  (`DriverVendorTools`) e o "Auto-Detect and Install" da AMD (só quando o ramo da placa AMD não é identificado; baixado de
  drivers.amd.com, assinatura da AMD conferida, aberto com a janela do próprio instalador da AMD — `AmdAutoDetectTool`).

## Ação do catálogo

| Id | Handler | Privilégio | O que faz | Desfazer |
|---|---|---|---|---|
| `driverupdates.install` | `DriverUpdateInstall` | Admin | Re-busca e confere a oferta → `pnputil /export-driver` do driver atual → manifesto → `UpdateDownloader.Download` + `UpdateInstaller.Install` (WUA) | `pnputil /add-driver <inf>` + `UpdateDriverForPlugAndPlayDevicesW(INSTALLFLAG_FORCE)` no ID que o driver anterior casava; ou o INF do próprio Windows |
| `driverupdates.gpu.install` | `DriverUpdatesVendorInstall` | Admin | Copia o instalador baixado para a pasta protegida → SHA-256 da cópia = o confirmado → Authenticode da cópia (NVIDIA Corporation / Advanced Micro Devices) → `pnputil /export-driver` do driver atual da placa → manifesto → instalador do fabricante em modo silencioso (tempo limite) | O mesmo do Windows Update: pacote anterior do backup com instalação forçada |

Página: "Instalar todas (N)" no grupo das recomendadas (uma confirmação, um aviso do Windows, um Desfazer), "Instalar" em cada
oferta e "Instalar selecionadas". O progresso do aplicador aparece na tela de progresso (ApplyAsync).

`userParameters`: `updateId` = `[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}` (UpdateID do WUA, minúsculo) e
`updateRevision` = `[0-9]{1,6}`. O alvo é `{ updateId, revision }`. Lote: a mesma ação várias vezes no pedido, uma por
atualização (até o limite de 64 do aplicador) — uma confirmação, um UAC, um ponto de restauração, um Desfazer.

### Estados (Detect)

`DriverUpdate:instalada` quando algum dispositivo da oferta usa a versão oferecida (comparação numérica a.b.c.d), ou quando
o WUA instalou e pediu reinício que ainda não aconteceu (manifesto com `InstalledUtc` posterior ao último boot).
Senão `DriverUpdate:pendente`. Detalhes: versão oferecida e versão atual de cada dispositivo.

### Antes de baixar (Assess e Apply)

1. Busca no WUA (`IsInstalled=0 and Type='Driver' and IsHidden=0`; cache de 15 min no aplicador para o plano e a execução;
   o Apply invalida o cache e busca de novo imediatamente antes de baixar).
2. O ID precisa estar na busca, com a mesma revisão, `Type = Driver` e termos de licença já aceitos (o NEXUS não aceita
   termos por você). Sem versão no título ("Fabricante - Classe - a.b.c.d") a instalação é recusada: sem versão não há como
   verificar.
3. Algum dispositivo presente precisa casar o `DriverHardwareID` da oferta (hardware ID ou compatible ID). Só sem hardware
   ID na oferta cai para o modelo.
4. Na instalação, o WUA busca de novo por `UpdateID='…' and RevisionNumber=N` (valores já validados por regex) e confere
   tipo/instalado/termos outra vez.

### "O que reverter" (decidido ANTES de instalar, `RollbackRules.Plan`)

| Driver atual do dispositivo | Origem do Desfazer | Observação |
|---|---|---|
| `oemNN.inf` (terceiros) | **Backup**: `pnputil /export-driver oemNN.inf DataDirectory\backup\drivers\wu-<UpdateID>\` | Export que não produz o INF original = nada é instalado |
| INF do Windows (`display.inf`, `hdaudio.inf`…) | **Inbox**: `%SystemRoot%\INF\<nome>.inf` | Nome conferido por regex; fica sempre no PC |
| Sem driver / sem `MatchingDeviceId` / nome estranho | **Nenhum** | A confirmação diz "SEM DESFAZER AUTOMÁTICO" e indica a Restauração do Sistema; o Desfazer é recusado com esse motivo |

Por que forçar no Desfazer: `pnputil /add-driver /install` sozinho **não** volta a uma versão mais antiga (o Windows
prefere a mais nova). Por isso o Desfazer usa `pnputil /add-driver <inf>` **sem** `/install` (só guarda o pacote no
repositório; 0 ou 3010) e depois a instalação forçada. Antes (correção desta rodada) usava `/install`, que não atualiza
nenhum dispositivo quando o driver instalado é mais novo/igual e pode sair com código ≠ 0 (ex.: 259), derrubando o Desfazer
antes de chegar à instalação forçada. `UpdateDriverForPlugAndPlayDevicesW` com `INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE` é a API
oficial do "Atualizar driver" do Gerenciador de Dispositivos, aplicada ao `MatchingDeviceId` que o driver anterior casava.

Backup anterior com o mesmo UpdateID nunca é sobrescrito (renomeado com sufixo de data). O manifesto
(`nexus-manifest.json`) guarda origem, INF publicado e original, versão anterior, ID casado, dispositivos e o resultado do WUA.
`CheckRestore` revalida ID, original, manifesto e a presença do INF no backup antes de qualquer escrita.

### Reinício

Detectado pelo `Microsoft.Update.SystemInfo.RebootRequired` e pelas chaves `WindowsUpdate\Auto Update\RebootRequired` e
`Component Based Servicing\RebootPending`. A página avisa ao abrir, depois de procurar e depois de instalar. Driver que
pediu reinício conta como instalado até o próximo boot; se depois do boot a versão não mudou, volta a "pendente".

## Dispositivos sem driver ("ver se falta algum")

Ao abrir a página (sem internet, sem administrador) o NEXUS lê o **código de problema** de cada dispositivo presente — o mesmo
número do ponto de exclamação amarelo do Gerenciador de Dispositivos — com `CM_Get_DevNode_Status` (cfgmgr32) sobre o `DevInst`
da SetupAPI (`DeviceDriver.ProblemCode`; 0 = funcionando, null = não lido). `DeviceProblems` traduz pela tabela oficial
"Device Manager error messages" (<https://learn.microsoft.com/windows-hardware/drivers/install/device-manager-error-messages>):

| Grupo | Códigos | O cartão mostra? |
|---|---|---|
| Sem driver | 1, 18, 28 (`CM_PROB_FAILED_INSTALL`) | Sim |
| Driver que não carrega | 3, 10, 19, 31, 32, 37, 38, 39, 40, 41, 43, 48, 52, 54 | Sim |
| Outro (recursos, reinício, dependência) | 12, 14, 21, 24, 33, 34, 35, 42, 44, 49, 50, 51… | Não (conta só) |
| Desativado (você, BIOS, remoção segura) | 22, 29, 47 | Não (não é defeito) |

Cartão **"Dispositivos sem driver"**: nome, "Código N", significado em português e hardware ID; botões **Procurar no Windows
Update** (a busca normal da página: o WU oferece drivers também para aparelhos sem driver, às vezes como opcional; essas ofertas
aparecem com "instalada: nenhum driver" e "sem desfazer automático", porque não há driver anterior) e **Abrir atualizações
opcionais**. Sem nenhum: uma linha verde "Nenhum dispositivo sem driver (N conferidos)". Se a busca não trouxer oferta para
eles, o resumo diz para procurar no site do fabricante (o hardware ID ajuda). O NEXUS não baixa driver de outro lugar.
O assistente (etapa Drivers) mostra o mesmo número e, se houver, uma ação para o usuário. O autoteste conta os quatro grupos.

## Erros do Windows Update (mensagens claras)

`WindowsUpdateQueries.DescribeError`: sem conexão (0x8024402C, 0x80072EE2/EE7/EFD/EFE/F8F, 0x8024401C…), serviço
desativado (0x80070422, 0x8024001E), política da empresa (0x8024002E, 0x80240025, 0x8024500C, 0x80244018), ocupado
(0x80240016…), tempo limite e cancelamento. O NEXUS não liga o serviço nem mexe em política.

## Cancelamento e tempo limite

O WUA não tem cancelamento síncrono. A busca roda numa thread própria; no tempo limite ou ao cancelar a página para de
esperar e avisa — a busca do Windows termina sozinha em segundo plano (é só leitura). Instalação: até 45 min; passou disso,
o aplicador relata a falha dizendo que o Windows Update pode continuar em segundo plano.

## Driver de vídeo pelo NEXUS (NVIDIA e AMD)

Ao abrir a página, o NEXUS consulta (só leitura) a versão mais nova para cada placa NVIDIA/AMD; o botão **Procurar versão nova**
repete. Resultado no cartão:

- **Em dia**: "Em dia: 621.01 é a mais nova." (verde).
- **Há versão nova**: botão principal **Baixar e instalar 621.01 (989,1 MB)**, com a linha "Driver GeForce Game Ready · de 21/10/2026 ·
  servidor oficial us.download.nvidia.com" e a nota de notebook quando é o caso.
- Ao clicar: download no próprio cartão (barra, "Baixando 42% (412 MB de 989 MB) · 31,5 MB/s · faltam 1 min", **Pausar download**
  — continua de onde parou), depois **"Conferindo assinatura…"**, depois a confirmação (lista versão atual → nova, tamanho, assinante,
  início do SHA-256, ponto de restauração, reinício, Desfazer) e o aplicador (UAC + tela de progresso). Sucesso: "Driver 621.01
  instalado. Driver anterior guardado para Desfazer." + botão **Desfazer (voltar ao driver anterior)**; o cartão relê a versão.
- Erros em português: sem internet ("Sem internet ou gfwsl.geforce.com fora do ar…"), espaço insuficiente (antes de baixar: precisa do
  instalador + 3 GB para a extração), download parado 2 min, tamanho diferente do anunciado, assinatura inválida ("o arquivo foi
  apagado e NUNCA foi executado"), assinante diferente, instalador alterado depois de conferido (SHA-256), código de saída de falha.

### NVIDIA: de onde vem a versão (pesquisado e conferido em 01/11/2026)

1. Lista oficial de séries e produtos: `https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2` (séries; `ParentID=1` =
   GeForce) e `TypeID=3` (produtos; `ParentID` = série). `TypeID=4` = sistemas: **Windows 11 = 135**, Windows 10 64-bit = 57.
   Séries de notebook têm "(Notebooks)" no nome. Ex.: "GeForce GTX 16 Series (Notebooks)" = psid **115**, "GeForce GTX 1650" nela =
   pfid **898** (desktop: 112/897).
2. A placa é casada pelo **nome normalizado** (sem "NVIDIA", "Laptop GPU", "Max-Q") na lista, preferindo a série de notebook quando o PC
   é notebook (bateria ou tela interna, `GpuReport.IsLaptop`) ou o nome diz "Laptop GPU"/"Max-Q"/"xxxM". O Windows chama a GTX 1650
   do Acer Nitro só de "NVIDIA GeForce GTX 1650": por isso o "é notebook" vem do PC, não do nome.
3. Sem casar (ou sem a lista), **plano B puro** `NvidiaDriverLookup.FromDeviceId`: faixa do PCI Device ID → arquitetura → série
   (Blackwell 131/133, Ada 127/129, Ampere 120/123, Turing 112/115, Pascal 101/102, Maxwell 98/99, Kepler 95/92). O driver é o
   mesmo para toda a série; a série só importa para linhas que pararam num ramo antigo (Pascal/Maxwell: 582.xx; Kepler: 475.14).
4. Versão mais nova: `https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=115&pfid=898&osID=135&languageCode=1046&isWHQL=1&dch=1&sort1=0&numberOfResults=1`
   (JSON; campos em URL-encoding). Exigido: `Success=1`, `IsWHQL=1`, `IsBeta=0`, `Version` "xxx.yy", `DownloadURL` https num host da
   lista, `DownloadURLFileSize` ("1.04 GB") dentro do limite. Em 01/11/2026 para o PC do responsável: **621.01**,
   `https://us.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe`, 1 037 190 000 bytes.
5. Tamanho exato por HEAD (seguindo e conferindo redirecionamentos); precisa bater com o anunciado (±5%).

Comparação de versões: o Windows guarda a NVIDIA como `30.0.15.1274`; os **5 últimos dígitos** de "15"+"1274" = 51274 = "512.74"
(`VendorVersions.NvidiaKeyFromWindows`), comparado com "621.01" → 62101.

### AMD: de onde vem a versão

A AMD não tem API pública em JSON. Opções avaliadas: (a) o "Auto-Detect and Install"
(`amd-software-adrenalin-edition-*-minimalsetup-*_web.exe`, ~46 MB, em drivers.amd.com/drivers/installer/…) — instala com a janela
da AMD, sem modo silencioso documentado; (b) **a página oficial de drivers de um produto** (HTML do amd.com), que traz versão,
tamanho, data e o link do **instalador completo**. O NEXUS usa (b) e cai para (a) só quando não identifica a placa.

- Ramo pelo Device ID (`AmdDriverLookup.Classify`, faixas do Laboratório de GPU). Conferido em 01/11/2026 nas páginas reais:
  RDNA 3/3.5/4 (RX 7600, RX 9070 XT, Ryzen 7 7840HS) → `…-26.10.2-win11-b.exe`; RDNA 1/2 (RX 6600, RX 5700 XT, Ryzen 7 6800H) →
  `…-win11-a.exe`; **Polaris/Vega e integradas Vega (Ryzen 5 5600H, 5800H, 4600H) → `whql-amd-software-adrenalin-edition-26.5.2-win11-may-vega-polaris.exe`**
  (ramo de manutenção: a Radeon integrada do PC do responsável não recebe mais as versões 26.10.x); GCN 1–3 → sem driver novo.
- Página de referência por ramo (o pacote é o mesmo no ramo): RX 6600, RX 7600 e Ryzen 5 5600H. Lida a seção "Windows 11 - 64-Bit
  Edition" (ou Windows 10), artigo "AMD Software: Adrenalin Edition": "Revision Number" (`Adrenalin 26.5.2 (WHQL Recommended)`) tem
  que ter a MESMA versão do link `https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-<versão>-….exe`; "File Size"
  (623 MB) e "Release Date". Escolhe a versão mais alta (na RX 7600 há 26.10.1 Recommended e 26.10.2 Optional: fica a 26.10.2).
- drivers.amd.com **exige `Referer: https://www.amd.com/`**; sem ele redireciona para uma página de erro em www.amd.com, que o NEXUS
  recusa (não é host de download).
- Versão instalada: o driver da AMD não segue a numeração do Adrenalin (27.20.14032.15001 não diz "21.x"). O NEXUS lê
  `RadeonSoftwareVersion` na chave do driver quando existe; sem ela, a tela diz que a versão do Adrenalin não é informada e o
  Detect usa a troca do driver depois da instalação (versão do Windows mais nova que a gravada no manifesto). **A existência desse
  valor não foi conferida num PC real** (ver "Como validar").
- Notebook: a própria página da AMD diz "AMD recommends OEM-provided drivers… If you experience issues… install the OEM-provided
  drivers". O NEXUS mostra isso no cartão e na confirmação (a Acer pode ter versão própria; o Adrenalin suporta os Ryzen 5000 móveis).

### Assinatura (conferida nos instaladores reais, pelo certificado embutido, em 01/11/2026)

| Fabricante | Assinante (CN) | Emissor |
|---|---|---|
| NVIDIA | `NVIDIA Corporation` (O=NVIDIA Corporation, Santa Clara) | DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1 |
| AMD | `Advanced Micro Devices` (O=Advanced Micro Devices) | Sectigo Public Code Signing CA R36 |

`WinVerifyTrust` (WINTRUST_ACTION_GENERIC_VERIFY_V2, sem interface, revogação só do cache) + nome simples do certificado do assinante
principal (`WindowsAuthenticodeVerifier`, mesmo padrão do PresentMon). Aceitos: só esses nomes ("Advanced Micro Devices, Inc." também,
nome de certificados anteriores). Conferido duas vezes: na interface depois do download e no aplicador elevado na cópia que vai rodar.

### Ação `driverupdates.gpu.install` (handler `DriverUpdatesVendorInstall`)

`userParameters` (validados pelo broker; o handler confere de novo o formato de cada fabricante): `gpuVendor` = `nvidia|amd`,
`gpuVersion` = `[0-9]{2,3}\.[0-9]{1,2}(?:\.[0-9]{1,2})?` (NVIDIA `xxx.yy`, AMD `aa.b.c`), `installerSha256` = `[0-9a-f]{64}` — o "token"
do arquivo. **Nunca um caminho**: o arquivo é `<perfil>\AppData\Local\NEXUS\drivers\<nvidia|amd>\<versão>\<nvidia-621.01.exe |
amd-adrenalin-26.5.2.exe>` (nome fixo). O alvo tem `"scope": "User"`: como serviço, o aplicador recebe o SID de quem pediu e acha o
perfil em `HKLM\...\ProfileList` (`WindowsUserProfileLocator`); sob demanda, usa o perfil do próprio processo (mesma conta). Elevado com
OUTRA conta de administrador (usuário comum digitando a senha de outro): o arquivo não é achado e nada roda (mensagem clara).

Apply, em ordem (qualquer falha para tudo; o executor só desfaz se o driver mudou):
1. Copia o instalador da pasta do usuário para `DataDirectory\drivers\<fab>\<versão>\` (pasta do aplicador, só administradores
   alteram), calculando o SHA-256 **da cópia**; diferente do confirmado = apaga, "NÃO foi executado" (contra troca entre a conferência
   e a execução, TOCTOU). Link/junção ou tamanho fora do limite (1,5 GB) = recusa.
2. Authenticode **da cópia**: cadeia válida e assinante do fabricante; senão apaga e recusa.
3. Backup do driver atual da placa (`RollbackRules.Plan` + `pnputil /export-driver oemNN.inf` em
   `DataDirectory\backup\drivers\gpu-<fab>-<versão>\`, o mesmo mecanismo das atualizações do Windows Update) e manifesto.
4. Executa a cópia (sem shell, argumentos fixos) e espera: **NVIDIA `-s -noreboot -noeula`**, até 15 min; **AMD `-INSTALL`**, até 20 min.
   Estourou o tempo: NÃO mata o instalador (driver pela metade é pior); relata e pede para esperar/reiniciar.
5. Código de saída: NVIDIA 0 = ok, 1 = ok com reinício (documentação da NVIDIA); AMD 0 = ok, 3010/1641 = ok com reinício. Outro código: se o
   driver da placa mudou mesmo assim, vale o driver; senão falha ("terminou com o código N e o driver não mudou").
6. Apaga a cópia (~1 GB). A interface apaga o download depois de instalado; se cancelar, fica para a próxima vez.

Opções silenciosas — fontes: NVIDIA, guia de instalação para Windows (`setup.exe -s` "suppresses the installer UI"; `-n`; 0 = sucesso,
1 = sucesso com reinício); o pacote baixado é um 7-Zip SFX ("NVIDIA Display Driver v621.01 - International Package", `RunProgram=setup.exe`)
que repassa as opções ao setup.exe — `<pacote>.exe -s` em uso por terceiros (ex.: buildbot do Skia); `-noreboot`/`-noeula` são
usadas pelos pacotes Chocolatey/Scoop e citadas no fórum de desenvolvedores da NVIDIA. Sem `-clean` ("instalação limpa" zera as
configurações do Painel de Controle da NVIDIA). Instalação só de "Display.Driver" (`-s -n Display.Driver`, documentada) **não** foi usada: tiraria
PhysX e áudio HDMI; fica como opção futura. AMD: "Command Line Installation" (`Setup.exe -INSTALL`, ResultCode 0/1/2); o SFX do pacote
Adrenalin tem as opções `-INSTALL`, `-SILENT` e `-factoryresetInstall` (lidas no binário real).

Detect: versão do fabricante em uso em alguma placa do fabricante (classe Display, `PCI\VEN_10DE&`/`PCI\VEN_1002&`) ≥ a pedida =
`instalada`; ou manifesto com instalação e reinício pendente (último boot antes da instalação); ou (AMD sem `RadeonSoftwareVersion`)
driver trocado pela instalação. Senão `pendente`. Restore: pacote anterior do backup (ou INF do Windows) com instalação forçada; o
Painel de Controle da NVIDIA/AMD Software instalados junto podem ficar na versão nova (o driver da placa volta).

## Placa de vídeo (leitura)

Reaproveita `Nexus.Lab.Gpu` por API pública (sem editar): `GpuLabScanner.Scan(null, …, includeGames: false)`,
`DriverInfo.Decode` (NVIDIA 32.0.15.6636 → 566.36; Intel 101.5590; AMD sem tabela pública, dito na tela),
`DriverInfo.AgeVerdict` e `DriverInfo.OfficialDownloadPage`. Ofertas de vídeo do Windows Update vão para o grupo "Placa de
vídeo" com o aviso de conferir se não são MAIS ANTIGAS que a instalada (a lista mostra "mais nova"/"MAIS ANTIGA").

## Fontes

- WUA: <https://learn.microsoft.com/windows/win32/wua_sdk/searching--downloading--and-installing-updates>,
  <https://learn.microsoft.com/windows/win32/api/wuapi/nn-wuapi-iwindowsdriverupdate>,
  critérios de busca: <https://learn.microsoft.com/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search>,
  códigos de erro: <https://learn.microsoft.com/windows/deployment/update/windows-update-error-reference>
- pnputil: <https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-command-syntax>
- NVIDIA, listas e consulta: <https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2> (e TypeID=3/4),
  `https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&…`
- NVIDIA, instalação silenciosa: <https://docs.nvidia.com/datacenter/tesla/driver-installation-guide/windows.html>;
  <https://forums.developer.nvidia.com/t/nvidia-geforce-gameready-studio-driver-silent-deployment-parameters-that-exclude-nvidia-app/372634>;
  <https://skia.googlesource.com/buildbot/+/9e907cc17f5d/skolo/win/ansible/nvidia-graphics-driver.yml>;
  <https://community.chocolatey.org/packages/nvidia-display-driver>
- AMD, páginas de drivers: <https://www.amd.com/en/support/download/drivers.html> e as páginas de produto citadas acima;
  instalação por linha de comando: <https://drivers.amd.com/relnotes/command-line-installation.pdf>
- WinVerifyTrust: <https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-winverifytrust>
- UpdateDriverForPlugAndPlayDevicesW: <https://learn.microsoft.com/windows/win32/api/newdev/nf-newdev-updatedriverforplugandplaydevicesw>
- SetupAPI (SetupDiGetClassDevs, SPDRP_*), chave do driver (DriverVersion/InfPath/MatchingDeviceId): documentação do WDK.

## Riscos

- Driver novo pode trazer defeito (vídeo, áudio, rede). Mitigação: ponto de restauração automático, backup, Desfazer.
- Trocar o driver de vídeo pisca a tela; alguns drivers só valem depois de reiniciar.
- Desfazer volta o driver do dispositivo; o pacote novo continua no repositório de drivers (removível pela página
  Ferramentas do sistema, que só remove pacotes sem uso).

## O que NÃO faz (e por quê)

- Não baixa drivers de sites de terceiros nem de "agregadores" (sem garantia de origem; vetor de malware).
- Driver de vídeo: só dos servidores oficiais da NVIDIA/AMD (lista fixa de hosts, conferida em cada redirecionamento), só WHQL estável,
  e só executa com a assinatura do fabricante conferida no aplicador. Não instala beta, Studio, versões antigas escolhidas a dedo nem
  Intel (só o link do assistente da Intel).
- Não usa `-clean` da NVIDIA (apagaria as configurações do Painel de Controle) nem a "redefinição de fábrica" da AMD.
- Não aceita termos de licença por você; não liga o serviço Windows Update; não contorna política da empresa.
- Não oculta atualizações (o Windows tem o próprio solucionador "Mostrar ou ocultar atualizações").
- Não promete desempenho: driver novo corrige falhas e compatibilidade; FPS só muda em casos específicos (jogos novos).

## Comparação com o Kudu

| Kudu | NEXUS |
|---|---|
| "Drivers": inventário e atualização | Driver de vídeo NVIDIA/AMD baixado do servidor oficial e instalado no app (progresso, assinatura conferida duas vezes, backup e Desfazer); busca oficial no Windows Update (recomendadas + opcionais), versão oferecida × instalada, data, tamanho, KB, link oficial; instalação em lote com ponto de restauração, backup por dispositivo e Desfazer real (instalação forçada do pacote anterior); honestidade quando não há volta; reinício pendente; GPU com idade, veredito e página/app oficial |

## Como validar no Windows

### Driver de vídeo pelo NEXUS (o que só um PC real prova)

Os testes no Linux cobrem a consulta (respostas REAIS da NVIDIA e páginas REAIS da AMD como amostras), a escolha da série, hosts,
redirecionamentos, retomada, tamanho, regex, códigos de saída e o handler inteiro com falsos. **Não foram exercitados de verdade:** o
download de 1 GB, `WinVerifyTrust` nos instaladores, o instalador da NVIDIA/AMD em modo silencioso, a troca real do driver, o Desfazer
de um driver de vídeo, a leitura de `RadeonSoftwareVersion`. Passos no PC do responsável (Acer Nitro AN515-45):

1. Abrir NEXUS → Sistema → **Atualizar drivers**. Esperado no cartão da GTX 1650: "Versão 512.74 (30.0.15.1274)" e, em segundos,
   "Há versão nova: 621.01 (você tem 512.74)" + **Baixar e instalar 621.01 (989,1 MB)** + nota de Optimus. Sem internet: mensagem
   "Sem internet…" em vermelho. Na Radeon integrada: "Versão mais nova do fabricante: 26.5.2" (ramo Vega/Polaris) + nota da Acer + "o
   Windows não informa a versão do Adrenalin" (se aparecer "Em dia"/"Adrenalin x" no cartão, `RadeonSoftwareVersion` existe — anotar).
2. **Baixar e instalar** na NVIDIA: barra com %, MB/s e tempo restante; testar **Pausar download** e clicar de novo (deve continuar de
   onde parou: `%LocalAppData%\NEXUS\drivers\nvidia\621.01\nvidia-621.01.exe.partial` cresce a partir do tamanho anterior).
3. "Conferindo assinatura…" → confirmação lista 512.74 → 621.01, "Assinatura digital conferida: NVIDIA Corporation" → UAC → tela de
   progresso. A tela pisca. Esperado: "Driver 621.01 instalado…"; o cartão relê "621.01 (32.0.16.2101)" e "Em dia: 621.01 é a mais nova".
   Conferir `%ProgramData%\NEXUS\backup\drivers\gpu-nvidia-621.01\` (nv_dispi.inf exportado + `nexus-manifest.json` com
   `ExitCode` 0 ou 1) e que `%ProgramData%\NEXUS\drivers\nvidia\621.01\` ficou vazio. Gerenciador de Dispositivos: 32.0.16.2101.
4. **Desfazer (voltar ao driver anterior)** → UAC → o Gerenciador de Dispositivos volta a 30.0.15.1274 (pode pedir reinício).
5. Teste de assinatura (opcional, seguro): com o download pronto e antes de confirmar, trocar o `.exe` por outro arquivo e confirmar:
   esperado "O instalador mudou depois de conferido (SHA-256 diferente): NÃO foi executado".
6. AMD (só se quiser trocar a integrada): igual ao passo 2–4 com 26.5.2 (~623 MB). Anotar se o Detect confirmou pela versão do
   Adrenalin ou "driver trocado pela instalação"; se o instalador da AMD mostrar janela mesmo com `-INSTALL`, anotar.
7. "Instalar todas (N)" nas recomendadas do Windows Update e "Instalar" numa oferta: uma confirmação, tela de progresso.

1. `dotnet build Nexus.slnx -c Release` e abrir o NEXUS → Sistema → **Atualizar drivers**.
2. Ao abrir: cartão da placa de vídeo (no PC do responsável: "NVIDIA GeForce RTX 3050 Laptop GPU · dedicada", versão
   tipo "566.36 (32.0.15.6636)", idade e veredito; AMD Radeon integrada). Botão da página oficial abre o navegador depois
   de confirmar; "Abrir NVIDIA App" aparece se o app existir.
3. **Procurar atualizações**: barra de progresso e contador; resultado agrupado. Sem internet → mensagem "Sem conexão…";
   com o serviço Windows Update desativado → mensagem do serviço; Cancelar funciona.
4. Marcar uma opcional pequena (ex.: um driver de sistema/áudio) → **Instalar selecionadas** → confirmação lista tudo,
   cita ponto de restauração e reinício → UAC → linha "instalada · driver anterior guardado". Conferir a pasta
   `%ProgramData%\NEXUS\backup\drivers\wu-<id>\` (INF exportado + manifesto) e a versão nova no Gerenciador de Dispositivos.
5. **Desfazer** → a versão anterior volta no Gerenciador de Dispositivos (pode pedir reinício).
6. VM: `tests/Nexus.IntegrationTests/Modules/DriverUpdatesIntegrationTests.cs` (inventário, busca real, recusa de ID não
   oferecido sem escrever).
7. Ao abrir a página, o cartão "Dispositivos sem driver" (ou a linha verde). Para ver o cartão num PC sem problema: desinstale
   um dispositivo inofensivo no Gerenciador de Dispositivos marcando "excluir o driver" e abra a página (código 28).

## Ponta a ponta na VM (`tests/Nexus.IntegrationTests/E2E/DriverUpdatesEndToEndTests.cs`, `Category=E2E`)

Roda em windows-2022 e windows-2025 (passo "Ponta a ponta" do `vm-integracao.yml`). Saída em `saida/e2e/drivers-*.log|json`.

| Teste | O que prova | Peça falsa |
|---|---|---|
| Busca no Windows Update + 1 instalação | Busca REAL (`Type='Driver'`); lista tudo o que achou. Se houver oferta instalável de classe segura (sem rede/disco/barramento), instala UMA (opcional de preferência) pelo **aplicador elevado real**, confere a versão nova no dispositivo, desfaz e confere a versão antiga | nenhuma |
| Backup e Desfazer com driver real | `pnputil /export-driver` de um pacote de terceiros real + INF achado no backup + `pnputil /add-driver` aceito; ciclo do handler (Assess → Detect → Apply com backup e manifesto → CheckRestore → **Restore real**: `pnputil /add-driver` + `UpdateDriverForPlugAndPlayDevices(FORCE)`) num dispositivo de classe inofensiva (fila de impressão, monitor…), conferindo mesmo driver e código de problema 0 | só o Windows Update (oferece uma versão "nova" e "instala" sem mudar nada) |
| Dispositivos sem driver | `CM_Get_DevNode_Status` responde em todos os dispositivos; lista os com problema | nenhuma |

**O que a VM NÃO prova e só um PC real com oferta prova**: o download e a instalação pelo WUA (`UpdateDownloader`/
`UpdateInstaller`) quando a VM não tem oferta (o comum no Azure: o log diz "sem oferta instalável; instalação real NÃO provada
nesta VM"), a troca para uma versão realmente diferente e a volta forçada a uma versão MAIS ANTIGA (na VM o Desfazer reinstala a
mesma versão), drivers de vídeo/áudio/rede e o reinício pedido pelo driver. Para provar no PC do responsável: passo 4 e 5 acima
com uma opcional pequena.
