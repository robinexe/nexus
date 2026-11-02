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

Em 31/10/2026 (noite): 1282 testes unitários verdes (Linux e Windows do CI), 0 avisos. Rodada 2: Laboratório de engenharia
reversa (Processador, Placa de vídeo, Memória RAM — pedido do responsável que tinha se perdido), Limpeza v2 (esvaziar
quarentena e Lixeira, regras do usuário em JSON, histórico), Automático v2 (agendas diária/semanal/mensal, limpeza segura
agendada opt-in, bandeja, iniciar com o Windows, página Ajustes), Verificar arquivos (hash + regras + heurística de PE),
núcleo v2 (lote, ações efêmeras, SafeFileMover por handle), empacotamento (instalador Inno Setup, ZIP portátil,
release.yml, ícone). Falta da paridade com o Kudu: 30 idiomas (só pt-BR). Ver `docs/modules/*.md` e `docs/08-packaging.md`.

Rodada 1 (mesmo dia): Em 31/10/2026 (tarde): 741/741 testes no Linux, 0 avisos. Nove módulos novos no nível do Kudu ou acima (Limpeza, Disco,
Programas, Inicialização, Serviços, Privacidade, Segurança, Monitor, Ferramentas do sistema, Automático) + CLI `nexus`.
A interface aplica neste PC por um aplicador sob demanda (usuário sem aviso; administrador com o aviso do Windows e ponto
de restauração), sempre com confirmação, diário e desfazer. Revisão de segurança independente feita (8 correções). VM do CI: 741/741 unitários e 29/29 de integração com escrita real (23 dos módulos).
Aberto no Windows do responsável às 12:16: todas as páginas novas abrem e leem dados reais (Limpeza 9,6 GB recuperáveis, Defender, monitor ao vivo, 82 programas, 82 redes Wi‑Fi). Dois defeitos achados ali e corrigidos (netsh em UTF‑8 sem recuo; `ipconfig /displaydns` exige admin no build 26200). Nenhuma escrita feita no PC pelo assistente; as escritas novas foram exercitadas na VM do CI.

## 2. Regras que continuam valendo

- **Mudou em 31/10 por decisão do responsável:** o NEXUS pode aplicar no PC do usuário (qualquer PC), "sem quebrar nada":
  toda escrita pelo aplicador, catálogo fechado, diário antes de escrever, releitura, desfazer, quarentena em vez de apagar,
  UAC + ponto de restauração para mudanças de sistema, alvos proibidos, confirmação explícita. Ações irreversíveis
  (exclusão segura) têm contrato próprio (`docs/modules/disk.md`) com confirmação dupla.
- O assistente não clica "Aplicar" em mudanças de sistema no PC do responsável por conta própria; testes de escrita
  automatizados continuam na VM descartável do CI.
- Não escolher licença para o NEXUS. Dependência nova só com registro em `docs/05-dependencies.md` e aviso ao responsável.
- Nunca mudar um teste só para fazê-lo passar.
- Módulos: um projeto `src/Nexus.<Módulo>` com `<X>Handlers.Create(HandlerEnvironment)` (registrado em
  `src/Nexus.Broker.Host/ModuleHandlers.cs`), catálogo `catalog/<módulo>.v1.json`, página `ModulePage` que só escreve por
  `IModuleHost` (`src/Nexus.Desktop/ViewModels/ModuleHost.cs`), doc em `docs/modules/`.

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
5. ~~Interface → broker~~ (feito em 31/10): página Broker, única via de escrita da interface, por
   `BrokerSession.ForInstalledService` (dono do pipe conferido); aplicar com confirmação, só com escrita liberada pelo
   serviço. Validado na VM (mesma via contra o serviço instalado) e aberto no Windows do responsável (sem serviço: mensagem
   correta). A execução real continua presa ao portão de VM.
6. **Liberar execução real no PC físico:** decisão do responsável, não técnica. Exige (a) instalar o serviço no PC
   (administrador) e (b) mudar o portão, hoje só VM. Recomendação: antes disso, a medição (item 7) para saber se alguma
   ação do catálogo melhora algo neste PC; aplicar sem medir contraria o propósito do laboratório.
