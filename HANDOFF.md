# HANDOFF — NEXUS Performance Lab (escrito em 2026-10-30)

> **Documento novo, não é o HANDOFF.md original.** O original, o CLAUDE.md e o `docs/04-validation-plan.md` foram citados
> nas instruções mas **não estavam na pasta recebida**. Nada aqui reproduz o conteúdo deles; este arquivo descreve o
> estado **verificado** do projeto nesta data. Se os originais aparecerem, compare antes de confiar em qualquer um.

## 0. Retomar daqui (pendências do responsável, registradas em 31/10/2026 01:59)

Ainda **não feitas** pelo responsável:

1. [x] **(Feito em 31/10 02:30.)** **Tornar o repositório privado.** Ele foi criado público: github.com/robinexe/nexus → Settings → "Change
   repository visibility" → Make private. A sessão do assistente não tem permissão para mudar isso.
2. [x] **(Feito pelo responsável em 31/10 02:49; conferido na lista de tokens.)** **Revogar o token do GitHub colado na conversa de 31/10** (GitHub → Settings → Developer settings → Fine-grained
   tokens). O token nunca foi gravado no repositório. Ele nem foi usado para o envio, mas ficou no histórico da conversa.
3. [x] **(Feito em 31/10 02:25 no PC novo: 143/143, 0 avisos, coordenação 10/10, descoberta 0 indisponíveis; ver `docs/06-build-log.md`.)** Validar a Etapa 3 no Windows: `powershell -ExecutionPolicy Bypass -File .\tools\etapa1-windows.ps1`. O
   esperado são 143 testes aprovados e 0 avisos. Devolver o log gerado em `%TEMP%`.
4. [x] **(Feito em 31/10 03:25.)** Testes de integração reais: o Windows Sandbox não existe no Windows Home, então
   rodam numa VM Windows descartável do GitHub Actions (`.github/workflows/vm-integracao.yml`). **6/6 com alteração e
   reversão reais** (registro, Modo de Jogo, crash + `nexus-recover`, sessão de energia, SysMain, core parking AC).
   Para rodar de novo: GitHub → Actions → vm-integracao → Run workflow (dispara sozinho quando o workflow ou os testes
   de integração mudam). Custo: dentro da cota gratuita (≈ 3 min por rodada; limite de gasto US$ 0).
5. [x] **(Decidido em 31/10, por delegação do responsável: "faça tudo, não me pergunte nada".)** A1: mantido
   (restaura e marca como inferido). R3: `nexus-recover accept <entryId>` implementado (esquema do diário 3).
   R5: mantido (o `--force` confirmado vale para a nova tentativa da mesma entrada). Detalhes e motivos no fim de
   `docs/07-recovery-cases.md`. Pode ser revisto a qualquer momento.

### Começar num PC novo

```powershell
git clone https://github.com/robinexe/nexus.git
cd nexus
dotnet --list-sdks          # precisa de um SDK 10.0.1xx ou superior (testado com 10.0.403); senão instale o .NET 10 SDK
dotnet restore Nexus.slnx --locked-mode
dotnet build Nexus.slnx -c Release --no-restore
dotnet test  Nexus.slnx -c Release --no-build --filter Category=Unit
```

As regras da seção 2 valem também no PC novo. Se ele for de jogos, nada de alterações reais nele; só em VM.

## 1. Estado em uma linha

Etapa 2 validada no Windows: 0 erros, 0 avisos, 99/99, inclusive coordenação entre processos reais. A interface
**somente leitura** abriu no PC do usuário. A Etapa 3 (broker e kit de VM) passa em 143/143 em Linux e, desde 31/10, também no
Windows (PC novo; 149/149 com o `accept`). Os testes de integração rodaram numa VM Windows descartável (GitHub Actions)
em 31/10: **6/6 com alteração e reversão reais**. A aplicação de otimizações reais continua **bloqueada** no Windows
hospedeiro: o portão exige VM, e a interface ainda não tem caminho de execução (itens 4–6 da seção 7).

