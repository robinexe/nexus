# Módulo Atualizar drivers (`Nexus.DriverUpdates`)

Página **Atualizar drivers** (grupo "Sistema", logo depois de Ferramentas do sistema). Responde ao pedido "aonde está a
página de atualização de drivers". Duas seções:

1. **Windows Update**: procura atualizações de driver oferecidas pela Microsoft para este PC (inclusive as "atualizações
   opcionais de driver"), mostra o que cada uma troca e instala as escolhidas pelo aplicador, com backup do driver atual e
   Desfazer.
2. **Placa de vídeo**: versão e idade do driver de cada GPU NVIDIA/AMD/Intel (leitura do Laboratório de GPU), com o botão
   para a página oficial de drivers do fabricante e, se instalado, o app oficial (NVIDIA App / GeForce Experience / AMD
   Software). Explica quando vale atualizar e quando não mexer.

Só fontes oficiais. Nada de "driver booster", nada de baixar de site agregador (está em `rejected` no catálogo).

- Leitura (busca no Windows Update, inventário de dispositivos, GPUs, reinício pendente): direto da biblioteca, somente
  leitura (`DriverUpdatesReader.Scan`), em `Task.Run`, com tempo limite (3 min) e botão Cancelar.
- Escrita: só pelo aplicador, ação `driverupdates.install` do catálogo `catalog/driverupdates.v1.json` (`Host.ApplyAsync` /
  `Host.RevertAsync`). Administrador: UAC + ponto de restauração automático do aplicador.
- Exceção controlada (abrir o que o usuário vê e controla, depois de `Host.Confirm`): página oficial do fabricante da GPU
  (`DriverInfo.OfficialDownloadPage` do Lab.Gpu), app oficial em caminho fixo de Program Files, "mais informações" da
  atualização (só https em domínios da Microsoft) e Configurações > Windows Update > Atualizações opcionais
  (`DriverVendorTools`). Nada é baixado pelo NEXUS fora do Windows Update.

## Ação do catálogo

| Id | Handler | Privilégio | O que faz | Desfazer |
|---|---|---|---|---|
| `driverupdates.install` | `DriverUpdateInstall` | Admin | Re-busca e confere a oferta → `pnputil /export-driver` do driver atual → manifesto → `UpdateDownloader.Download` + `UpdateInstaller.Install` (WUA) | `pnputil /add-driver <inf>` + `UpdateDriverForPlugAndPlayDevicesW(INSTALLFLAG_FORCE)` no ID que o driver anterior casava; ou o INF do próprio Windows |

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
prefere a mais nova). `UpdateDriverForPlugAndPlayDevicesW` com `INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE` é a API
oficial do "Atualizar driver" do Gerenciador de Dispositivos, aplicada ao `MatchingDeviceId` que o driver anterior casava.

Backup anterior com o mesmo UpdateID nunca é sobrescrito (renomeado com sufixo de data). O manifesto
(`nexus-manifest.json`) guarda origem, INF publicado e original, versão anterior, ID casado, dispositivos e o resultado do WUA.
`CheckRestore` revalida ID, original, manifesto e a presença do INF no backup antes de qualquer escrita.

### Reinício

Detectado pelo `Microsoft.Update.SystemInfo.RebootRequired` e pelas chaves `WindowsUpdate\Auto Update\RebootRequired` e
`Component Based Servicing\RebootPending`. A página avisa ao abrir, depois de procurar e depois de instalar. Driver que
pediu reinício conta como instalado até o próximo boot; se depois do boot a versão não mudou, volta a "pendente".

## Erros do Windows Update (mensagens claras)

`WindowsUpdateQueries.DescribeError`: sem conexão (0x8024402C, 0x80072EE2/EE7/EFD/EFE/F8F, 0x8024401C…), serviço
desativado (0x80070422, 0x8024001E), política da empresa (0x8024002E, 0x80240025, 0x8024500C, 0x80244018), ocupado
(0x80240016…), tempo limite e cancelamento. O NEXUS não liga o serviço nem mexe em política.

## Cancelamento e tempo limite

O WUA não tem cancelamento síncrono. A busca roda numa thread própria; no tempo limite ou ao cancelar a página para de
esperar e avisa — a busca do Windows termina sozinha em segundo plano (é só leitura). Instalação: até 45 min; passou disso,
o aplicador relata a falha dizendo que o Windows Update pode continuar em segundo plano.

## Placa de vídeo

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
- UpdateDriverForPlugAndPlayDevicesW: <https://learn.microsoft.com/windows/win32/api/newdev/nf-newdev-updatedriverforplugandplaydevicesw>
- SetupAPI (SetupDiGetClassDevs, SPDRP_*), chave do driver (DriverVersion/InfPath/MatchingDeviceId): documentação do WDK.

## Riscos

- Driver novo pode trazer defeito (vídeo, áudio, rede). Mitigação: ponto de restauração automático, backup, Desfazer.
- Trocar o driver de vídeo pisca a tela; alguns drivers só valem depois de reiniciar.
- Desfazer volta o driver do dispositivo; o pacote novo continua no repositório de drivers (removível pela página
  Ferramentas do sistema, que só remove pacotes sem uso).

## O que NÃO faz (e por quê)

- Não baixa drivers de sites de terceiros nem de "agregadores" (sem garantia de origem; vetor de malware).
- Não baixa nem instala o driver de vídeo do site do fabricante: só abre a página/app oficial; você escolhe a versão.
- Não aceita termos de licença por você; não liga o serviço Windows Update; não contorna política da empresa.
- Não oculta atualizações (o Windows tem o próprio solucionador "Mostrar ou ocultar atualizações").
- Não promete desempenho: driver novo corrige falhas e compatibilidade; FPS só muda em casos específicos (jogos novos).

## Comparação com o Kudu

| Kudu | NEXUS |
|---|---|
| "Drivers": inventário e atualização | Busca oficial no Windows Update (recomendadas + opcionais), versão oferecida × instalada, data, tamanho, KB, link oficial; instalação em lote com ponto de restauração, backup por dispositivo e Desfazer real (instalação forçada do pacote anterior); honestidade quando não há volta; reinício pendente; GPU com idade, veredito e página/app oficial |

## Como validar no Windows

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