7. ~~Captura PresentMon e comparação de sessões~~ (feito em 31/10: páginas Medir e Comparar; `Nexus.Measurement`). Falta
   uma captura real: baixar o PresentMon oficial (github.com/GameTechDev/PresentMon) e indicar o caminho em Medir.
8. ~~Redesign da interface~~ (feito em 31/10: "bancada de telemetria"; tokens em `src/Nexus.Desktop/Themes/Tokens.xaml`,
   estilos em `Controls.xaml`, uma página por arquivo em `Views/Pages/`). Conferido no Windows do responsável.
9. ~~Módulos nível Kudu~~ (feito em 31/10 com 9 agentes em paralelo + revisor; ver `docs/modules/*.md`).
   **Pendências deles:** abrir cada página no Windows real e conferir bindings/leituras; `PersistenceKind.Ephemeral` para
   ações sem estado (DNS/ARP/ponto de restauração); esvaziar quarentena e Lixeira usando o contrato irreversível; mesma ação
   com argumentos diferentes num pedido (remoção em lote); fechar a corrida restante de junção entre a checagem e o
   `Move` com operações por handle (revisão: médio); toast da verificação semanal.
10. **Rodada 2 (31/10 noite):** Laboratório + paridade com o Kudu. Defeitos achados só no Windows do CI e corrigidos:
    `SetFileInformationByHandle` recusa RootDirectory (trocado por `NtSetInformationFile`), tamanho do FILE_RENAME_INFO,
    trava do aplicador elevado com `CreateNew` sem direito de escrita (teria bloqueado todas as ações de administrador),
    constante duplicada no script do Inno Setup. **Pendências:** abrir as páginas novas no Windows do responsável;
    idiomas (i18n); auto-desfazer da taxa do monitor (precisa de `RevertAsync` sem confirmação no IModuleHost);
    lançador sem console para a tarefa agendada; `Install-Broker.ps1 -InPlace`.
    **Estado ao fim do dia 31/10:** VM do CI 44/44 de integração; `release.yml` verde (instalador instalado e desinstalado
    em silêncio na VM, ZIP testado; artifact `nexus-0.9.0-ci.5`). No PC do responsável a versão nova abriu uma vez e
    **travou a interface por ~2–3 min** logo depois da leitura; numa segunda tentativa não apareceu (causa não achada).
    Diagnóstico permanente adicionado: `%LocalAppData%\NEXUS\desktop-diag.log` (etapa atual + "INTERFACE PARADA há X s").
    Próximo passo: abrir pelo `Abrir NEXUS.cmd`, reproduzir e ler esse log; `_claude\diag-start.cmd` abre esperando e
    anota código de saída + erros do .NET no Visualizador de Eventos.
11. **31/10 noite (pedidos do responsável):** página Atualizar drivers (Windows Update oficial + backup/desfazer pelo pnputil;
    GPU com link oficial), desinstalação em massa na aba Programas (fila sequencial, MSI /qb-, 1618/3010, ponto de restauração,
    sobras de todos), Memória: diagnóstico (vazamentos por tendência, pool do kernel por tag, soluções temporárias e
    permanentes), Medir v2 (jogo detectado → captura automática PresentMon, antes x depois automático, ao vivo, mostrador
    de FPS sem injeção; página Comparar absorvida). Defeito real achado no PC: ProgressBar ligada a propriedade só leitura
    (pilha de janelas de erro) → corrigido + teste `XamlBindingTests`. CI: 1504 unitários e 51/51 integração no Windows.
