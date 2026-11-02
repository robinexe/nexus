# Módulo Automático (e a linha de comando `nexus`)

> "Se adapta ao PC que inicia": o NEXUS lê o hardware, classifica o PC e só recomenda o que faz sentido **para ele**,
> incluindo o que **não** fazer. Nada muda sem o usuário ver o plano e confirmar.

Código: `src/Nexus.Automation` (lógica pura + leituras do Windows), `src/Nexus.Cli` (executável `nexus`),
página `ViewModels/Modules/AutomationPage.cs` + `Views/Pages/Automation.xaml`, catálogo `catalog/automation.v1.json`,
página `ViewModels/Modules/SettingsPage.cs` + `Views/Pages/Settings.xaml` (Ajustes), casca `src/Nexus.Desktop/Services/Shell/`
(bandeja, preferências, `--minimized`) + `App.xaml.cs`/`MainWindow.xaml.cs` (instância única, minimizar/fechar para a bandeja),
regras `src/Nexus.Automation/Rules/recommendations.v1.json` (recurso embutido), testes `tests/Nexus.Tests/Modules/AutomationTests.cs`,
`AutomationV2Tests.cs` e `tests/Nexus.IntegrationTests/Modules/AutomationIntegrationTests.cs` (VM).

Paridade com o Kudu: verificações agendadas diária/semanal/mensal, limpeza em um clique ("Otimizar agora" com a limpeza segura)
e CLI (`nexus clean`, `schedule`, `quarantine`, `history`, `report`). Além dele: limpeza agendada só reversível (quarentena,
diário, desfazer) e opt-in, condições de bateria e jogo, tarefa de outro programa nunca tocada, notificação com "abrir relatório",
bandeja e iniciar com o Windows como ações desfazíveis.

## 1. Perfil do PC (`PcProfiler.Classify`, puro)

Entradas: `SystemFacts` (descoberta), `PcTraits` (leituras extras deste módulo) e `DiagnosticRules` (diagnóstico existente).

| Etiqueta | Como é decidida | Fonte |
|---|---|---|
| `laptop` / `desktop` / `vm` / `form-unknown` | Gabinete SMBIOS tipo 3, byte 05h (8, 9, 10, 11, 14, 30, 31, 32 = portátil); sem gabinete útil e com bateria do sistema = notebook (inferido); VM pela descoberta | DMTF DSP0134 §7.4.1; `GetSystemFirmwareTable('RSMB')` |
| `on-battery` / `on-ac` | `GetSystemPowerStatus`: ACLineStatus 0/1, BatteryFlag bit 128 = sem bateria | Win32 `SYSTEM_POWER_STATUS` |
| `ssd` / `hdd` / `disk-unknown` | `IOCTL_STORAGE_QUERY_PROPERTY` + `StorageDeviceSeekPenaltyProperty` no volume do sistema aberto **sem direitos de acesso**; barramento NVMe também conta como SSD | Win32 `DEVICE_SEEK_PENALTY_DESCRIPTOR` |
| `gpu-discrete` / `gpu-integrated-only` / `gamer` | Pelo nome: NVIDIA = dedicada; AMD "RX/R9/Radeon Pro" = dedicada, "Radeon(TM) Graphics/Vega N" = integrada; Intel "Arc A/B###" = dedicada. `gamer` = dedicada e não-VM | descoberta (DXGI/SetupAPI) |
| `low-ram` (<8 GB), `ram-16plus` | soma dos módulos SMBIOS, senão memória total | descoberta |
| `low-disk` | disco do sistema com <10% ou <15 GB livres | descoberta |
| `few-cores` (≤4 lógicos), `low-end` (= low-ram ou hdd ou few-cores) | | descoberta |
| `cpu-amd`, `cpu-intel`, `win10`/`win11`, `elevated` | | descoberta |
| `diag:<id>:<severidade>` | cada achado do `DiagnosticRules` (ex.: `diag:win.gamemode:attention`) | diagnóstico |

