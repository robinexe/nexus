# Memória: diagnóstico (`src/Nexus.Lab.Memory/Problems`, página "Memória: diagnóstico" no grupo Laboratório)

Resolve o problema "minha memória está sendo comida e eu não sei por quem". Mostra **quem** come (por app, serviço e kernel),
**se está vazando** (tendência ao longo do tempo, não um número solto) e oferece, para cada suspeito, soluções **temporárias**
(que dizem que o problema volta) e **permanentes** (reversíveis pelo aplicador). Não há "limpador de RAM": cada botão diz o que
faz, quanto dura e o custo.

Código: lógica pura em `Problems/LeakDetector.cs`, `AppGrouping.cs`, `ProcessProtection.cs`, `PoolTags.cs`, `MemoryDoctor.cs`
(motor + histórico + modo jogo), handlers em `Problems/DoctorHandlers.cs`, leituras e backends do Windows em
`Problems/WindowsDoctor.cs`. Página `ViewModels/Modules/MemoryDoctorPage.cs` + `Views/Pages/MemoryDoctor.xaml`. Catálogo:
`catalog/memorylab.v1.json` (mesmo módulo do Laboratório da memória). Testes: `tests/Nexus.Tests/Modules/MemoryDoctorTests.cs`
e `tests/Nexus.IntegrationTests/Modules/MemoryDoctorIntegrationTests.cs` (VM).

## 1. Diagnóstico ao vivo

