# Log de build e testes

Registro cronológico. Cada bloco diz **onde** rodou. Resultado em Linux não vale como validação no Windows. Validação
de compilação ou de testes unitários também não vale como validação de aplicação, rollback ou recuperação real.

---

## Etapa 1 — Linux (contêiner isolado), 2026-10-30

Ubuntu 24.04 x64, .NET SDK 10.0.403 (MSBuild 18.9.15), runtime 10.0.14. Códigos de saída via `$?`.

| # | Comando | Exit | Resumo |
|---|---|---|---|
| 1 | `dotnet --info` / `--list-sdks` | 0 / 0 | SDK 10.0.403; `global.json` (10.0.100 + latestFeature) aceita |
| 2 | `dotnet restore Nexus.slnx` | 0 | Sem NU1102. NU1903 em SQLitePCLRaw.lib.e_sqlite3 2.1.11 |
| 3 | `dotnet build Nexus.slnx -c Release --no-restore` | 0 | 0 erros, 59 avisos, nenhum CA1416 |
| 4 | `dotnet test Nexus.slnx -c Release --no-build` | 0 | 50 aprovados, 0 falhos |
| 5 | Após correções: restore / build / `test --filter Category=Unit` | 0 / 0 / 0 | 55 aprovados, 0 falhos |
| 6 | Smoke test `nexus-recover` runs / scan / show | 0 / 1 / 0 | `scan` com exit 1 = há operações a recuperar (esperado); diário com SHA-256 igual |

Correções da Etapa 1: colisão de sequência do diário (`UNIQUE(run_id, seq)`) num segundo `Execute` da mesma execução;
CLI de consulta abrindo o diário para escrita; Microsoft.Data.Sqlite 10.0.0 → 10.0.14; `Trait Category=Unit`.

## Etapa 1 — Windows (PC do desenvolvedor), 2026-10-30 — resultado informado pelo usuário

Executado pelo usuário com `tools\etapa1-windows.ps1` (versão de 30/10), PowerShell não elevado, **.NET SDK 10.0.403**.

| Passo | Resultado |
|---|---|
| `dotnet restore Nexus.slnx` | exit 0 |
| `dotnet build Nexus.slnx -c Release --no-restore` | exit 0, 0 erros, 61 avisos |
| `dotnet test ... --filter Category=Unit` | **55 aprovados, 0 falhos, 0 ignorados** |
| Nexus.SmokeJournal (compilação e criação do diário de teste) | aprovado |
| `nexus-recover runs` / `show` | exit 0 / exit 0 |
| `nexus-recover scan` (diário com interrompidas) | exit 1 (esperado) |
| `nexus-recover scan` (diário inexistente) | exit 3 (esperado) |
| SHA-256 do diário de teste antes/depois das consultas | igual |
| Codificação | **caracteres corrompidos** na saída do script e no log (corrigido na Etapa 2) |

**O que isso valida:** compilação, testes unitários (backends em memória e arquivos temporários) e as consultas
somente leitura do CLI, **no Windows, na versão de 30/10**.
**O que isso NÃO valida:** aplicação real, rollback real e recuperação real contra registro, energia ou serviços.
Nenhum teste toca APIs reais do Windows. Isso fica para a VM Windows isolada.

---

## Etapa 2 — Linux (contêiner isolado), 2026-10-30

Mesmo ambiente (SDK 10.0.403). O WPF compila em Linux com `EnableWindowsTargeting`, mas **a interface não foi aberta**:
não há Windows nem tela neste ambiente.

### Verificação limpa final (sem bin/obj)