Sem dado, a etiqueta não aparece (nada é presumido); o motivo fica em `PcProfile.Notes`. O título vira
"Notebook gamer · RTX 4060 · 16 GB · SSD" (com " na bateria" / " modesto" quando for o caso).

## 2. Recomendações (regras JSON + motor pequeno)

`RecommendationEngine` aplica `Rules/recommendations.v1.json` às etiquetas e liga cada regra às ações que **existem** no
catálogo carregado em tempo de execução (`CatalogLoader.LoadDirectory`, que mescla `catalog/*.json`). Assim, quando os módulos
de Limpeza (`cleaning.*`), Inicialização (`boot.*`), Privacidade (`privacy.*`) etc. adicionarem ações, elas aparecem sozinhas.

Tipos: **Recomendado** (pode entrar no "Otimizar agora"), **Testar com medição** (só com A/B em Medir), **Revisar** (abre a
página do módulo; o usuário escolhe), **Não recomendado para este PC**, **Conselho** (hardware, sem ação).

- "Evitar" é avaliado primeiro: uma ação que não convém a este PC nunca aparece como recomendada.
- Uma regra por `actionPrefix` só inclui ações até `maxRisk` e que podem ser pedidas sem escolha do usuário.
- Ação ausente do catálogo: regra ignorada em silêncio.

Exemplos do que **não** recomendamos (com motivo ao usuário): plano Alto desempenho em notebook (pior ainda na bateria) e em
VM; manter todos os núcleos acordados em notebook; desligar o SysMain em PC com HDD ou pouca memória (é onde ele mais
ajuda); HAGS sem placa dedicada; Win32PrioritySeparation e SystemResponsiveness (mitos comuns de guias). Conselhos honestos:
"troque o HDD por SSD", "programas que limpam RAM não resolvem", "memória em par", "XMP é no BIOS", "jogue na tomada".

### Risco (`RiskModel`)
Experimental/Rejeitada/Obsoleta = **alto** sempre. Um campo opcional `"risk": "Low|Medium|High"` em qualquer ação do catálogo é
respeitado (sugestão aos outros módulos), mas nunca rebaixa uma experimental. Sem o campo: condicional = médio; documentada
com parâmetro do usuário ou reinício = médio; documentada simples = baixo.

## 3. "Otimizar agora" (um clique)

1. Candidatos = recomendações do tipo Recomendado, risco baixo, sem escolha do usuário e com valor definido.
2. **Limpeza segura** (ver §4.3): as regras `cleaning.*` que passam no `SafeCleaningSelector`, todas num lote novo, medidas
   antes (somente leitura, `CleaningScanner`) e conferidas no aplicador do usuário. Regra de programa aberto (navegador,
   Discord…) volta "Bloqueada" e fica de fora com o motivo.
3. Simulação no **aplicador do usuário** (`Host.PlanAsync(..., User)`, sem aviso do Windows). Uma ação inválida derruba o lote
   no broker, então a simulação cai para uma a uma. Ação de administrador que volta "Bloqueada: requer o broker elevado" vai
   para o nível administrador (o aplicador elevado simula de novo antes de escrever); "já está assim", "não se aplica" e
   bloqueios ficam fora do plano com o motivo.
4. O plano é mostrado (n ações + "Limpeza segura: N regras, X recuperáveis"). Um `Host.ApplyAsync` por nível: **usuário
   primeiro** (ajustes + limpeza segura no mesmo pedido, até 64 ações), depois **administrador** com o aviso do Windows e ponto
   de restauração.
5. Depois: **espaço antes → depois** (medido de novo) e quanto foi para a quarentena, com a frase honesta: no mesmo disco, o
   espaço só volta de fato quando a quarentena for esvaziada.
6. **Desfazer tudo**: `Host.RevertAsync` para cada execução, na ordem inversa (a limpeza volta junto, pela execução do usuário).

`nexus optimize` faz o mesmo no terminal.

## 4. Agendamentos

### 4.1 Decisão de segurança (mudou na rodada 2)