| O quê | Como | Fonte |
|---|---|---|
| Memória agora | em uso (total − disponível − modificada, como o Gerenciador de Tarefas), modificada, cache (lista de espera), livre, comprometida / limite | `GlobalMemoryStatusEx`, `K32GetPerformanceInfo`, PDH `\Memory\Standby Cache *`, `Modified Page List Bytes`, `Free & Zero Page List Bytes` |
| Por processo | memória privada (commit), working set, handles, threads, objetos GDI/USER, caminho, horário de início | `Process.GetProcesses` (instantâneo do sistema, sem abrir processo) + `OpenProcess(QUERY_LIMITED)` → `GetProcessTimes`, `QueryFullProcessImageName`, `GetGuiResources` |
| Por app | processos somados: Edge/Chrome/Firefox/Opera/Brave/Vivaldi (navegador), Discord, Teams, Steam (+steamwebhelper), Epic, Riot, VS Code, Spotify, WebView2, Vmmem (WSL/VM) | `AppGrouping` (tabela por nome do executável; o resto agrupa pelo próprio nome) |
| Por serviço | svchost agrupado pelos serviços que hospeda (chave estável mesmo se o PID mudar) | `EnumServicesStatusEx(SC_ENUM_PROCESS_INFO)` — uma chamada dá o PID de todos os serviços ativos |
| Kernel | pool não paginado e paginado (total, sem administrador); por tag (como o PoolMon) | `K32GetPerformanceInfo`; `NtQuerySystemInformation(SystemPoolTagInformation = 22)` — o Windows só entrega as tags com administrador; sem ela, a página diz isso |
| Dono da tag | tabela de tags do Windows (`pooltag.txt`), `pooltag.txt` local se as Ferramentas de Depuração estiverem instaladas, e busca do texto da tag nos `.sys` de `System32\drivers` | Microsoft: [Troubleshooting Pool Leaks Part 2 – Poolmon](https://learn.microsoft.com/en-us/archive/blogs/ntdebugging/troubleshooting-pool-leaks-part-2-poolmon) (`findstr /s TAG *.sys`) |
| GPU | memória compartilhada (RAM usada como vídeo) total e por processo; VRAM dedicada em uso | PDH `\GPU Adapter Memory(*)\Shared Usage`, `Dedicated Usage`, `\GPU Process Memory(*)\Shared Usage` |
| Configuração | compressão de memória, modo do arquivo de paginação, RAM instalada, single channel, slot livre | `WindowsMemoryReader.ReadState` e `MemoryLabAnalyzer` (Laboratório da memória) |

Amostragem: uma leitura ao abrir e a cada 1, 2 ou 5 min (escolha na página; padrão 1 min) enquanto a página está aberta;
opcionalmente continua com a página fechada enquanto o NEXUS estiver aberto. O histórico fica **só em memória** (até 480
leituras = 8 h a 1 min). Nada é gravado em disco. Gravação em segundo plano com o NEXUS fechado **não** foi feita (exigiria um
agente residente; ver "O que NÃO faz").

### Detector de vazamento (`LeakDetector`)

Todos os critérios ao mesmo tempo:

1. ≥ 6 leituras cobrindo ≥ 15 min (antes disso: "observando…");
2. inclinação positiva pela regressão linear **e** pela mediana das inclinações (Theil-Sen, imune a pontos fora da curva);
3. ajuste linear bom (R² ≥ 0,70) — uma serra de coleta de lixo ou um pico isolado têm R² baixo;
4. crescimento robusto (mediana do último terço − mediana do primeiro terço) ≥ limiar absoluto **e** relativo;
5. a última leitura continua alta (uma subida que já voltou não é vazamento).

| Métrica | Limiar absoluto | Relativo |
|---|---|---|
| Memória privada de um app | 150 MB | 20% |
| Handles | 1.500 | 30% |
| Objetos GDI / USER | 500 (alerta direto ≥ 8.000; o limite padrão por processo é 10.000) | 30% |
| Threads | 150 | 50% |
| Pool não paginado / paginado | 150 MB / 300 MB | 30% |
| Uma tag do pool | 100 MB | 50% |

O número mostrado ("Discord cresceu 1,2 GB em 40 min") é a reta ajustada (inclinação × duração). Testado com séries
sintéticas: crescimento linear com ruído (vazamento), serra (não), pico isolado (não), degrau que voltou (não), pouco tempo (não).

Se um app some do histórico (fechou), a série recomeça quando ele volta: reiniciar o app zera a evidência, como deve.

### Suspeitos (`MemoryDoctorEngine`)

| Suspeito | Quando | Temporário | Permanente |
|---|---|---|---|
| App vazando memória | detector na memória privada do app | Fechar (pedir para fechar), Aparar (só app parado) | Não abrir com o Windows (ações `boot.startup.*`), Atualizar o programa |
| Serviço vazando | detector no grupo svchost/serviço | Reiniciar o serviço | Serviço em Manual (`boot.service.starttype`) |
| Recursos do Windows vazando | handles/GDI/USER/threads pelo detector, ou GDI/USER ≥ 8.000 | idem app/serviço | idem |
| Navegador pesado | ≥ 2,5 GB ou ≥ 25% da RAM em ≥ 8 processos | Fechar abas / guias suspensas (orientação) | Edge: guias suspensas sempre ligadas, sem Inicialização rápida, sem segundo plano; Chrome: sem segundo plano (políticas) |
| Vmmem (WSL/Docker/Hyper-V) | ≥ 1,5 GB na RAM | `wsl --shutdown` (orientação) | limite em `.wslconfig` (orientação, [Microsoft](https://learn.microsoft.com/windows/wsl/wsl-config)) |
| Pool do kernel crescendo / alto | detector no pool, ou não paginado ≥ 1 GB / paginado ≥ 3 GB; tags que mais cresceram com dono | Reiniciar o PC (único jeito de liberar pool de driver) | Atualizar o driver (abre **Atualizar drivers** via `Host.FindPage`; rede/vídeo: site do fabricante) |
| Commit perto do limite | ≥ 80% (≥ 90% = problema) | fechar o maior consumidor | Paginação automática (`memorylab.pagefile.automatic`, se desligada/fixa), mais RAM (Laboratório) |
| Livre zerada com cache enorme | livre < 3% (mín. 256 MB) e cache > 20% da RAM | Esvaziar a lista de espera (com a explicação de que é normal e quando ajuda) | Mais RAM |
| Compressão desligada | `Get-MMAgent` = False | — | Ligar (`memorylab.compression.enable`) |
| Memória sem dono visível | em uso − (Σ parte privada na RAM dos processos + pools) > máx(1,5 GB, 20% da RAM) — **estimativa** | — | Atualizar drivers; RAMMap mostra "Driver Locked" ([Sysinternals](https://learn.microsoft.com/sysinternals/downloads/rammap)) |
| GPU usando muita RAM | compartilhada ≥ 2 GB ou ≥ 25% da RAM | — | baixar texturas (orientação) |
| Pouca RAM para o uso | pico de commit (desde o boot e no histórico) ≥ 90% da RAM | — | Laboratório: tamanho sugerido = próximo padrão ≥ 1,25 × pico; com single channel + slot livre, "um segundo módulo igual dobra RAM e banda" |

No PC do responsável (8 GB em single channel), o suspeito "Seu uso pede 16 GB de RAM" aparece assim que o pico de commit passa
de ~7,2 GB, ligando ao diagnóstico de single channel do Laboratório.

## 2. Ações (catálogo `memorylab.v1.json`)

| Id | Handler | Privilégio | Persistência | O que faz | Desfazer |
|---|---|---|---|---|---|
| `memorylab.standby.purge` | `MemoryLabStandbyPurge` | Admin | Efêmera | `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` com `SeProfileSingleProcessPrivilege` ativado no próprio aplicador (o mesmo que "Empty Standby List" do RAMMap) | nada a desfazer: o cache se refaz |
| `memorylab.process.trim` | `MemoryLabProcessTrim` | Usuário | Efêmera | `EmptyWorkingSet` no processo escolhido | nada a desfazer |
| `memorylab.process.close` | `MemoryLabProcessClose` (mode=close) | Usuário | Efêmera | pede para a janela principal fechar e espera 10 s; nada é forçado | abra o programa de novo |
| `memorylab.process.end` | `MemoryLabProcessClose` (mode=end) | Usuário | Efêmera | encerra o processo (só ele) | abra o programa de novo |
| `memorylab.service.restart` | `MemoryLabServiceRestart` | Admin | Efêmera | para e inicia; se não voltar, tenta de novo e avisa — termina no estado anterior (rodando) | nada a desfazer |
| `memorylab.compression.enable` | `MemoryLabCompression` | Admin | Persistente | `Enable-MMAgent -MemoryCompression` | Restore = valor anterior (`Disable-MMAgent -MemoryCompression`) |
| `memorylab.browser.edge-startupboost-off` | `MemoryLabBrowserPolicy` | Admin | Persistente | `HKLM\SOFTWARE\Policies\Microsoft\Edge\StartupBoostEnabled` = 0 | valor anterior ou remove (e a chave, se o NEXUS a criou) |
| `memorylab.browser.edge-background-off` | idem | Admin | Persistente | `...\Edge\BackgroundModeEnabled` = 0 | idem |
| `memorylab.browser.edge-sleepingtabs-on` | idem | Admin | Persistente | `...\Edge\SleepingTabsEnabled` = 1 | idem |
| `memorylab.browser.chrome-background-off` | idem | Admin | Persistente | `HKLM\SOFTWARE\Policies\Google\Chrome\BackgroundModeEnabled` = 0 | idem |

Reaproveitadas de outros módulos (não duplicadas): `memorylab.pagefile.automatic`, `boot.service.starttype` (Manual),
`boot.startup.*.run` / `boot.startupfolder.*` / `boot.task.*` (a página procura o executável no inventário do módulo
Inicialização e usa o mesmo pedido da página Inicialização).

Garantias:
- **Processos**: o alvo é `{pid, name, started}` (regex no catálogo, revalidado no handler). O handler confere que o PID ainda é
  o mesmo processo (nome + horário de criação `GetProcessTimes`) — um PID reaproveitado é recusado. `ProcessProtection` recusa
  sempre núcleo do Windows (csrss, lsass, dwm, svchost, MsMpEng…), anticheat (vgc, EasyAntiCheat, BattlEye, FACEIT, ACE,
  mhyprot…), qualquer processo da sessão 0 (serviços) e o próprio NEXUS. Jogos (pastas de loja/launcher), Explorer, launchers e
  programas de System32 pedem **confirmação dupla** na página.
- **Serviços**: `SafetyPolicy.CheckService` (mesma lista proibida do módulo Serviços: RPC, Defender, firewall, rede básica,
  som, anticheat…); serviço parado, que não aceita parar ou com dependentes rodando é recusado.
- **Políticas de navegador**: lista fechada no código (o catálogo só escolhe o id). A `SafetyPolicy` geral proíbe
  `SOFTWARE\Policies`; este handler abre só esses 4 valores, como o módulo Privacidade faz com a lista dele.
- **Ações efêmeras** seguem o contrato do núcleo (`persistence: Ephemeral`): diário antes, sem ponto de restauração, desfazer
  responde "nada a desfazer".

Fontes: [EmptyWorkingSet](https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-emptyworkingset),
[ControlService](https://learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-controlservice),
[Enable-MMAgent](https://learn.microsoft.com/powershell/module/mmagent/enable-mmagent),
[StartupBoostEnabled](https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/startupboostenabled),
[BackgroundModeEnabled (Edge)](https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/backgroundmodeenabled),
[SleepingTabsEnabled](https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/sleepingtabsenabled),
[BackgroundModeEnabled (Chrome)](https://chromeenterprise.google/policies/#BackgroundModeEnabled),
[objetos GDI](https://learn.microsoft.com/windows/win32/sysinfo/gdi-objects), [objetos USER](https://learn.microsoft.com/windows/win32/sysinfo/user-objects),
[pools de memória](https://learn.microsoft.com/windows/win32/memory/memory-pools),
[arquivo de paginação e limite de commit](https://learn.microsoft.com/troubleshoot/windows-client/performance/introduction-to-the-page-file),
comunidade: [Ndu.sys e drivers de rede Killer/Realtek com pool não paginado enorme](https://woshub.com/huge-memory-usage-non-paged-pool-windows/),
[Intelligent Standby List Cleaner](https://www.wagnardsoft.com/) (engasgos relatados com a lista de espera cheia).

## 3. Honestidade: quando cada solução ajuda

- **Esvaziar a lista de espera**: a lista de espera é cache e já conta como memória disponível. Esvaziar não "libera RAM
  presa"; faz o PC reler do disco. Só ajuda num caso: jogo com engasgos em PC com pouca RAM em que a memória **livre** está
  zerada e o cache não é devolvido rápido o bastante (relatos da comunidade; mecanismo plausível, sem número da Microsoft).
  Dura minutos.
- **Aparar (EmptyWorkingSet)**: o commit não muda. Costuma piorar se o programa estiver em uso (falta de página). Só faz sentido
  para app parado em segundo plano que reservou memória e não usa — e mesmo assim o Windows já faz isso sozinho quando falta RAM.
- **Fechar/reiniciar**: devolve tudo, mas um vazamento volta no mesmo ritmo. A permanente é atualizar o programa ou tirar da
  inicialização.
- **Pool do kernel**: nenhum programa libera memória de um driver. Só reiniciar (temporário) ou atualizar o driver (permanente).

## 4. Modo "vigiar enquanto jogo"

Padrão **desligado**. Ligado, a cada leitura: se um processo de jogo estiver aberto (pasta de loja/launcher), a memória **livre**
estiver abaixo de 5% da RAM, o cache for ≥ 1 GB e já tiverem passado 10 min desde o último esvaziamento (`GameGuard.Decide`,
testado), a página avisa (barra de status + linha do tempo) e mostra "Esvaziar a lista de espera agora".

**Não roda sozinho**, de propósito: esvaziar exige administrador, e o NEXUS só escreve com confirmação do usuário
(`Host.ApplyAsync`). Fazer automático exigiria um agente elevado residente (tarefa agendada como SYSTEM chamando a ação efêmera
pelo aplicador) — fora do escopo deste módulo; ver o relatório. O aviso com um clique cobre o caso sem dar a um processo
elevado permissão de agir sem ninguém ver.

## 5. O que NÃO faz, e por quê

- **"Otimizar RAM" automático / limpeza periódica**: mito; piora o cache. Só a ação explícita, com explicação.
- **Encerrar processos do sistema, serviços pela via de processo, anticheat**: recusado no handler, não só escondido na página.
- **Mudar o tipo de início de drivers (ex.: `Ndu`)**: drivers de boot/sistema são recusados pelo módulo Serviços; indicamos
  atualizar o driver de rede.
- **Desligar SysMain**: é o módulo Serviços que decide; a compressão depende dele.
- **Ler tags do pool sem administrador**: o Windows não entrega. Sem elas, o total do pool continua e a página explica.
- **Afirmar o driver de uma tag de terceiros**: a busca nos `.sys` é a da Microsoft, mas uma tag curta pode aparecer em mais de um
  arquivo; a página mostra "provável" e todos os arquivos encontrados.
- **Gravação com o NEXUS fechado**: não há agente em segundo plano; o histórico vive só enquanto o NEXUS está aberto.
- **Memória "Driver Locked"/AWE exata**: o Windows não expõe sem driver; mostramos a estimativa e indicamos o RAMMap.

## 6. Riscos

- Leitura: abre cada processo só com `PROCESS_QUERY_LIMITED_INFORMATION`; ~0,3–1 s por leitura a cada minuto. A busca da tag
  nos drivers lê os `.sys` de `System32\drivers` (só leitura, até 64 MB cada, uma vez por tag desconhecida que cresce).
- Ações: ver tabela; as permanentes são reversíveis pelo diário; as efêmeras dizem que não há o que desfazer.

## 7. Como validar no Windows

1. Abrir **Laboratório > Memória: diagnóstico**: "Memória agora" com barras; maiores consumidores (Edge somado, svchost por
   serviço). Sem administrador, a nota "O Windows só mostra as tags do pool com administrador".
2. Deixar 20 min aberto com um app que cresce (ex.: uma aba com vazamento de teste, ou `testlimit -d` da Sysinternals): aparece
   o cartão "X cresceu N GB em M min" com Fechar/Aparar e "Não abrir com o Windows".
3. Clicar "Resolver agora" em Fechar: confirmação; o app fecha; a linha do tempo registra. Em jogo: duas confirmações.
4. "Esvaziar a lista de espera" (via cartão de cache cheio ou modo jogo): pede administrador; o cache cai e volta a subir.
5. Política do Edge: aplicar, ver em `edge://policy`, "Desfazer" remove.
6. `dotnet test tests/Nexus.IntegrationTests --filter FullyQualifiedName~MemoryDoctor` na VM.
