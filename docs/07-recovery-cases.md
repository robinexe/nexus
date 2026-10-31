# Casos ambíguos de recuperação — análise e tratamento (2026-10-30)

A releitura do estado mostra só o valor **atual**. Ela não diz quem escreveu esse valor nem qual estado deve ser
restaurado. Este documento registra os sete casos encontrados na revisão da Etapa 1 (A1–A7), os achados da revisão
independente da Etapa 2 (R1–R10) e o que ainda depende de decisão.

> `docs/04-validation-plan.md`, citado como fonte dos "casos ambíguos já documentados", **não estava na pasta
> recebida**. Esta análise foi feita do zero e não reproduz aquele documento. Quando ele aparecer, os casos precisam
> ser conferidos um contra o outro.

## Regra geral adotada

1. **Intenção gravada antes de escrever.** `Applying` antes de aplicar e, agora, `Restoring` antes de restaurar.
2. **Um valor só é sobrescrito sem `--force` quando o diário prova que veio do NEXUS:**
   - o valor desejado;
   - em `Failed`, o valor relido logo após o próprio Apply;
   - em `Restoring`, o valor que a restauração estava autorizada a sobrescrever, ou o que ela mesma deixou.
3. **Qualquer outro valor é conflito:** nada é escrito e o caso continua visível na recuperação até alguém decidir.
4. **Alvo e original lidos do diário são revalidados antes de escrever** (forma, tipos e política de segurança).
5. **Recuperação só com a trava de execução**, que prova que nenhum outro executor está vivo.

## Casos A1–A7

| # | Cenário | Risco | Comportamento adotado | Teste |
|---|---|---|---|---|
| **A1** | Crash entre `Applying` e `Applied`, com o valor atual igual ao desejado | Se o Apply nunca escreveu e outro programa gravou exatamente esse valor, restaurar desfaz a escrita dele | Restaura o original e **marca o resultado como inferido** (`RevertResult.Inferred`, nota no diário, aviso no CLI). A janela é a própria chamada ao Apply; a coincidência exata é improvável. **Decisão sua abaixo** | `RecoveryCaseTests.A1_*` (2) |
| **A2** | Apply grava um 3º valor (API "mente") e o processo morre durante a compensação | Antes: a recuperação via um valor que o próprio NEXUS produziu como "conflito externo" e nunca terminava | O valor relido após o Apply vai para `LastObserved` com o estado `Failed`; a compensação grava `Restoring` antes de escrever. A recuperação restaura esse valor sem `--force`; valor diferente continua sendo conflito | `A2_value_written_by_nexus…`, `A2_crash_after_restoring…`, `A2_external_change…` |
| **A3** | Restauração não confirmada (o handler deixou um valor intermediário) | Antes: a nova tentativa via o intermediário como conflito externo | Novo estado `Restoring` (esquema 2) com o valor deixado em `LastObserved`. A nova tentativa conclui sem `--force`; alteração externa posterior é conflito | `A3_unconfirmed_restore…`, `A3_external_change…` |
| **A4** | `nexus-recover` durante uma sessão ativa da interface (ou aplicação/restauração em outro processo) | Desfazer uma sessão viva ou escrever no meio de uma aplicação | **Coordenação compartilhada** (`FileRunCoordinator`): trava exclusiva do SO em `nexus.lock`, liberada pelo SO quando o processo morre. PID e início do processo ficam só no registro de diagnóstico. O executor só existe com a trava; `recover`/`revert` recusam com exit 5 (ativa) ou 6 (indeterminado); a recuperação nunca toca a execução vinculada à própria trava. Trava livre com registro de dono = execução abandonada | `CoordinationTests` (9): simultâneos no mesmo processo, outro processo segurando, processo morto (trava abandonada), recusa durante aplicação e durante restauração com o `nexus-recover` real, trava liberada, execução não vinculada, execução própria, `--journal` sem valor |
| **A5** | O diário falha depois de o Apply escrever (ex.: disco cheio) | Antes: o item virava "falhou" e o plano seguia aplicando com o diário em falha | Novo resultado `Interrupted`; a execução inteira para (os demais itens viram `Blocked`); o diário fica em `Applying` e a recuperação o encontra | `A5_journal_failure…` |
| **A6** | Ordem da recuperação por `UpdatedAt` | Marcar conflito muda `UpdatedAt` e inverte a ordem real; a restauração fica parcial | Ordem por execução (a mais nova primeiro) e sequência inversa | `A6_recovery_undoes_newest_run_first…` |
| **A7** | `SqliteJournal` usado por várias threads | Uma conexão SQLite não é thread-safe ("nested transactions") | Uma trava por instância em todos os métodos públicos | `A7_sqlite_journal_tolerates_concurrent_use…` |