Antes: "o NEXUS nunca agenda ações que escrevem". **Agora: o NEXUS só agenda a verificação somente leitura e — se você ligar
(opt-in, desligada por padrão) — a limpeza reversível de baixo risco.** Nada de serviços, registro, privacidade, limpeza de
administrador, histórico, cookies, shaders ou ações irreversíveis sem você presente: o item `automation.scheduled-apply`
continua em `rejected` no catálogo, com o motivo atualizado.

### 4.2 Tarefas (handler `AutomationScheduledTask`)

| Ação | Tarefa | Roda | Padrão |
|---|---|---|---|
| `automation.schedule.scan` | `NEXUS-Verificacao` | `nexus scan --report --quiet --scheduled [--skip-on-battery] [--skip-when-gaming]` | desligada |
| `automation.schedule.clean` | `NEXUS-LimpezaSegura` | `nexus clean --safe --scheduled --quiet [--skip-on-battery] [--skip-when-gaming]` | desligada; condições "bateria" e "jogo" já marcadas |

- Valor: `ON`/`OFF` (`allowedValues`). Horário e condições são `userParameters` validados pelo broker por regex fechada
  (`schedule`: `DAILY HH:MM`, `WEEKLY DIA HH:MM`, `MONTHLY 1..28 HH:MM`; `conditions`: `none|battery|game|battery-game`) e de novo
  pelo handler. Dia do mês até 28 para existir em todo mês.
- **Detect**: `OFF` (não existe), `ON` (tarefa do NEXUS com o marcador DESTE tipo, uma única ação, este `nexus-task.exe` (ou `nexus.exe`, ver abaixo), argumentos
  fixos, horário, condições e `DisallowStartIfOnBatteries` iguais ao alvo), `DIFFERENT` (do NEXUS, outro horário/condição/pasta)
  ou `FOREIGN` (outra tarefa com o mesmo nome: bloqueada, nunca tocada). O XML vai para `Detail["xml"]` (até 32 KB).
- **Apply**: XML esquema 1.2 gerado com `XmlWriter` (`CalendarTrigger` com `ScheduleByDay`/`ScheduleByWeek`/`ScheduleByMonth`
  com os 12 meses, `InteractiveToken`, `LeastPrivilege`, prioridade 7, `StartWhenAvailable`, limite 15 min (verificação) / 30 min
  (limpeza), `DisallowStartIfOnBatteries`/`StopIfGoingOnBatteries` = condição de bateria) e `schtasks /create /xml` (UTF-16,
  argumentos em lista, sem shell). Caminho do executável validado como antes (`ValidateExecutable`: local, sem UNC, aspas, `%`,
  controle, `.`/`..`; termina em `nexus-task.exe` ou `nexus.exe`). Os argumentos NUNCA vêm do cliente: são
  `NexusTaskKind.ArgumentsFor(condições)`.
- **Sem janela preta (1.0):** a tarefa roda `nexus-task.exe` (`src/Nexus.Cli.Task`): o MESMO código do `nexus` compilado como
  aplicativo de janela (WinExe, constante `NEXUS_WINDOWLESS`), então o Agendador não cria console. Sem console não há como
  perguntar nem mostrar erro, por isso ele só aceita as linhas de comando exatas que o NEXUS grava nas tarefas
  (`WindowlessTask.AllowedCommandLines`: verificação e limpeza × 4 condições, mais a verificação semanal antiga); qualquer outra
  sai com código 2 sem fazer nada. `AutomationScheduledScanHandler.DefaultExecutable` prefere o `nexus-task.exe` da pasta do
  aplicador e cai para o `nexus.exe` se ele não existir (cópia de desenvolvimento). Tarefa criada antes (apontando para o
  `nexus.exe`) aparece como `DIFFERENT` ("aponta para outra pasta ou versão"): salvar de novo troca para o `nexus-task.exe`, e
  desfazer recria o XML antigo exato. `publish.py` confere o subsistema PE (2 = janela) e o `release.yml` roda o
  `nexus-task.exe` instalado com a linha da tarefa (relatório gerado) e com outro comando (recusado).