## 2. Regras que continuam valendo

- Este PC é ao mesmo tempo máquina de desenvolvimento e PC de jogos. Não alterar registro, serviços, tarefas, planos de
  energia, drivers nem arquivos de jogos. Não rodar nada como administrador.
- Testes de alteração real só em VM Windows isolada.
- Não escolher licença para o NEXUS. Dependência nova só com registro em `docs/05-dependencies.md` e aviso ao responsável.
- Nunca mudar um teste só para fazê-lo passar.
- Execução de otimizações no hospedeiro fica bloqueada até os testes de integração e recuperação em VM serem aprovados.

## 3. Evidência por ambiente

| Ambiente | Versão | Resultado | Onde está |
|---|---|---|---|
| Linux (contêiner) | Etapa 1 | 55/55 | `docs/06-build-log.md` |
| **Windows (PC do usuário)** | Etapa 1 | restore 0; build 0 (61 avisos); **55/55**; CLI runs/show 0, scan 1/3 esperados; SHA igual | idem (informado pelo usuário) |
| Linux (contêiner) | Etapa 2 | restore 0; build 0 (**0 avisos**); **99/99**; smoke ok | idem |
| **Windows (PC do usuário)** | Etapa 2 | restore 0; build 0 (**0 avisos**); **99/99**; coordenação 9/9; descoberta sem falhas (0 indisponíveis); CLI 0/1/0/3; SHA igual | `docs/06-build-log.md` |
| Linux | Etapa 2 + correção de rede | 106/106 | idem |
| Linux | **Etapa 3 (atual: broker + kit de VM)** | 143/143, 0 avisos; integração compila, **não executada** | idem |
| **Windows (PC novo, MATHEUSANJOS)** | Etapa 3 | restore/build 0 (**0 avisos**); **143/143**; coordenação 10/10; descoberta 0 indisponíveis; CLI 0/1/0/3; SHA igual | `docs/06-build-log.md` |
| **VM Windows (GitHub Actions, windows-latest, elevada)** | Etapa 3 + `accept` | 149/149 unitários; **integração 6/6** com escrita e reversão reais; descoberta "Máquina virtual: Sim" | `docs/06-build-log.md` |

## 4. Mapa do código

| Projeto | Papel | Escreve no sistema? |
|---|---|---|
| `src/Nexus.Core` | Modelo, resolvedor, executor, diário em memória, recuperação, coordenação (contratos), catálogo, `SimulationOnlyHandler`, `IRestoreValidator` | Não (só via handlers) |
| `src/Nexus.Persistence` | `SqliteJournal` (esquema 2), `FileRunCoordinator` (trava do SO), `RunLock` legado | Só o diário e a trava |
| `src/Nexus.WindowsActions` | Handlers tipados: registro, preferência DirectX, StartupApproved, energia, serviços; `SafetyPolicy` | **Sim** (único lugar, além do CS2) |
| `src/Nexus.GameProfiles` | CS2 `.cfg` (escrita atômica, anti-symlink) | **Sim** (arquivos `.cfg`) |
| `src/Nexus.HardwareDiscovery` | Descoberta **somente leitura** + diagnóstico | Não (garantido por teste de metadados) |
| `src/Nexus.Desktop` | Interface WPF somente leitura | Não executa nada (garantido por teste); grava só o próprio log de erros e o JSON exportado pelo usuário |
| `src/Nexus.Recovery.Cli` | `nexus-recover` | `recover`/`revert` **sim**, com a trava e o portão (fora de VM liberada: exit 7; `--host-emergency` é exceção explícita); `accept` só o diário (com a trava, sem portão); `runs`/`show`/`scan` não |
| `src/Nexus.Broker` | Protocolo fechado, `BrokerService` (motor completo do lado do broker), `CatalogBoundHandler`, portão `VmOnlyExecutionGate`, transporte por pipe | Só via portão + trava |
| `src/Nexus.Broker.Host` | `nexus-broker`: console ou serviço Windows (`--service`, LocalSystem); pipe com ACL; como serviço, HKCU = perfil de quem pede (B10) | No PC físico: nada (nem cria arquivos) |
| `tools/service` | `Install-Broker.ps1` / `Uninstall-Broker.ps1` (elevados): binários, `%ProgramData%\NEXUS` e `nexus.lock` com ACL, serviço `NexusBroker` | Sim (só onde for instalado; hoje só a VM do CI) |
| `tests/Nexus.IntegrationTests` | Testes com handlers **reais**, fora da solução | Só em VM liberada (portão antes de tudo) |
| `tools/vm` | Windows Sandbox: `Prepare-Sandbox.ps1` (hospedeiro), `Inside-Sandbox.ps1` (dentro) | Hospedeiro: só builds e `vm-saida`/`vm-config` |
| `src/Nexus.TraceAnalysis`, `src/Nexus.Experimentation` | Parser PresentMon, métricas, comparação estatística | Não |
| `tests/Nexus.Tests` | 149 testes (todos `Category=Unit`; isolados em memória/%TEMP%) | Só em %TEMP% |
| `tests/Nexus.TestHelper` | Processo auxiliar dos testes de coordenação | Só no diretório temporário do teste |
| `tools/Nexus.SmokeJournal` | Gera diário de teste (registro em memória) | Só o arquivo `.db` pedido |
| `tools/Nexus.Discover` | Relatório de texto da descoberta | Não |
| `tools/etapa1-windows.ps1` | Validação local no Windows (não elevado) | Só `%TEMP%` |