| Comando | Exit | Resumo |
|---|---|---|
| `dotnet --list-sdks` | 0 | `10.0.403` |
| `dotnet restore Nexus.slnx` | 0 | 0 avisos (inclui Nexus.Desktop, Nexus.HardwareDiscovery, Nexus.TestHelper) |
| `dotnet build Nexus.slnx -c Release --no-restore` | 0 | **0 erros, 0 avisos** |
| `dotnet test Nexus.slnx -c Release --no-build --filter Category=Unit` | 0 | **99 aprovados, 0 falhos, 0 ignorados** (93 métodos de teste; Theories contam por caso) |
| `dotnet build tools/Nexus.SmokeJournal` / `tools/Nexus.Discover` | 0 / 0 | 0 erros, 0 avisos |
| `Nexus.SmokeJournal <tmp>/smoke.db` | 0 | "SQLite nativo carregado: 3.53.3" |
| `nexus-recover runs` / `scan` | 0 / 1 | execução interrompida marcada **ABANDONADA**; "Nenhum executor ativo." |
| `nexus-recover recover --journal` (sem caminho) | 2 | recusado, nada feito |
| SHA-256 do diário de teste antes/depois | — | igual |

### Testes por área (99)

| Arquivo | Casos | Área |
|---|---|---|
| EngineTests | 16 | resolvedor, executor, política de segurança |
| RecoveryTests | 9 | falhas simuladas (seção 14), SqliteJournal, RunLock |
| HandlerTests | 10 | handlers de registro, inicialização, energia, serviços |
| GameAndAnalysisTests | 15 | CS2 .cfg, métricas, PresentMon |
| JournalReviewTests | 5 | sequência do diário, abertura somente leitura |
| CoordinationTests | 9 | trava entre processos (com `nexus-recover` e processo auxiliar reais) |
| RecoveryCaseTests | 11 | casos A1–A3, A5–A7, esquema v2 |
| ReviewFindingsTests | 8 | achados da revisão independente |
| DiscoveryTests | 16 | SMBIOS, topologia da CPU, diagnóstico, catálogo, garantias somente leitura (descoberta e interface) |

### NU1903 — demonstração sem supressão

Nenhum `NoWarn`, `WarningsNotAsErrors` ou `NuGetAudit` no repositório (busca em `*.props`, `*.csproj`, `*.targets` e
`nuget.config`). Restore isolado de `src/Nexus.Persistence` com `--force` e lock files apagados:

| `Microsoft.Data.Sqlite` | Saída do restore | Resolvido (`packages.lock.json`) |
|---|---|---|
| 10.0.0 | `warning NU1903: Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.11 has a known high severity vulnerability, https://github.com/advisories/GHSA-2m69-gcr7-jv3q` | Microsoft.Data.Sqlite.Core 10.0.0; SQLitePCLRaw.bundle_e_sqlite3 / core / provider.e_sqlite3 / **lib.e_sqlite3 2.1.11** |
| **10.0.14** | (nenhum aviso) | Microsoft.Data.Sqlite.Core 10.0.14; SQLitePCLRaw.bundle_e_sqlite3 / core / provider.e_sqlite3 / **lib.e_sqlite3 2.1.13** |

**SQLite nativo efetivamente carregado:** `SELECT sqlite_version()` → **3.53.3**
(`SqliteJournal.NativeSqliteVersion()`, impresso pelo Nexus.SmokeJournal). A `libe_sqlite3.so` (linux-x64) e a
`e_sqlite3.dll` (win-x64) do pacote 2.1.13 contêm o mesmo identificador de fonte do SQLite
(`2026-06-26 20:14:12 d4c0e51e4aeb…`). O sistema tem libsqlite3 próprio, mas a versão relatada é a do pacote.
3.53.3 ≥ 3.50.2, versão que corrige a CVE-2025-6965. No Windows, o script imprime a versão carregada lá.

### Avisos: de 61 para 0

| Regra | Antes | Tratamento |
|---|---|---|
| CA1707 (sublinhado em nomes) | 50 (69 na Etapa 2) | Convenção de nomes de teste: `dotnet_diagnostic.CA1707.severity = none` **só em `tests/.editorconfig`**. Analisadores intactos em `src/` |
| CA1725 (nome de parâmetro ≠ interface) | 6 | Corrigido (`t` → `target`) |
| CA1716 (palavra reservada em VB) | 2 | Parâmetro `to` → `targetState`. `IJournal.Get`: `SuppressMessage` pontual com justificativa (só afeta implementações em VB.NET) |
| CA1512, CA1816, CA1861 | 1 cada | Corrigidos |
| Novos na Etapa 2 (CA1416, CA2201, CA1859, CA1869, CA1822, CA1826) | 22 | Corrigidos na origem (constantes fora da classe Windows-only, `Win32Exception`, opções JSON em cache, etc.) |