- **Restore**: `OFF` → remove; `ON`/`DIFFERENT` → recria o XML exato do diário depois de validar (`IRestoreValidator`: marcador
  do tipo, executável válido, argumentos que o NEXUS gera, horário reconhecido).
- `OtherAccount=true` → recusado (a tarefa seria de outra conta).
- A verificação semanal da v1 (`automation.scheduled-scan.weekly`, `NEXUS-VerificacaoSemanal`, handler `AutomationScheduledScan`)
  continua funcionando e desfazível; a página Ajustes mostra se ela está ligada e oferece "Desligar a antiga".

### 4.3 Limpeza segura (`SafeCleaningSelector`)

Entra só a regra que cumpre **tudo**: ação `cleaning.<regra>` do handler `CleaningRule` (id e alvo conferem com a regra
embutida); privilégio `User`; não `irreversible`; classificação `SupportedDocumented` (nada `Conditional`); risco declarado, se
houver, `Low`; sem reinício; o único parâmetro é o lote; regra de quarentena, do perfil do usuário (`Scope User`, sem `admin`),
não sensível, sem aviso (`warning`) e marcada por padrão na página Limpeza. Hoje: temporários da conta, relatórios de erro e
despejos da conta, caches de Chrome/Edge/Brave/Opera/Firefox, Discord, Spotify, Teams, Slack, Steam (htmlcache), Epic e
Battle.net. Ficam de fora (com o motivo no relatório): `Windows\Temp`, WER/CBS/despejos do sistema, logs e shaders do Steam,
Battle.net em ProgramData (administrador), histórico e cookies (sensíveis), shaders de GPU (aviso de engasgo), miniaturas
(condicional) e Lixeira/Windows Update/Otimização de Entrega (só medição).

`nexus clean --safe --scheduled` (o que a tarefa roda):
1. Pelo `nexus-task.exe`, não há console (nada pisca). Pelo `nexus.exe` (tarefa antiga ou cópia de desenvolvimento), solta o
   console que o Agendador abriu (`FreeConsole`, só se o console é só dele), o que pode piscar uma janela por um instante.
2. Condições: bateria (`GetSystemPowerStatus`, ACLineStatus 0) e jogo (`SHQueryUserNotificationState` = Direct3D em tela cheia,
   app em tela cheia ou modo apresentação; ou um processo de uma lista curta de jogos conhecidos). Se valer, **pula** (código 0),
   grava `clean-*.json/html` com o motivo e uma linha em `reports\agendamentos.log`.
3. Mede antes, simula no aplicador do usuário (sem UAC), aplica só o que ficou "Pendente" (programa aberto = pulado), mede
   depois, lê do manifesto o que foi para a quarentena e grava o relatório `clean-AAAAMMDD-HHMMSS.json/.html`.
4. Nunca chama o aplicador de administrador (lança exceção se alguma regra de administrador chegasse até ali).
5. Notificação (se ligada em Ajustes): "Limpeza segura: X em N arquivos foram para a quarentena. Clique para abrir o relatório."

Desfazer: Limpeza > Histórico, `nexus quarantine restore <lote>` ou `nexus revert <execução>` (a execução fica no diário do
usuário com o perfil `agendado-limpeza`).

## 5. Notificações, bandeja e iniciar com o Windows

- **Notificação** sem WinRT nem pacote: `Shell_NotifyIconW` com balão (`NIF_INFO`), que o Windows 10/11 mostra como notificação
  do sistema e guarda na central. Na tarefa agendada, `BalloonNotifier` cria uma janela invisível só de mensagens, mostra, espera
  até 25 s o clique (`NIN_BALLOONUSERCLICK`) e abre o relatório — só um `.html` existente, direto na pasta de relatórios, sem
  link (`ReportFiles.IsReportFile`). Escolha consciente: o toast WinRT exigiria mudar o TFM da CLI para
  `net10.0-windows10.0.19041` (+ ~25 MB do Microsoft.Windows.SDK.NET) e registrar um AppUserModelID (atalho ou chave em
  `HKCU\Software\Classes\AppUserModelId`, uma escrita no sistema fora do aplicador). O balão não tem botões, mas clicar abre o
  relatório.