## 5. Comandos reproduzíveis

```powershell
# Requisito: .NET SDK 10.0.1xx ou superior na linha 10.0 (global.json: 10.0.100 + latestFeature). Testado com 10.0.403.
dotnet --list-sdks
dotnet restore Nexus.slnx                       # lock files versionados; use --locked-mode para garantir reprodução
dotnet build Nexus.slnx -c Release --no-restore # esperado: 0 erros, 0 avisos
dotnet test  Nexus.slnx -c Release --no-build --filter Category=Unit   # esperado: 149 aprovados

# Tudo de uma vez, com log (UTF-8) em %TEMP%\nexus-validacao-<data>.log:
powershell -ExecutionPolicy Bypass -File .\tools\etapa1-windows.ps1

# Descoberta de hardware em texto (somente leitura):
dotnet build tools\Nexus.Discover -c Release
dotnet tools\Nexus.Discover\bin\Release\net10.0\Nexus.Discover.dll --rows 400

# Interface (somente leitura): duplo clique em "Abrir NEXUS.cmd" na raiz (compila se preciso), ou:
src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe
# Abrir a solução (.slnx) no editor exige Visual Studio 2022 17.13+/2026, Rider ou VS Code + C# Dev Kit; não é preciso para usar.

# Recuperação, somente leitura, contra um diário de TESTE:
dotnet tools\Nexus.SmokeJournal\bin\Release\net10.0\Nexus.SmokeJournal.dll $env:TEMP\t\smoke.db
src\Nexus.Recovery.Cli\bin\Release\net10.0\nexus-recover.exe scan --journal $env:TEMP\t\smoke.db
```

Códigos de saída do `nexus-recover`: 0 ok · 1 há o que recuperar / recuperação parcial · 2 uso inválido · 3 diário não
encontrado · 4 `accept`: entrada inexistente ou não pendente · 5 **recusado: execução ativa** · 6 recusado: não foi
possível verificar · 7 **recusado: portão de execução** (fora de VM de teste liberada).

```powershell
# Testes de aplicação/rollback REAIS, isolados no Windows Sandbox (ver tools\vm\README.md):
powershell -ExecutionPolicy Bypass -File .\tools\vm\Prepare-Sandbox.ps1   # no PC, não elevado: só compila e gera vm-config\nexus-sandbox.wsb
# depois: duplo clique em vm-config\nexus-sandbox.wsb (resultados em vm-saida\)
```