### Teste de mutação

Cada correção foi desfeita numa cópia descartável, uma por vez, e o teste correspondente rodado. **Detectadas:**
sequência do diário, A1, A2, A3, A4 (CLI sem trava e recuperação da execução própria), A5, A6, A7 (sem nenhuma trava;
falha nas 3 repetições), R2 (validação do alvo), R3 (superada), R4 (registro desatualizado), R6 (erro por item),
R7 (`--journal` sem valor), garantia somente leitura da descoberta (escrita de registro inserida) e da interface
(`new ActionExecutor` inserido).
**Primeira tentativa inválida, registrada:** a rodada inicial restaurou os fontes antigos por erro meu de script e os
testes rodaram contra binários velhos; foi refeita. **Mutação não detectada:** A7 com trava retirada de um único método.

### Defeitos encontrados nesta etapa e corrigidos

- Leitura de serviços: os ponteiros de nome apontam para dentro do buffer, que era lido num segundo bloco `fixed`;
  o coletor de lixo poderia mover o array entre os dois blocos. Agora o buffer é memória nativa.
- Diagnóstico concluía "Modo de Jogo ativado (padrão)", "HAGS no padrão do driver" e "sem elevação" **sem ter lido
  nada**: confundia "valor ausente no registro" com "não lido". Agora existe `ReadSignals` e `IsElevated` anulável;
  um teste exige "indisponível" em todo achado quando nada foi lido.
- Dados sintéticos de um teste SMBIOS violavam o formato (string vazia). O decodificador fez o certo e parou sem
  inventar; o dado do teste foi corrigido.

## Etapa 2 — Windows (PC do usuário), 2026-10-30 20:41 — log `nexus-validacao-20261030-204152.log`

Executado pelo usuário com `tools\etapa1-windows.ps1`, PowerShell não elevado. SDKs instalados: 8.0.421,
10.0.100-rc.2, **10.0.403** (usado; o `global.json` com latestFeature escolhe o mais novo da linha 10.0). Runtimes
NETCore.App 10.0.14 e WindowsDesktop.App 10.0.14.

| Passo | Exit | Resultado |
|---|---|---|
| `dotnet --info` / `--list-sdks` | 0 / 0 | |
| `dotnet restore Nexus.slnx` | 0 | 4 projetos novos restaurados |
| `dotnet build Nexus.slnx -c Release --no-restore` | 0 | **0 avisos, 0 erros** (inclui Nexus.Desktop) |
| `dotnet test ... --filter Category=Unit` | 0 | **99 aprovados**, 0 falhos |
| `dotnet test ... --filter FullyQualifiedName~CoordinationTests` | 0 | **9 aprovados**: processos reais, inclusive morte no meio da aplicação e da restauração |
| `Nexus.Discover --rows 400` | 0 | leitura completa em 0,09 s; **0 valores indisponíveis** |
| `Nexus.SmokeJournal` | 0 | "SQLite nativo carregado: 3.53.3" (no Windows) |
| `nexus-recover runs` / `scan` / `show` / `scan` (inexistente) | 0 / 1 / 0 / 3 | todos os esperados |
| SHA-256 do diário de teste | — | igual antes e depois |
| Codificação | — | **corrigida**: acentos íntegros no log ("ação, código, não, recuperação") e nos nomes de serviços |

**Leitura de hardware (resumo do log):** Windows 11 Pro 25H2, build 26200.9457 (o registro diz "Windows 10 Pro";
inferido corretamente pelo build) · AMD Ryzen 7 5800X3D, 8 núcleos / 16 threads, SMT ativo, 96 MB L3, sem arquitetura
híbrida · NVIDIA GeForce RTX 3080, driver 32.0.16.2101 (19/10/2026), 12 GB · 4×8 GB DDR4 3200/3200 MT/s · ASUS TUF
GAMING B550M-PLUS, BIOS 3002 (23/02/2023), SMBIOS 3.3, hardware físico · C:\ 953 GB (10% livre), D:\ 223,6 GB,
E:\ 953 GB; discos XPG GAMMIX S70 Blade (NVMe), P3-1TB, Kingston SA400S3 · plano ativo "Ultimate Performance
(ExitLag)" (personalizado) · Modo de Jogo ativado (1), captura em segundo plano desativada, HAGS configurado como ativado,
VBS e HVCI configurados como desativados · 313 serviços (130 em execução), SysMain desativado · 17 itens de inicialização
(6 habilitados, estado inferido).