- **Bandeja** (`Services/Shell/TrayIcon.cs`): o mesmo `NotifyIconHandle` numa janela invisível `HwndSource` (recebe
  `TaskbarCreated` e recoloca o ícone se o Explorador reiniciar). Menu nativo (`TrackPopupMenuEx`): Abrir, Otimizar agora… (abre
  o Automático e roda o mesmo fluxo com confirmação), Verificar agora (relê o PC, grava o relatório e avisa com "abrir"), Sair.
  "Minimizar para a bandeja" e "fechar para a bandeja" são opcionais (desligados por padrão). Sem `UseWindowsForms`: evita
  ambiguidades (`MessageBox`, `Application`) nos arquivos dos outros módulos e a carga do WinForms.
- **Instância única** (`App.xaml.cs`): mutex `Local\NEXUS.Desktop.Instance`; abrir de novo só traz a janela existente
  (evento `Local\NEXUS.Desktop.Show`).
- **Iniciar com o Windows** (`automation.start-with-windows`, handler `AutomationStartWithWindows`, User): valor `NEXUS` em
  `HKCU\...\CurrentVersion\Run` = `"<pasta>\Nexus.Desktop.exe" --minimized`. Handler próprio porque o caminho é dinâmico: é
  montado pelo aplicador (Nexus.Desktop.exe ao lado dele) e validado como o do `nexus.exe`. Detect: `OFF`, `ON`, `STALE`
  (do NEXUS, outra pasta) e `FOREIGN` (qualquer outro conteúdo, tipo `ExpandString` ou argumentos diferentes: nunca tocado).
  Restore: remove ou grava o texto anterior (validado). `--minimized` abre minimizado e, com o ícone ligado, esconde na bandeja.
  O `StartupApproved` (o liga/desliga do Gerenciador de Tarefas) não é tocado.

## 6. Página Ajustes (grupo "Registro")

Agendamentos (verificação e limpeza segura: frequência, dia, hora, "não rodar na bateria", "não rodar com jogo aberto", Ligar /
Salvar horário / Desligar, a lista do que a limpeza inclui e o resumo do que nunca entra, último relatório de limpeza),
notificações (ao terminar; também quando pulada), bandeja (ícone, minimizar e fechar para a bandeja), iniciar com o Windows,
pastas (dados e relatórios, abrir no Explorador) e sobre (versão, aplicador, catálogo, licenças das dependências redistribuídas).

Preferências da interface em `%LocalAppData%\NEXUS\settings.json` (`NexusSettings`): gravação atômica (`.tmp` + troca);
arquivo ausente, corrompido, de outra versão, com tipos errados ou maior que 64 KB → padrões (o ruim é guardado como `.bad`).
Nada ali muda o sistema; tarefas e iniciar com o Windows são ações do catálogo pelo aplicador do usuário (`Host.ApplyAsync`).
A linha de comando agendada lê só `notifications`/`notifySkipped`.

## 7. Linha de comando `nexus`

`nexus --help` (português). Leitura: `scan [--report] [--json]`, `report [--html]` (com a seção de limpeza: quarentena e últimos
lotes), `recommend`, `plan <ids>`, `history [runId]`, `history --cleaning`, `quarantine list`, `measure <csv>`,
`compare --before … --after …`, `schedule list`/`status`. Mudança (sempre pelo aplicador, com confirmação no terminal ou `--yes`,
diário e desfazer): `optimize`, `clean [--safe] [--rules id,…] [--dry-run] [--yes]`, `quarantine restore <lote>`,
`apply <id[=valor]>…`, `revert <runId>`, `schedule add scan|clean [--freq daily|weekly|monthly] [--day SUN..SAT|1..28]
[--at HH:MM] [--skip-on-battery] [--skip-when-gaming] [--always]`, `schedule remove scan|clean`, `schedule on|off [--at]` (antiga).