**Prova de que os testes detectam os defeitos (teste de mutação):** para cada correção, a correção foi desfeita numa
cópia e o teste correspondente rodado: A1, A2, A3, A4 (CLI e execução própria), A5, A6, A7 e a sequência do diário
**falharam todos** sem a correção. No A7, uma mutação parcial (trava só em `BeginRun`) não foi detectada; sem nenhuma
trava, o teste falha nas 3 repetições com o erro real do SQLite.

## Revisão independente (Etapa 2)

Um agente que não escreveu o código revisou coordenação, diário, P/Invoke e interface. Não encontrou corrida que deixe
dois processos com a trava ao mesmo tempo, layout de P/Invoke errado, caminho de AccessViolation nem erro de XAML que
derrube a janela.

| # | Severidade | Achado | Tratamento | Teste |
|---|---|---|---|---|
| R1 | Alto | Trava aberta com escrita; ACL padrão de `%ProgramData%` impede que um usuário comum use a trava criada pelo broker (SYSTEM), e um usuário comum pode segurá-la indefinidamente | **Parcial.** Trava aberta só para leitura (a exclusão vem do compartilhamento). **Pendente:** instalador criar `%ProgramData%\NEXUS` com ACL explícita (SYSTEM/Administradores total; Usuários sem substituir `journal.db` nem o registro de dono). Pré-requisito do broker | Coordenação continua passando com acesso só leitura |
| R2 | Alto | `Revert` escrevia alvo e original lidos do diário sem `Assess` nem política de segurança; diário adulterado levaria um `recover` elevado a escrever em alvo proibido | `IRestoreValidator` nos 7 handlers que escrevem (forma do alvo, tipos, `SafetyPolicy`); o executor recusa antes de gravar `Restoring` | `Restore_refuses_a_journal_entry…`, `Service_restore_refuses…` (2), `Cfg_restore_refuses_path…` |
| R3 | Médio | Entrada antiga restaurada por cima de uma mudança mais nova do próprio NEXUS no mesmo alvo | Entrada "superada" não é restaurada: resultado `Conflict` com motivo, recuperação parcial. **Decisão sua abaixo** | `Older_entry_superseded…` |
| R4 | Médio/baixo | Registro de dono desatualizado mostrado como fato | Leitura com compartilhamento total; o dono só é exibido se PID **e** início do processo corresponderem a um processo vivo; nova tentativa ao trocar o registro | `Owner_record_that_does_not_match…` |
| R5 | Baixo/médio | Valor relido após o Apply (A2) pode ter sido escrito por um terceiro na mesma janela; e `--force` anterior seguido de exceção deixa `Restoring` autorizado a sobrescrever o valor externo sem novo `--force` | **Documentado, não alterado.** Janela inerente; ver decisões | — |
| R6 | Baixo | Exceção do diário no meio da recuperação perdia o relatório | Exceção capturada por item (`Failed`, "Erro inesperado"); a recuperação segue e nunca vira "completa" | `Recovery_records_unexpected_errors…` |
| R7 | Baixo | `--journal` sem valor caía no diário padrão | Erro (exit 2) sem tocar nada | `Cli_refuses_journal_option…` |
| R8–R10 | Baixo | Nulo possível na descoberta; exceções não tratadas na simulação; marcador "Xen" amplo | Corrigidos | (cobertos pelos testes existentes) |

Mutação das correções R2, R3, R4, R6 e R7: todas detectadas.

## Revisão independente do broker (B1–B15, 2026-10-30)

Segundo revisor independente, focado na fronteira de privilégio. Os achados B1 e B2 foram reproduzidos por ele
(entrada de diário forjada restaurada; resposta grande derrubando o servidor).