**Defeito encontrado na leitura e corrigido depois:** a seção de rede contava 52 "adaptadores" (30 "ativos"). A maioria
eram **vínculos de filtro NDIS** (WFP, QoS Packet Scheduler, monitor de rede, filtros Wi-Fi; nome e descrição terminados
em "-0000"). Agora esses vínculos e os adaptadores ausentes são omitidos, com a contagem informada. "Conectado" passou a
exigir adaptador ligado com IPv4 fora de 169.254/16, e o tipo 53 (Tailscale) aparece como "Virtual (53, propVirtual)".
Teste novo com os nomes reais deste log (106 testes em Linux após a correção). **Essa correção ainda não rodou no Windows.**

**O que isso valida no Windows:** compilação, os 99 testes (inclusive coordenação entre processos reais), consultas
somente leitura do CLI e execução sem falhas da descoberta. **Não valida:** os valores lidos (você precisa conferir),
a interface (o log não diz se ela foi aberta), aplicação, rollback e recuperação real.

**Observação:** dar duplo clique em `Nexus.slnx` abriu "Escolher aplicativo" sem opções. Nenhum programa instalado está
associado ao formato `.slnx` (exige Visual Studio 2022 17.13+ ou 2026, Rider ou VS Code com C# Dev Kit). Para usar o
NEXUS isso não é necessário: `Abrir NEXUS.cmd` na raiz abre a interface já compilada.

## Etapa 3 — broker e kit de VM (Linux), 2026-10-30

| Comando | Exit | Resumo |
|---|---|---|
| `dotnet restore Nexus.slnx` (limpo) | 0 | 0 avisos |
| `dotnet build Nexus.slnx -c Release --no-restore` | 0 | **0 avisos, 0 erros** (inclui Nexus.Broker e Nexus.Broker.Host) |
| `dotnet test ... --filter Category=Unit` | 0 | **143 aprovados**, 0 falhos (BrokerTests: 36; CoordinationTests: 10) |
| `dotnet build tests/Nexus.IntegrationTests` (fora da solução) | 0 | 0 avisos; **não executado** (só em VM) |

Teste do pipe (`Named_pipe_round_trip_and_malformed_frames`): era instável na primeira versão do transporte (falhou 1 em
5, apontado pela revisão). Depois da correção B3/B4, passou em 8 de 8 repetições e nas 3 rodadas completas.

Revisão independente do broker: 15 achados (B1–B15), tratados ou registrados como pendentes em `07-recovery-cases.md`.

## Etapa 3 — Windows (PC novo do usuário, MATHEUSANJOS), 2026-10-31 02:25 — log `nexus-validacao-20261031-022559.log`

Executado pelo assistente por meio do PC vinculado (PowerShell **não elevado**, via `tools\etapa1-windows.ps1`).

| Passo | Exit | Resumo |
|---|---|---|
| `dotnet --list-sdks` | 0 | 10.0.403 |
| `dotnet restore Nexus.slnx` | 0 | |
| `dotnet build Nexus.slnx -c Release --no-restore` | 0 | **0 avisos, 0 erros** |
| `dotnet test ... --filter Category=Unit` | 0 | **143/143 aprovados** |
| `CoordinationTests` (processos reais) | 0 | **10/10** |
| `Nexus.Discover --rows 400` | 0 | **0 valores indisponíveis** (processo não elevado) |
| `nexus-recover runs / scan / show / scan inexistente` | 0 / 1 / 0 / 3 | todos os esperados |
| SHA-256 do diário de teste antes/depois do CLI | — | igual (`610F9D5F58D2B3F3…`) |

Testes de integração continuam **não executados** (só em VM). Conferência visual da interface contra o Gerenciador de
Tarefas: pendente.

## `nexus-recover accept` (R3) — Linux (contêiner), 2026-10-31

| Comando | Exit | Resumo |
|---|---|---|
| `dotnet build Nexus.slnx -c Release --no-restore` | 0 | **0 avisos, 0 erros** (SDK 10.0.403) |
| `dotnet test ... --filter Category=Unit` | 0 | **149 aprovados** (143 + 6 novos), 0 falhos |
| `dotnet build tests/Nexus.IntegrationTests` | 0 | 0 avisos; não executado |

Teste de esquema ajustado por mudança de especificação (esquema 3): agora compara com `SqliteJournal.SchemaVersion`,
exige que seja 3, migra v1 e v2 e recusa `SchemaVersion + 1`. Mutações: 3 de 3 detectadas; a quarta (CLI sem trava)
não compila.

## `accept` — Windows (MATHEUSANJOS), 2026-10-31 02:43 — log `nexus-validacao-20261031-024345.log`

Mesmo script, não elevado: restore/build 0 (**0 avisos**); **149/149**; `CoordinationTests` **11/11** (inclui o
teste do CLI `accept` com processos reais); descoberta 0 indisponíveis; CLI 0/1/0/3; SHA igual.

## Conferência da descoberta contra o próprio Windows — 2026-10-31 02:46

A interface não pôde ser vista pelo controle de tela (não é app instalado). Como ela mostra a mesma descoberta do
`Nexus.Discover`, a comparação foi feita entre esse relatório e uma coleta **somente leitura** do Windows
(`Get-CimInstance`, `Get-PhysicalDisk`, `Get-Volume`, `powercfg /getactivescheme`, `Get-NetAdapter`, `Get-Service`).

| Item | NEXUS | Windows | Resultado |
|---|---|---|---|
| Windows | 11 Home Single Language, 25H2, 26200.9711 | idem (UBR 9711) | igual |
| CPU | Ryzen 5 5600H, 6 núcleos / 12 lógicos, L3 16 MB, 3294 MHz | idem, `MaxClockSpeed` 3301 MHz | igual (frequência: fontes diferentes, ambas ~3,3 GHz) |
| GPUs e drivers | Radeon 27.20.14032.15001 (25/08/2021, 512 MB); GTX 1650 30.0.15.1274 (03/05/2022, 4 GB) | idem (datas WMI em horário local, 21:00 do dia anterior) | igual |
| Memória | 7,4 GB utilizáveis; 1 × 8 GB DDR4 3200/3200, SMS4WEC8C1K0446FCG | 7 897 944 064 B; idem | igual |
| Placa/BIOS | Acer Nitro AN515-45, CZ Scala_CAS, Insyde V1.14 (24/06/2026) | idem | igual |
| Máquina virtual | Não (inferido) | `HypervisorPresent=True` (hipervisor do próprio Windows) | correto: PC físico; o portão continua fechado |
| Disco | IM2P33F8ABR2-512, NVMe (inferido); SSD/HDD indisponível | IM2P33F8ABR2-512GB, SSD, NVMe | **diverge:** o modelo vem truncado em 16 caracteres pelo registro; tipo de mídia é limitação conhecida |
| Volume C: | 475,7 GB, 196,6 GB livres | 510 745 636 864 B, 211 141 165 056 B livres | igual |
| Rede | Wi-Fi MediaTek MT7921, 1,2 Gb/s | idem | igual |
| Energia | Acer, 8d5d9f56-… | idem | igual |
| SysMain | Em execução, Automático | idem | igual |

Nenhuma correção de código: a única divergência (modelo do disco truncado e tipo de mídia) depende de IOCTL/WMI, já
listada nas limitações.

## Testes de integração — VM Windows descartável (GitHub Actions), 2026-10-31

O Windows Sandbox não existe no Windows Home do PC atual. Workflow `.github/workflows/vm-integracao.yml` em
`windows-latest` (Azure, elevado). Descoberta na VM: "Microsoft Corporation / Virtual Machine", **Máquina virtual: Sim**,
processo elevado: Sim; arquivo de liberação criado com ACL de Administradores.

| Rodada | Commit | Resultado |
|---|---|---|
| #1 | 10caafc | Não iniciou: cobrança da conta bloqueada (resolvido pelo responsável) |
| #2 | 10caafc | **Falhou** num teste unitário: `CoordinationTests.Recovery_is_refused_while_another_process_is_restoring…` procurava texto acentuado na saída redirecionada do CLI, que nesta VM usa a página de código 437. Defeito do teste; corrigido em d592777 (motivo conferido pelo diário) |
| #3 | d592777 | 149/149 + integração 6/6, mas energia (sessão) e core parking caíram em "já configurado": a imagem já vem em Alto desempenho e com CPMINCORES no valor desejado |
| #4 | 95505d8 | Igual à #3, agora com o ramo de cada teste no log |
| #5 | 73c5540 | **149/149 + integração 6/6, todos com escrita real.** O workflow ativa o plano Equilibrado e põe CPMINCORES AC em 50 antes dos testes |

Ramos da rodada #5 (log):

| Teste | Antes | Resultado |
|---|---|---|
| Sessão de energia | Equilibrado (381b4222-…) | Verified → Alto desempenho → `EndSession` voltou ao Equilibrado |
| SysMain (admin) | Disabled | Verified → Manual → revertido para Disabled |
| Core parking AC (admin) | 50 | Verified → revertido para 50 |
| Modo de Jogo (HKCU) | — | aplicado, relido e revertido ao valor exato |
| Crash + `nexus-recover` | — | escrita real, crash simulado, CLI real restaurou e removeu a chave criada |
| Alvo protegido (HKLM\…\Lsa) | — | recusado por `Assess` e pela validação de restauração |

## Broker como serviço (B10, B3, B7, R1/B1) — 2026-10-31

Linux: build 0 avisos; **163/163** (14 novos). Mutações: não gravar o SID, aceitar HKCU sem perfil no modo serviço,
aceitar SID adulterado, transporte sem identidade, aceitar qualquer SID: 5 de 5 detectadas.

VM do GitHub Actions (rodada de 6b0db82; a anterior, 4ab33f0, falhou no `sc.exe create`: o PowerShell 5.1 quebra as aspas
do binPath, erro 1639, trocado por `New-Service`):

| Passo | Resultado |
|---|---|
| Unitários | 163/163 |
| Integração (handlers diretos) | 6/6 com escrita real (como antes) |
| `Install-Broker.ps1 -Start` | serviço `NexusBroker` LocalSystem, Running; `%ProgramData%\NEXUS`: Usuários (RX), Administradores/SYSTEM (F); `nexus.lock`: só Administradores/SYSTEM |
| `ServiceIntegrationTests` (cliente = usuário da VM) | 2/2. Execute pelo serviço: **Verified**, HKCU do cliente 0 → 1, HKCU do SYSTEM (`HKU\S-1-5-18`) intocado, `userSid` do cliente no diário, RevertRun voltou a 0. Cliente conferiu o dono do pipe (B3) |
| Log do serviço | `Conta: NT AUTHORITY\SYSTEM`, escrita liberada pela VM, Status/Execute/RevertRun ok |
| `Uninstall-Broker.ps1` | serviço e binários removidos; dados preservados |

Windows do responsável (MATHEUSANJOS, não elevado), 31/10 03:56, log `nexus-validacao-20261031-035641.log`: build 0
avisos, **163/163**, coordenação 11/11, descoberta 0 indisponíveis, CLI 0/1/0/3, SHA igual. Aqui roda o cenário do impostor
do B3, que exige processo não elevado: `Client_in_service_mode_refuses_a_pipe_owned_by_an_ordinary_user` **aprovado**
(o cliente recusou um pipe criado pelo usuário comum com o nome do broker).

## Pendente no Windows (esta versão)

1. ~~Rodar `tools\etapa1-windows.ps1`~~ — feito em 2026-10-31 (acima).
2. ~~Conferir os valores~~ — feito pela descoberta (acima). Falta só olhar a interface em si (abre com `Abrir NEXUS.cmd`).
3. Testes de aplicação e rollback reais: `tools\vm\README.md` (Windows Sandbox).