- `clean` sem `--rules` = limpeza segura. Com `--rules`, qualquer regra de quarentena (as de administrador numa segunda
  confirmação, com UAC e ponto de restauração; as com aviso mostram o aviso); com `--safe --rules`, só as que também são seguras.
  `--scheduled` só existe com `--safe` e sem `--rules`. Ids de regra: `[a-z0-9-]`, até 64.
- Os argumentos que as tarefas geram são exatamente aceitos pelo parser (teste).
- Códigos de saída: 0 ok (inclui "pulado por condição") · 1 concluído com problemas · 2 uso inválido · 3 não encontrado (regra,
  lote, execução) · 4 cancelado · 5 aplicador recusou ou não respondeu · 6 não suportado neste sistema.

## 8. Riscos e limites

- Classificação de GPU por nome pode errar em modelos novos (fica "integrada" e o PC não vira "gamer": erro conservador).
- Tipo de disco: alguns controladores RAID/USB não respondem à consulta de penalidade de busca → "não determinado".
- Limpeza agendada roda sem você olhar: por isso só regras reversíveis e de baixo risco, sempre para a quarentena, e desligada
  por padrão. A quarentena cresce até ser esvaziada (o esvaziamento é do módulo Limpeza, com o contrato de irreversíveis).
- Detecção de jogo: tela cheia exclusiva e sem borda (pelo sinal do Windows) e uma lista curta de processos; um jogo em janela
  que não está na lista não é detectado. Na dúvida, prefira um horário em que você não joga.
- Tarefas criadas antes da 1.0 rodam o `nexus.exe` (console) e podem piscar uma janela até serem salvas de novo (passam para o
  `nexus-task.exe`). Na cópia de desenvolvimento (`bin\Release`) não há `nexus-task.exe` ao lado do aplicador; ela continua
  usando o `nexus.exe`.
- O balão aparece com o nome/ícone do executável (sem AppUserModelID) e não tem botões; clicar abre o relatório.
- Mover a pasta do NEXUS deixa tarefas e "iniciar com o Windows" apontando para a pasta antiga (aparecem como desatualizadas;
  salvar de novo atualiza).
- Recomendações usam a leitura do PC do momento; depois de aplicar, o plano é conferido de novo no aplicador.

## 9. Validar no Windows

1. `dotnet test tests/Nexus.Tests -c Release --filter FullyQualifiedName~Automation` (também no Linux).
2. VM: `tests/Nexus.IntegrationTests/Modules/AutomationIntegrationTests.cs` — o Agendador real aceita os XML diário/semanal/
   mensal das duas tarefas (caminho com espaço e `&`), releitura = `ON`, restauração recria o XML exato; Run real do HKCU.
3. Interface: Ajustes → ligar a verificação "todo dia" daqui a 2 minutos; conferir `NEXUS-Verificacao` no Agendador
   (`taskschd.msc`), esperar a notificação e clicar (abre `reports\scan-*.html`). Ligar a limpeza segura, rodar a tarefa à mão
   (botão direito > Executar) com o navegador fechado: notificação, `reports\clean-*.html`, lote em Limpeza > Histórico,
   Restaurar. Repetir com o notebook na bateria e com um jogo em tela cheia: a tarefa pula (ver `reports\agendamentos.log`).
4. Bandeja: ícone, menu (Abrir, Otimizar agora, Verificar agora, Sair), minimizar/fechar para a bandeja ligados e desligados;
   reiniciar o Explorador (o ícone volta). Iniciar com o Windows: ligar, sair e entrar na conta (abre na bandeja), desligar.
5. Terminal: `nexus clean --dry-run`, `nexus clean`, `nexus quarantine list`, `nexus quarantine restore <lote>`,
   `nexus history --cleaning`, `nexus schedule add clean --freq weekly --day SAT --at 10:00`, `nexus schedule list`,
   `nexus schedule remove clean`, `nexus report --html`.