## 6. Decisões de desenho desta etapa (resumo)

- **Coordenação:** trava exclusiva do SO em `<dir do diário>\nexus.lock`, aberta só para leitura e com `FileShare.None`.
  Liberada pelo SO quando o processo morre. O arquivo nunca é apagado. O registro de dono (`nexus.lock.owner.json`)
  só é exibido se PID e horário de início corresponderem a um processo vivo. O executor de produção exige a trava; o
  construtor sem trava é `internal`. A recuperação nunca toca a execução vinculada à própria trava.
- **Diário:** estado `Restoring` gravado antes de toda restauração. O esquema passou a 2, para que versões antigas recusem abrir.
- **Restauração:** só sobrescreve valores que o diário prova terem vindo do NEXUS, e revalida alvo e original
  (`IRestoreValidator`, `SafetyPolicy`) antes de escrever.
- **Interface:** handlers embrulhados em `SimulationOnlyHandler` (Apply/Restore lançam exceção), diário em
  `OpenReadOnly`, coordenação só sondada.
- **Descoberta:** cada valor é detectado, inferido ou indisponível, com fonte e motivo. Nada é inventado; um teste
  exige "indisponível" em todo diagnóstico quando nada foi lido.

Detalhes: `docs/07-recovery-cases.md` (casos e revisão independente) e `docs/06-build-log.md` (comandos e evidências).

## 7. Pendências, em ordem

1. ~~Validar a Etapa 3 no Windows~~ (feito em 31/10: 143/143). Rodar de novo `tools\etapa1-windows.ps1` com o
   `accept`: feito (149/149 no Windows). Valores da descoberta conferidos contra o próprio Windows: tudo igual, exceto
   o modelo do disco (truncado) e SSD/HDD, que são limitações conhecidas (`docs/06-build-log.md`).
2. ~~Decisões em aberto~~ (decididas em 31/10; ver `docs/07-recovery-cases.md`).
3. ~~Rodar os testes de integração em VM~~ (feito em 31/10 no GitHub Actions: 6/6 reais).
4. ~~Broker como serviço~~ (feito em 31/10: B10, B3, B7, R1/B1; validado na VM do CI com o serviço instalado, ver
   `tools/service/README.md`; impostor do B3 recusado no Windows do responsável, 163/163). Pendente só a assinatura
   Authenticode do cliente (exige certificado de assinatura de código).
5. **Interface → broker (próximo):** a interface fala com o serviço por `BrokerClient` (`BrokerServerKind.Service`):
   simulação pelo broker, depois aplicar/reverter com confirmação. A execução real continua presa ao portão de VM.
6. Só depois da VM aprovada: liberar execução real (hoje não existe caminho de execução na interface).
7. Captura PresentMon e comparação de sessões; depois NVIDIA DRS, VALORANT, investigador de stutters, laboratório A/B,
   relatórios e, por último, a IA.

## 8. Limitações conhecidas

- Descoberta: SSD/HDD indisponível (exige IOCTL/WMI); tarefas agendadas fora da lista de inicialização; estado efetivo
  de HAGS e VBS exige APIs não implementadas (mostra-se só a configuração); "velocidade máxima" da memória é a anunciada
  ao firmware (geralmente JEDEC), não o perfil XMP/EXPO.
- O relatório da descoberta e o JSON exportado contêm nome do computador, IPs e caminhos de programas. O usuário decide
  se compartilha.
- `SourceCfgDocument.Diff` é posicional; um `.cfg` criado pelo NEXUS fica vazio após restaurar; chaves-pai de registro
  criadas acima da última não são removidas; formato do dump `cvarlist` e unidade de `CPUStartTime` do PresentMon a
  confirmar com dados reais.
- O `scan` do CLI lê um diário em modo WAL, e o SQLite cria `-wal`/`-shm` auxiliares; o arquivo principal não muda.
