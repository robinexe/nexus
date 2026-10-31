# Módulo Automático (e a linha de comando `nexus`)

> "Se adapta ao PC que inicia": o NEXUS lê o hardware, classifica o PC e só recomenda o que faz sentido **para ele**,
> incluindo o que **não** fazer. Nada muda sem o usuário ver o plano e confirmar.

Código: `src/Nexus.Automation` (lógica pura + leituras do Windows), `src/Nexus.Cli` (executável `nexus`),
página `ViewModels/Modules/AutomationPage.cs` + `Views/Pages/Automation.xaml`, catálogo `catalog/automation.v1.json`,
regras `src/Nexus.Automation/Rules/recommendations.v1.json` (recurso embutido), testes `tests/Nexus.Tests/Modules/AutomationTests.cs`
e `tests/Nexus.IntegrationTests/Modules/AutomationIntegrationTests.cs` (VM).

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

1. Candidatos = recomendações do tipo Recomendado, risco baixo, sem escolha do usuário e com valor definido (inclui limpeza
   segura `cleaning.*` de risco baixo quando existir).
2. Simulação no **aplicador do usuário** (`Host.PlanAsync(..., User)`, sem aviso do Windows). Uma ação inválida derruba o lote
   no broker, então a simulação cai para uma a uma. Ação de administrador que volta "Bloqueada: requer o broker elevado" vai
   para o nível administrador (o aplicador elevado simula de novo antes de escrever); "já está assim", "não se aplica" e
   bloqueios ficam fora do plano com o motivo.
3. O plano é mostrado (n ações, ganhos honestos, nível de cada uma). Um `Host.ApplyAsync` por nível: **usuário primeiro**, depois
   **administrador** com a explicação de que o Windows vai pedir permissão e um ponto de restauração será criado.
4. Resultado e **Desfazer tudo**: `Host.RevertAsync` para cada execução, na ordem inversa.

## 4. Verificação semanal agendada

Ação `automation.scheduled-scan.weekly` (handler `AutomationScheduledScan`, privilégio **User**). Valores permitidos:
`SUN 19:00`, `SAT 12:00`, `WED 20:00` e `OFF` (desligar é uma execução normal, também reversível).

- **Detect**: `schtasks /query /fo csv /nh` (existe?) + `schtasks /query /tn NEXUS-VerificacaoSemanal /xml ONE`. Estados:
  `OFF`, `"DIA HH:MM"` (tarefa do NEXUS apontando para este `nexus.exe` com `scan --report --quiet`), `STALE` (do NEXUS, mas
  outro caminho/argumentos) e `FOREIGN` (tarefa de outro com o mesmo nome: bloqueada, nunca tocada). O XML de uma tarefa do
  NEXUS vai para `Detail["xml"]` (até 32 KB).
- **Apply**: gera o XML (esquema 1.2, `CalendarTrigger` semanal, `LogonType InteractiveToken`, `RunLevel LeastPrivilege`,
  limite de 15 min, prioridade 7, `StartWhenAvailable`) com `XmlWriter` (escapa tudo) e registra com
  `schtasks /create /tn ... /xml <arquivo UTF-16> /f`, argumentos em lista (sem shell).
- **Restore**: original `OFF` → remove; original com XML → recria exatamente o XML anterior, depois de validá-lo
  (`IRestoreValidator`: marcador do NEXUS, executável válido, mesmos argumentos).
- **Validação do executável** (`ValidateExecutable`): caminho local com letra de unidade, sem UNC, sem aspas, sem `%` (o
  Agendador expandiria variáveis), sem caracteres de controle, sem `.`/`..`, terminando em `nexus.exe`. Testado com espaços,
  `&`, apóstrofo e tentativas de injeção (`"`, `</Command>`, `& calc`).
- Com `OtherAccount=true` (serviço/outra conta) a ação é recusada: a tarefa seria criada para a conta errada.
- `nexus scan --report` só lê o PC e grava `scan-AAAAMMDD-HHMMSS.json` e `.html` em `%LocalAppData%\NEXUS\reports`
  (sem nome do computador nem IPs).

**Decisão de segurança: o NEXUS nunca agenda ações que escrevem.** Aplicar é sempre com o usuário presente, vendo o plano e
confirmando (item `automation.scheduled-apply` está em `rejected` no catálogo). O agendamento só faz verificação.

## 5. Linha de comando `nexus`

`nexus --help` (português). Leitura: `scan [--report] [--json]`, `recommend`, `plan <ids>`, `history [runId]`,
`measure <csv>`, `compare --before … --after …`, `report`, `schedule status`. Mudança (sempre pelo aplicador, com confirmação
no terminal ou `--yes`, diário e desfazer): `optimize`, `apply <id[=valor]>…`, `revert <runId>`, `schedule on|off [--at]`.
O `nexus-broker` é copiado ao lado (target `CopyBrokerHost`, como na interface) e o catálogo também.
Códigos de saída: 0 ok · 1 concluído com problemas · 2 uso inválido · 3 não encontrado · 4 cancelado · 5 aplicador recusou ou
não respondeu · 6 não suportado neste sistema.

## 6. Riscos e limites

- Classificação de GPU por nome pode errar em modelos novos (fica "integrada" e o PC não vira "gamer": erro conservador).
- Tipo de disco: alguns controladores RAID/USB não respondem à consulta de penalidade de busca → "não determinado".
- Toast de notificação da verificação semanal: **não feito** (exige WinRT/pacote); o relatório fica na pasta.
- A interface precisa do `nexus.exe` ao lado do aplicador para ligar a verificação semanal (ver relatório do módulo: cópia
  da CLI na saída do Desktop). Sem ele, a ação volta "não se aplica" com o motivo.
- Recomendações usam a leitura do PC do momento; depois de aplicar, o plano é conferido de novo no aplicador (o que foi
  aplicado aparece como "já está assim").