| # | Sev. | Achado | Tratamento | Teste |
|---|---|---|---|---|
| B1 | Alto | Diário adulterado fazia o broker restaurar um alvo fora do catálogo; `ControlSet001\Control\Lsa` escapava da lista de bloqueio; o arquivo de liberação podia ser criado por usuário comum (ACL padrão de `%ProgramData%`) | `CatalogBoundHandler`: toda restauração exige chave id@versão, handler e alvo **idênticos** ao que o catálogo do broker produz. `SafetyPolicy` normaliza `ControlSet00N` e `WOW6432Node`. O portão exige que o arquivo de liberação pertença a Administradores/SYSTEM, sem escrita para outras contas (`AdminOwnedFile`) | `Tampered_journal_entry_*` (2), `Safety_policy_normalizes_*` (4) |
| B2 | Alto | Resposta acima de 64 KiB (registro de dono gigante, muitos itens) derrubava o servidor | Textos limitados a 1 KiB, listas a 40 itens (o diário tem todos); registro de dono acima de 4 KiB é ignorado; qualquer exceção fica restrita à conexão | `Huge_owner_record_cannot_blow_up_responses` |
| B3 | Médio | Entre conexões o nome do pipe ficava livre (outro processo podia ocupá-lo e derrubar o servidor) | Sempre uma instância escutando: a próxima é criada antes de atender. Na partida, `FirstPipeInstance`: se o nome já existe, o broker **recusa subir**. **Pendente:** o cliente validar a identidade do servidor (SID), necessário antes de a interface usar o broker | `Server_refuses_to_start_when_the_pipe_name_is_already_taken` |
| B4 | Médio | Cliente parado monopolizava o broker e impedia o encerramento | Conexões atendidas em paralelo, prazo de 15 s por quadro, cancelamento repassado | `Idle_client_does_not_block_other_clients` |
| B5 | Médio | O host criava pasta e diário no PC físico mesmo com o portão fechado | Portão fechado: nada é criado; diário aberto só para leitura (ou vazio em memória) | (Windows; revisão de código) |
| B6 | Médio | `nexus-recover recover/revert` não passava pelo portão | Mesmo portão do broker (exit 7). Exceção explícita `--host-emergency`, impressa como aviso | `Cli_recover_is_blocked_by_the_gate_outside_a_test_vm` |
| B7 | Médio | Qualquer usuário local pode segurar `nexus.lock` indefinidamente | **Pendente** (instalação como serviço): trava do broker num objeto com ACL restrita | — |
| B8 | Médio | Conflito pendente bloqueava `Execute` para sempre (coação a usar `--force`) | Só operações interrompidas (estado incerto) bloqueiam; conflitos e entradas superadas são decisões pendentes. O comando "aceitar estado atual" continua sendo a decisão 2 abaixo | `Conflicts_do_not_block_new_executions_*` |
| B9 | Médio | O `.wsb` ficava na pasta gravável pelo Sandbox | `.wsb` em `vm-config\` (não mapeada); caminhos escapados no XML | (script) |
| B10 | Médio | Como LocalSystem, HKCU seria o perfil do SYSTEM | Host rodando como SYSTEM bloqueia escrita até resolver HKCU para o SID do cliente (**pendente**) | (Windows) |
| B11 | Baixo | JSON aceitava `"execute"`, `"Ping, Execute"`, chave duplicada, campos obrigatórios ausentes, ação nula | Conversor de enum exato, `AllowDuplicateProperties=false`, campos obrigatórios, recusa de nulos e campos longos | `Wire_decoder_is_exact_*` (6), `Null_or_oversized_*` |
| B12 | Baixo | Teste de VM criava pasta antes do portão; apagava o diário em caso de falha | Portão é o primeiro passo; diário preservado | (projeto de integração) |
| B13 | Baixo | `Inside-Sandbox` confiava em `$env:USERNAME` | Identidade real do processo + SMBIOS de VM antes de criar a liberação; dono e ACL definidos com `icacls` | (script) |
| B14 | Baixo | Esquema de energia congelado na partida do host | Relido a cada pedido | (Windows) |
| B15 | Baixo | `BeginRun` antes do plano deixava execução órfã se a leitura falhasse | Plano montado antes de abrir a execução | (coberto pelos testes do broker) |

**Mutação:** desfazendo B1 (chave e alvo, cada um), B1-normalização, B2, B3, B6, B8, B11 (enum, duplicatas, nulos), o
teste correspondente falhou. A primeira tentativa da mutação de alvo foi inválida, por precedência de operadores
(`false && A || B`), e foi refeita corretamente.

## Decisões que dependem de você

1. **A1 — restaurar ou preservar quando o processo morreu durante o Apply e o valor atual é o desejado.**
   Hoje: **restaura e marca como inferido**. A alternativa é marcar como conflito e exigir `--force`. Isso nunca
   sobrescreve uma escrita externa idêntica, mas deixa no sistema uma mudança que o NEXUS quase certamente fez
   (inclusive ajustes de sessão).
2. **R3 — o que fazer com entradas superadas.** Hoje ficam pendentes para sempre (recuperação parcial), e só
   `--force` as resolve, desfazendo a mudança mais nova. Falta um comando explícito "aceitar o estado atual e
   encerrar a entrada", com registro no diário. Proposta: `nexus-recover accept <entryId>`, sem escrever no sistema.
3. **R5 — validade de um `--force` entre invocações.** Hoje a confirmação dada numa invocação vale para a nova tentativa
   da mesma entrada em `Restoring`. A alternativa é exigir `--force` de novo, guardando no diário que o valor
   autorizado veio de um conflito confirmado.
4. ~~Quem segura a trava quando houver broker.~~ **Adotado (recomendação):** o broker segura a trava durante a
   execução e durante toda a sessão (até `EndSession`). Se o broker parar, a sessão vira "abandonada" e a próxima
   recuperação a reverte. A interface só sonda.
