# Módulo Inicialização e Serviços (`Nexus.Boot`)

Páginas: **Inicialização** e **Serviços** (grupo "Sistema"). Catálogos: `catalog/boot.v1.json` e `catalog/services.v1.json`.
Testes: `tests/Nexus.Tests/Modules/BootTests.cs` (unitários, Linux) e `tests/Nexus.IntegrationTests/Modules/BootIntegrationTests.cs`
(VM Windows descartável).

## O que faz

### Inicialização (leitura)
Inventário de tudo o que abre com o Windows, cada item com nome, editor, caminho, se o arquivo existe e a origem:

| Origem | Onde é lido | Pode mudar? |
|---|---|---|
| Run do usuário | `HKCU\...\CurrentVersion\Run` | Ligar/desligar (StartupApproved\Run) |
| Run do computador (64 e 32 bits) | `HKLM\...\Run` nas duas visões | Ligar/desligar (StartupApproved\Run e \Run32), administrador |
| RunOnce (usuário/computador) | `...\RunOnce` | Só leitura (roda uma vez e some) |
| Pasta Inicializar do usuário e comum | `shell:startup`, `shell:common startup` (atalhos `.lnk` lidos pelo formato [MS-SHLLINK]) | Ligar/desligar (StartupApproved\StartupFolder) |
| Tarefas agendadas com gatilho de logon/boot | API COM do Agendador (`Schedule.Service`); alternativa `schtasks /query /xml` | Ligar/desligar (fora de `\Microsoft\`) |
| Serviços automáticos de terceiros | `HKLM\SYSTEM\CurrentControlSet\Services` + `EnumServicesStatusExW` | "Pôr em manual" (mesma ação da página Serviços) |

- **Ligado/desligado** é lido de `Explorer\StartupApproved` exatamente como o Gerenciador de Tarefas (valor ausente = ligado;
  byte 0 ímpar = desligado).
- **Editor**: assinatura Authenticode embutida verificada com `WinVerifyTrust` **sem rede** (`WTD_CACHE_ONLY_URL_RETRIEVAL`,
  sem revogação), nome do signatário do certificado. Sem assinatura embutida (comum em arquivos do Windows assinados por
  catálogo): nome da empresa na versão do arquivo, marcado "não assinado; dado do arquivo".
- **Entrada órfã**: valor Run cujo executável comprovadamente não existe (caminho absoluto e local, sem variável desconhecida).
  Se não dá para provar (variável de outra conta, caminho relativo, caminho de rede), o arquivo fica como "não foi possível
  conferir" e nada é oferecido para remover.

### Impacto no boot (leitura)
Lê `Microsoft-Windows-Diagnostics-Performance/Operational` pela API do Log de Eventos (wevtapi):
- evento **100**: duração do boot (`BootTime`), até a área de trabalho (`MainPathBootTime`) e pós-boot (`BootPostBootTime`);
- eventos **101 a 110**: componentes que o Windows registrou como lentos no boot (101 programa, 102 driver, 103 serviço,
  109 dispositivos, demais etapas do sistema), com `TotalTime` e `DegradationTime`.

A página mostra "Último boot: 38 s; área de trabalho em 24 s; principal atraso: X" e um gráfico das últimas 12 inicializações.

**Classificação de impacto** (heurística do NEXUS, documentada na própria página): o maior `TotalTime` registrado para o
executável (101) ou serviço (103). Alto ≥ 3 s, Médio 1 a 3 s, Baixo < 1 s. **Sem registro = Desconhecido**, nunca "Baixo":
o Windows só registra o que passa do limite dele. Sem permissão para ler o log (o canal costuma exigir administrador ou o grupo
"Leitores de Log de Eventos"), tudo fica "Desconhecido" com o motivo, e o tempo de boot aparece como indisponível. Nada é estimado.

### Serviços
- Lista completa (Win32; drivers ficam de fora): nome, exibição, descrição (strings `@dll,-id` resolvidas com
  `SHLoadIndirectString`), tipo de início (inclusive **Automático atrasado**), estado, conta, dependências, editor, origem
  (Microsoft/terceiro pelo editor do binário ou da `ServiceDll`).
- Busca e filtros: todos, terceiros, Microsoft, em execução.
- **Presets explicados** (campo `presets` de `services.v1.json`), cada um com "para quem", antes → depois lido do PC e uma
  frase de honestidade sobre o ganho:
  - *Não uso impressora nem fax*: Spooler e Fax → Desativado (Spooler: recomendação da Microsoft no CVE-2021-34527).
  - *Não uso Xbox nem Game Pass*: XblAuthManager, XblGameSave, XboxNetApiSvc → Desativado. Ganho de desempenho perto de zero;
    `XboxGipSvc` (acessórios/controle) não é tocado.
  - *Privacidade e acesso remoto*: DiagTrack e RemoteRegistry → Desativado. Não acelera nada.
  - *Jogos em PC com SSD (ganho pequeno)*: SysMain, MapsBroker e WSearch → Manual. Em SSD o efeito costuma ser pequeno ou nenhum;
    sem indexação a busca do Iniciar/Outlook fica lenta e incompleta. Recomendação: medir antes/depois na página Medir.
- Mudança individual: escolher serviço + tipo de início, com aviso (dependências, o que "Manual" e "Desativado" significam).

## Como escreve (sempre pelo aplicador)

A interface só lê. Toda mudança é uma ação de catálogo pedida por `Host.ApplyAsync` (confirmação, diário antes de escrever,
releitura com Detect, Desfazer). Ações de administrador disparam o aviso do Windows e ponto de restauração.

| Ação (catálogo) | Handler | Escopo |
|---|---|---|
| `boot.startup.user.run`, `boot.startup.machine.run`, `boot.startup.machine.run32` | **`StartupApproved` (existente, reaproveitado)** com `userParameters.entryName` | HKCU usuário; HKLM administrador |
| `boot.startupfolder.user`, `boot.startupfolder.common` | `BootStartupFolder` (valida o alvo e delega Detect/Apply/Restore ao `StartupApprovedHandler`, kind StartupFolder) | idem |
| `boot.task.user`, `boot.task.admin` | `BootScheduledTask` (`IRegisteredTask.Enabled`; alternativa `schtasks /change`) | conta do usuário / administrador |
| `boot.orphan.user.run`, `boot.orphan.machine.run`, `boot.orphan.machine.run32` | `BootOrphanEntry` | idem |
| `boot.service.<serviço>.<tipo>` (10 ações de preset) e `boot.service.starttype` (`userParameters.service`) | `BootServiceStartType` (delega ao `ServiceStartTypeHandler` existente e acrescenta a lista proibida) | administrador |

Regex dos parâmetros do usuário (o broker exige casamento inteiro):
`entryName` sem controle nem `\`; `fileName` sem `\ / : * ? " < > |`; `taskPath` `\pasta\nome` até 8 níveis e **nunca**
`\Microsoft\…` (também recusado no handler); `service` `[A-Za-z0-9_.-]` com espaços internos, até 80.

### Lista proibida de serviços (`ServicePolicy`, aplicada no handler, com qualquer catálogo)
Núcleo (RpcSs, RpcEptMapper, DcomLaunch, LSM, SamSs, ProfSvc, UserManager, Power, PlugPlay, BrokerInfrastructure,
CoreMessagingRegistrar, StateRepository, SystemEventsBroker, TimeBrokerSvc, SENS, gpsvc, Schedule, KeyIso, VaultSvc, Appinfo,
TrustedInstaller, EventLog, EventSystem, Winmgmt, CryptSvc, DeviceInstall, camsvc), segurança (BFE, MpsSvc, WinDefend,
WdNisSvc, WdFilter, WdBoot, Sense, SecurityHealthService, wscsvc, SgrmBroker, WaaSMedicSvc), rede básica (Dhcp, Dnscache, nsi,
NlaSvc, netprofm, Wcmsvc, LanmanWorkstation, WinHttpAutoProxySvc, BITS), som (AudioSrv, AudioEndpointBuilder), anticheat
(vgc, vgk, EasyAntiCheat, EasyAntiCheat_EOS, BEService, FACEIT, PnkBstrA/B, mhyprot2, ACE-BASE). Mais a lista do
`SafetyPolicy` do núcleo. **Nunca Desativado** (Manual permitido): wuauserv, UsoSvc, DoSvc, InstallService, msiserver.
Também recusados: drivers (tipo ≠ 0x10/0x20) e serviços por usuário (tipo com 0x40/0x80, ex.: `CDPUserSvc_1a2b`).

## Como desfaz
- StartupApproved/pasta Inicializar: grava de volta os 12 bytes originais, ou remove o valor se ele não existia.
- Tarefa: volta ao estado anterior (habilitada/desabilitada). Tarefa apagada depois não é recriada (Restore recusa "ausente").
- Entrada órfã: o snapshot guarda tipo (`String`/`ExpandString`) e dados completos; Desfazer recria o valor exatamente.
  Se um programa recriar o valor nesse meio-tempo, o executor detecta conflito e não sobrescreve.
- Serviço: volta ao tipo original, inclusive Automático atrasado. Restaurar um "Desativado" que era do usuário é permitido.

## Fontes
- Run/RunOnce: learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys
- Aplicativos de inicialização (Gerenciador de Tarefas): support.microsoft.com "Configurar aplicativos de inicialização no Windows";
  formato de `StartupApproved` **observado** (não documentado oficialmente).
- Tarefas: learn.microsoft.com/windows/win32/taskschd/registeredtask-enabled; esquema XML: learn.microsoft.com/windows/win32/taskschd/task-scheduler-schema;
  learn.microsoft.com/windows-server/administration/windows-commands/schtasks-change
- Atalhos: learn.microsoft.com/openspecs/windows_protocols/ms-shllink
- Log de Eventos: learn.microsoft.com/windows/win32/api/winevt (EvtQuery/EvtNext/EvtRender). Os nomes dos campos dos eventos
  100–110 do Diagnostics-Performance são observados nos próprios eventos (não há página oficial com o esquema).
- Serviços: learn.microsoft.com/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server
  (escrito para Windows Server 2016 com Experiência Desktop; num PC o critério é se você usa o recurso);
  msrc.microsoft.com/update-guide/vulnerability/CVE-2021-34527 (Spooler); learn.microsoft.com/windows/privacy/configure-windows-diagnostic-data-in-your-organization (DiagTrack);
  learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw.

## Riscos
- Desligar um item de inicialização pode desligar algo útil (sincronização, painel do mouse/teclado, atalhos). Reversível.
- Alguns programas se registram de novo ao abrir. O NEXUS não "briga" com eles.
- Serviços em Desativado quebram o recurso que dependem deles até desfazer (por isso os presets dizem para quem servem).

## O que NÃO faz, e por quê
- Não apaga entradas Run de programas que existem (desligar é suficiente e reversível; apagar não ajuda o boot mais que desligar).
- Não mexe em tarefas da Microsoft nem em serviços essenciais/anticheat (quebram o Windows ou jogos; ganho nulo).
- Não promete FPS por desligar serviços: a maioria já fica em Manual. Presets dizem o ganho real (geralmente pequeno ou nenhum).
- Não "limpa Prefetch" nem muda "número de processadores" no msconfig (mitos; ver `rejected` em `boot.v1.json`).
- Não estima impacto quando o Windows não mediu ("Desconhecido"), nem inventa tempo de boot sem acesso ao log.
- Não acessa a rede para verificar assinaturas (revogação não é consultada; a página diz "assinatura válida" só pela cadeia em cache).
- Não altera tarefas sem gatilho de logon/boot (fora do escopo de inicialização).

## Limitações conhecidas
- Valores Run com `\` ou caracteres de controle no nome não passam no regex e não podem ser alterados pela página.
- Pasta Inicializar do usuário quando o aplicador roda como outra conta (serviço): a existência do arquivo não é conferida
  (o valor em StartupApproved é inofensivo sem o arquivo).
- O tempo de boot exige ler o log como administrador (ou grupo "Leitores de Log de Eventos").
- COM do Agendador acessado por `dynamic` (sem interop tipado); se falhar, cai para `schtasks`, cuja saída pode trocar acentos
  de nomes de tarefas (código de página do console). A alteração continua usando o caminho validado.

## Como validar no Windows
1. `dotnet build Nexus.slnx -c Release` e `dotnet test tests/Nexus.Tests -c Release --no-build --filter Category=Unit`.
2. Abrir o NEXUS (não elevado): Inicialização deve listar os itens do Gerenciador de Tarefas > Aplicativos de inicialização com o
   mesmo estado ligado/desligado; o tempo de boot deve aparecer como indisponível com o motivo. Abrir como administrador: o tempo
   deve bater com o Visualizador de Eventos > Microsoft > Windows > Diagnostics-Performance > Operacional, evento 100.
3. Serviços: conferir alguns tipos de início com `services.msc` (inclusive "Automático (Atraso na Inicialização)").
4. Alterações reais: só na VM (`vm-integracao` no GitHub Actions roda `BootIntegrationTests`).
