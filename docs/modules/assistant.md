# Assistente completo

Pedido do responsável: "uma função automática: o cliente inicia e o NEXUS faz passo a passo com ele em todas as funções do
app, para o PC sair totalmente otimizado, verificando tudo e ajustando tudo; no final dá o relatório para ele".

Página **Assistente completo** (grupo Automático, a primeira dele) + biblioteca `src/Nexus.Assistant` (net10.0).

## O que faz

Treze etapas. Em cada uma a tela mostra: o que verifica, por que importa, o que encontrou (números grandes, lidos agora no
PC), o que recomenda e uma ação principal. Botões: **Aplicar recomendado**, **Escolher** (abre a lista com as marcações),
**Continuar**/**Seguir sem aplicar**, **Pular etapa**, **Voltar**, **Parar e continuar depois**, **Desfazer tudo que o
assistente fez**. A barra de etapas mostra feitas (verde), atual (âmbar) e puladas (riscadas); dá para voltar a uma etapa
já vista.

| # | Etapa | Lê (biblioteca do módulo) | Propõe (ação do catálogo) | Leva o usuário a |
|---|---|---|---|---|
| 1 | Antes de começar | `PcTraitsProbe` (tomada/bateria), `DriveInfo` (espaço), diário só leitura (alterações pela metade) | `system.restore.create-point@1` (marcado) | Aplicar › Recuperar, se houver pendência (aí não propõe nada) |
| 2 | Seu PC | descoberta + `MemoryLabAnalyzer`/`MemoryDiagnosis` (canais, XMP, capacidade), `GpuLabScanner`/`GpuDiagnostics` (taxa do monitor, idade do driver, PCIe, Modo de Jogo, HAGS, GPU por jogo); medição curta opcional (`FrequencyProbe`, `MemoryBenchmark` com 3 passadas) | `gpulab.display.mode@1`, `win.gamemode.enable@1`, `gpu.hags.lab@1` (opcional), `gpulab.app.gpu-preference@1` | Laboratórios; ações do usuário: 2º módulo de RAM, XMP no BIOS, driver de vídeo no site oficial |
| 3 | Limpeza segura | `SafeCleaningSelector` (mesmas regras do "Otimizar agora" e da limpeza agendada) + `CleaningScanner` (bytes por regra) + `CleaningOverview` | regras seguras → quarentena (com lote) | Limpeza |
| 4 | Programas | `ProgramInventory` (+ atualizações sob demanda pelo `PackageManagers`) | nada (nunca desinstala) | Programas, com os candidatos **já marcados** (o usuário confere e desinstala lá) |
| 5 | Inicialização e serviços | `StartupInventory` + `WindowsBootEventReader` (impacto medido pelo Windows), `ServicePresets` | desligar do boot os itens de terceiros de impacto alto (mesma regra de `StartupPage.ToggleRequest`); presets de serviços como opcionais | Inicialização, Serviços |
| 6 | Privacidade | `PrivacyScanner` + `PrivacyPlanner.MissingFor(Recommended)` | ações do perfil Recomendado | Privacidade |
| 7 | Segurança | `WindowsSecurityReader` (placar, ameaças, pontos suspeitos) | nada; verificação rápida do Defender opcional (MpCmdRun, depois de `Host.Confirm`) | Segurança (revisar suspeitos) |
| 8 | Drivers | `DriverUpdatesReader` (GPU local; Windows Update sob demanda) | `driverupdates.install@1` dos recomendados (desmarcados) | Atualizar drivers; site oficial da GPU |
| 9 | Memória | `WindowsDoctorReader` + `MemoryDoctorEngine` (maiores consumidores, compressão, paginação) | só soluções **permanentes**, reversíveis, `FixKind.Apply`, sem proteção de processo | Memória: diagnóstico |
| 10 | Desempenho | `RecommendationEngine` (Automático) | `OneClickPlanner.Candidates` (risco baixo, marcados) + recomendações sem parâmetro do usuário (desmarcadas se risco médio) | Otimização automática |
| 11 | Medir de verdade | página Medir (PresentMon encontrado? medição automática?) | nada no Windows; liga a **medição automática** e o **mostrador ao abrir jogo** (preferências do NEXUS, desfeitas pelo "Desfazer tudo") | Medir |
| 12 | Manter em dia | `AutomationHandlers.ReadTaskStatus` | `automation.schedule.scan@1` semanal (domingo 19:00, fora da bateria e sem jogo; marcado), `automation.schedule.clean@1` (sábado 19:30; opcional, desmarcado) | Ajustes |
| 13 | Relatório final | relê espaço, limpeza, inicialização, privacidade e segurança para o "depois" | — | Abrir / Exportar o HTML |

## Segurança (como cada regra do brief é cumprida)

- **Toda escrita pelo aplicador**: a biblioteca não escreve no sistema; a página implementa `IAssistantWriter` com
  `Host.ApplyAsync`/`Host.RevertAsync` (confirmação, diário, releitura, desfazer; administrador = aviso do Windows + ponto de
  restauração automático). `DesktopReadOnlyTests` e `XamlBindingTests` continuam verdes.
- **Planejar antes**: cada etapa monta as mudanças pelo catálogo (`AssistantCatalog.Change`: nível, risco, experimental,
  irreversível e "dá para desfazer" vêm **do catálogo**, nunca do chamador) e confere com `Host.PlanAsync` (aplicador do usuário,
  sem escrever): fica o que mudaria e a ação de administrador que só o aplicador elevado simula; "já está assim"/"não se aplica"
  saem com o motivo.
- **Agrupado por etapa e nível**: `ApplyGrouping` faz no máximo dois pedidos por etapa (sua conta primeiro, administrador depois:
  um aviso do Windows), cada um com até 64 ações e uma confirmação que lista o que muda, antes → depois.
- **Seleção padrão**: marcado só risco baixo, documentado, reversível e não opcional. Risco médio, experimental e opt-in ficam
  desmarcados e vão para "ficou de fora e por quê" com o motivo.
- **Nada irreversível**: ação com `irreversible: true` ou pedido com `ConfirmIrreversible` nunca entra (filtro na fábrica e de
  novo no agrupamento). O assistente não esvazia quarentena, não esvazia Lixeira, não faz exclusão segura, não desinstala.
- **Recusas do aplicador**: não param a etapa; cada uma fica registrada com o motivo. Cancelar uma confirmação encerra a etapa
  (o que já foi aplicado fica e pode ser desfeito).
- **Desfazer tudo**: cada execução que mudou algo desfazível vira um `RunRecord`; `AssistantRunner.UndoAllAsync` reverte da
  última para a primeira (ponto de restauração não entra: não tem o que desfazer). As preferências do NEXUS mudadas na etapa 11
  voltam ao valor anterior.
- **Sem dados pessoais no relatório**: `ReportRedactor` troca caminho do perfil por `%USERPROFILE%`, nome do usuário, nome do
  computador, e-mails e IPs (versões de driver como 31.0.15.3623 não são confundidas com IP). O perfil do PC só tem hardware e
  Windows. Links só `https://` vindos das fontes dos módulos.
- **Honestidade**: limpeza não acelera (diz que o espaço volta ao esvaziar a quarentena), "limpar a RAM" é mito, privacidade não
  acelera, presets de serviços dependem do uso, "pouco usado" não é medido pelo Windows (a lista usa tamanho e data).

## Estado (parar e retomar)

`%LocalAppData%\NEXUS\assistente\estado.json` (gravação atômica). Guarda etapa atual, situação de cada etapa, execuções (para
desfazer), mudanças e resultado, o que ficou de fora, ações do usuário, números antes/depois e preferências mudadas. Estado
ilegível, de versão desconhecida ou com etapa inexistente: começa do zero e guarda a cópia como `estado.json.corrompido-<data>`.
Depois de concluído o estado fica salvo (é ele que o "Desfazer tudo" usa); "Começar de novo" avisa antes de substituí-lo.

## Relatório

Resumo na página (mudanças aplicadas, o que ficou de fora, ações do usuário, números, próximo passo) e HTML autocontido em
`%LocalAppData%\NEXUS\relatorios\assistente-<data>.html` (botões Abrir e Exportar). Seções: números antes → depois, próximo
passo, perfil do PC, o que só você pode fazer, o que foi verificado, o que foi alterado (antes → depois, nível, risco, se
desfaz), o que ficou de fora e por quê. Tema escuro na tela; `@media print` troca para fundo branco e evita quebrar cartões.
XHTML bem formado (teste faz parse como XML), sem script.

## O que NÃO faz (e por quê)

- Não desinstala, não apaga de verdade, não usa ações irreversíveis (contrato de `docs/modules/disk.md`).
- Não instala driver de vídeo de site de fabricante nem baixa nada: só abre a página oficial, depois de confirmar.
- Não remove pontos suspeitos de segurança: leva à página Segurança, onde cada correção tem a própria confirmação.
- Não aplica soluções temporárias de memória (fechar/encerrar processos, esvaziar lista de espera).
- O "depois" do tempo de boot só existe no próximo boot (o relatório diz isso).
- `Host.RevertAsync` confirma cada execução: "Desfazer tudo" mostra uma confirmação geral e depois a do aplicador para cada
  execução (veja "Pendências").

## Pendências

- `IModuleHost` sem "reverter várias execuções com uma confirmação": hoje "Desfazer tudo" pede uma confirmação por execução.
  Sugestão: `Task<BrokerCallResult?> RevertManyAsync(IReadOnlyList<(Guid Run, BrokerLevel Level)> runs, string confirmation)`.
- A seleção em massa da página Programas é preenchida marcando `ProgramRow.IsChecked` na coleção pública `Programs`; um método
  público `ProgramsPage.Preselect(ids)` deixaria isso explícito.
- Visual fino (ilustrações, cartões) vem com o design system v2; a página usa só o contrato de `Controls.xaml`.

## Testes

`tests/Nexus.Tests/Modules/AssistantTests.cs` (Unit): máquina de etapas (avançar, voltar, pular, ir a uma etapa, concluir),
estado (salvar/retomar, cinco formas de arquivo corrompido), seleção padrão (baixo marcado, médio/experimental/opcional
desmarcados, irreversível e ação desconhecida recusados, inclusive todas as irreversíveis do catálogo real), agrupamento
(usuário antes de administrador, limite de 64, filtro de irreversível), aplicador falso (resultado por mudança, recusa não para o
próximo lote, cancelar para a etapa, execução só efêmera fora do desfazer, desfazer tudo em ordem inversa e parando ao cancelar),
relatório sintético (XHTML válido, seções, escape, sem nome/computador/perfil/e-mail/IP, link só https), planejadores (ponto de
restauração, agendas, candidatos de programas, filtro do `PlanAsync` com e sem aplicador) e presença no catálogo real de toda
ação usada.

## Como validar no Windows

1. `dotnet build Nexus.slnx -c Release` e abrir o NEXUS; no menu, Automático › Assistente completo (primeira do grupo).
2. Começar; na etapa 1 conferir energia, espaço e "Alterações pela metade" (0). "Aplicar recomendado" deve pedir **uma**
   confirmação e o aviso do Windows (ponto de restauração).
3. Etapa 2: "Medir CPU e RAM"; no PC do responsável deve aparecer "single channel" e a ação "Comprar um 2º módulo de RAM".
4. Etapa 3: o total recuperável deve bater com a página Limpeza (regras marcadas por padrão, só da conta). Aplicar e conferir a
   quarentena.
5. Etapa 4: "Revisar na página Programas" abre a página com os candidatos marcados (sem desinstalar).
6. Parar no meio, fechar o NEXUS, abrir de novo: "Retomar de onde parei" volta à mesma etapa.
7. No fim, "Abrir relatório" (conferir que não há nome do PC/usuário) e imprimir em PDF para ver o modo de impressão.
8. "Desfazer tudo que o assistente fez" e conferir na página Histórico que as execuções foram revertidas (da última para a primeira).