12. **31/10 noite (2):** Assistente completo (13 etapas, relatório HTML, desfazer tudo), autoteste no PC real
    (`tools\selftest\Rodar-Autoteste.cmd` → `%LocalAppData%\NEXUS\autoteste\<data>\`, 37 verificações + todas as páginas
    fotografadas; roda também na VM do CI), design system v2 (ilustrações vetoriais, ícones, componentes animados) aplicado
    às 27 páginas. VM do CI: 37 verificações sem FALHA e 27 páginas abertas sem exceção.
13. **01/11 madrugada:** PresentMon 2.6.0 oficial embutido (conferido por SHA-256/assinatura Intel), ação "medir sem pedir
    permissão" (grupo Usuários do log de desempenho), instalador Inno validado no CI (instala, confere, captura, desinstala).
    Teste ponta a ponta do Assistente com aplicador real em Windows Server 2022 e 2025: 0 defeitos depois de corrigir a
    limpeza que vinha desmarcada; drivers: backup/restauração real, dispositivos sem driver, busca no WU (no PC real achou
    oferta Apple com versão entre parênteses — corrigido). Auditoria de fiação: 5 defeitos reais corrigidos + WiringTests.
    Simulação deixou de marcar "bloqueada" ações de administrador. 1669 testes unitários.
14. **01/11 manhã:** tela de progresso (anel, % real, tempo restante suavizado, etapas) no Assistente e em toda aplicação
    (sobreposição na janela); atualização automática assinada (Nexus.Updates, ECDSA P-256; falta criar o repositório público
    robinexe/nexus-releases e os segredos NEXUS_UPDATE_SIGNING_KEY/NEXUS_RELEASES_TOKEN — docs/11-atualizacao.md); drivers de
    vídeo NVIDIA/AMD baixados, conferidos (assinatura) e instalados dentro do app; correção grave: interface aberta como
    administrador fazia o aplicador de usuário nascer elevado e recusar %LocalAppData% (tudo "bloqueado") — agora usa o
    aplicador de administrador. Ponto de restauração do limite de 24 h não é mais erro. 1900 testes.
15. **01/11 madrugada (PC real):** Aplicar consulta o aplicador sozinho; pedidos >64 viram lotes (desfazer todos); RequestsFor só
    envia valor com allowedValues (o plano de energia derrubava o pedido); ação recusada na validação é pulada sem derrubar o
    resto; broker aplica/desfaz ajustes normais com sessão aberta (trava emprestada; só outro ajuste de sessão espera);
    teste do catálogo inteiro (94 ações) aplicando e desfazendo na VM 2022/2025; drivers AMD sem compressão HTTP;
    Inicialização com desligar em massa. Cópia de desenvolvimento (bin\Release gravável) não roda ações de administrador:
    usar o instalador. Atualização automática: script _claude\Configurar-Atualizacoes.cmd (usuário roda; a sessão foi
    bloqueada de criar repositório público e gravar segredos).
16. **01/11 manhã:** pesquisa (3 agentes, web/GitHub/fóruns) → catalog/tuning.v1.json: 29 ajustes (MPO, mouse 1:1, Power
    Throttling, EPP/estado mínimo/ASPM/USB/NVMe na tomada, Otimização de Entrega, Recall/Click to Do, animações, NTFS,
    Inicialização Rápida, PcaSvc/TrkWks, IPv4 preferido) + 10 mitos com fonte (docs/modules/tuning.md); aplicados e desfeitos
    na VM 2022/2025. Assistente mostra o resto do catálogo como "Avançado" desmarcado. Conferência no PC real (script só
    leitura): 79 de 89 ajustes no valor aplicado; limpeza real (23 mil arquivos na quarentena).
17. **01/11 noite:** driver NVIDIA (DRS via nvapi64, handler NvidiaDrsSetting, catalog/nvidia.v1.json, docs/modules/nvidia.md):
    cache de sombreador global; por jogo: baixa latência On, desempenho máximo, limite de FPS, V-Sync para G-SYNC. Página
    Placa de vídeo › "Driver NVIDIA por jogo". Layouts conferidos com gcc contra nvapi.h; falta testar num driver real
    (GTX 1650 / 512.74 do usuário). VM sem NVIDIA: NotApplicable sem escrever. 1957 testes.
18. **Próximos:** NVIDIA DRS, VALORANT, investigador de stutters, laboratório A/B guiado (alternar antes/depois automaticamente),
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
