# 09 — Autoteste no PC real

Os testes unitários rodam em Linux com backends falsos e a VM do CI cobre as escritas. Mas alguns defeitos só existem
num PC de verdade: no Windows do responsável apareceram um binding XAML para propriedade só leitura (pilha de janelas de
erro), `netsh` em UTF‑8 sem recuo e `ipconfig /displaydns` exigindo administrador. O autoteste roda **no PC do usuário**,
**sem alterar nada**, e entrega um relatório legível por pessoas (HTML) e por máquinas (JSON).

## Como rodar (dois cliques)

```
tools\selftest\Rodar-Autoteste.cmd
```

O script acha o NEXUS (repositório compilado, compilando antes se o .NET SDK existir; senão a pasta do próprio script,
`%ProgramFiles%\NEXUS` ou `%LocalAppData%\Programs\NEXUS`), roda os dois autotestes e abre o relatório.
Tudo fica em `%LocalAppData%\NEXUS\autoteste\<aaaammdd-hhmmss>\`:

| Arquivo | O que é |
|---|---|
| `relatorio.html` | Relatório para pessoas: verificações dos módulos + telas da interface (com imagens) |
| `relatorio.json` | `nexus selftest` em JSON (`nexus.selftest/1`) |
| `relatorio.txt` | A saída do terminal |
| `ui.json` | Autoteste da interface (`--selftest-ui`) |
| `telas\NN-pagina.png` | Imagem de cada página |

Opções do script: `admin` (roda como administrador; o Windows pergunta), `sem-interface` (só as leituras dos módulos),
`--with-benchmarks`, `--with-windows-update`. Exemplo: `Rodar-Autoteste.cmd admin --with-benchmarks`.
Sem administrador é o padrão: algumas leituras voltam como **AVISO** com o motivo ("precisa de administrador"), que é
exatamente o que o usuário comum vê na interface.

## 1. `nexus selftest` (leituras de todos os módulos)

```
nexus selftest [--json ARQ] [--html ARQ] [--ui ui.json] [--with-windows-update] [--with-benchmarks]
               [--timeout S] [--max-files N] [--only Módulo,Módulo] [--quiet]
```

Cada verificação chama **as mesmas leituras que a página faz**, direto na biblioteca do módulo
(`src/Nexus.SelfTest/NexusChecks.cs`):

| Módulo | Verificações |
|---|---|
| Descoberta, Diagnóstico | `Discovery.Capture`, `DiagnosticRules.Evaluate` |
| Catálogo | `CatalogLoader.LoadDirectory` (catalog/*.json); **Detect** de cada ação instanciável com os handlers embrulhados em `SimulationOnlyHandler` (Apply/Restore lançam) |
| Limpeza | Varredura completa (`CleaningScanner`, regras embutidas + do usuário), Lixeira |
| Disco | Unidades; análise da pasta do próprio NEXUS (pequena) |
| Programas | Inventário + regra do lote; apps da Store; gerenciadores de pacote e lista de atualizações (cada ferramenta com teto de 90 s) |
| Inicialização, Serviços | Inventário, eventos de desempenho do boot, serviços com editor (Authenticode), predefinições |
| Privacidade | `PrivacyScanner` + permissões de app |
| Segurança | Defender/Central de Segurança/heurísticas (`WindowsSecurityReader`), inspetor de registro |
| Verificar arquivos | Bases de regras e hashes; verificação rápida **limitada** a N arquivos (padrão 300) |
| Monitor | 3 amostras dos contadores PDH (`MonitorSession.Tick`) |
| Ferramentas do sistema | DNS/ARP, perfis de Wi‑Fi (só a contagem), drivers (pnputil), Appx, pontos de restauração |
| Atualizar drivers | Dispositivos, placas de vídeo e reinício pendente; busca no Windows Update só com `--with-windows-update` |
| Laboratórios | CPU (CPUID, topologia, energia); GPU (DXGI, PCIe, NVML, monitores); memória (SMBIOS, estado); medições curtas só com `--with-benchmarks` |
| Memória: diagnóstico | Uma amostra (`WindowsDoctorReader.Read`) |
| Medir | Bibliotecas de jogos; PresentMon encontrado? |
| Automático | Perfil + recomendações + candidatas do "Otimizar agora"; tarefas agendadas do NEXUS |
| Ajustes | `settings.json` (validação própria: o `NexusSettings.Load` copia arquivo corrompido, o autoteste não) |

**Resultado de cada verificação**

- **OK**: leu sem problemas.
- **AVISO**: a biblioteca devolveu leitura parcial com motivo conhecido (precisa de administrador, sem Wi‑Fi, PresentMon
  ausente, contador indisponível, Detect que falhou em uma ação…). O motivo aparece no relatório.
- **FALHA**: exceção não tratada (com tipo, mensagem e pilha) ou **tempo limite** excedido (padrão 60 s; cada verificação
  pesada pede mais). Uma leitura presa é abandonada e as escritas atrasadas dela no relatório são ignoradas.
- **IGNORADO**: não é Windows, ou opção não pedida (`--with-benchmarks`, `--with-windows-update`).

Código de saída: **0** sem FALHA (AVISO não conta), **1** com alguma FALHA (ou com FALHA no `--ui` incluído), **2** uso
inválido. Fora do Windows, as verificações do Windows saem como "IGNORADO: não é Windows" (o CI em Linux roda as portáveis).

**Dados pessoais**: as verificações anotam só contagens, sim/não e textos curtos técnicos (modelo da CPU/GPU, perfil do
PC). Nunca nomes de redes, programas, serviços de terceiros ou caminhos. Mensagens de erro e pilhas passam pelo
`Sanitizer`: perfil do usuário → `%USERPROFILE%`, `X:\Users\<nome>` → `<usuário>`, nome do usuário e do computador
(palavra inteira, 3+ letras), e‑mails.

**Nada escreve no sistema**: `Nexus.SelfTest` só monta texto. O teste `SelfTest_assembly_never_calls_a_write_api` lê o
assembly compilado e proíbe referências a escrita de arquivo/pasta (`File.Write*`, `Delete`, `Move`, `FileStream`…),
registro (`SetValue`, `CreateSubKey`, `Delete*`), `Process.Start`, o motor (`ActionExecutor`, `IActionHandler.Apply/Restore`,
`SqliteJournal`, `BrokerSession`…) e as APIs de escrita dos módulos (quarentena, exclusão segura, `Save` de preferências).
Quem grava os arquivos de relatório pedidos é o `nexus.exe`. Limite honesto: a inspeção vê chamadas **diretas** do
assembly; as leituras chamadas nos módulos são leituras por contrato (revisadas nos testes de cada módulo). Efeitos
colaterais conhecidos de ferramentas de terceiros: o winget pode atualizar o próprio cache de fontes ao listar atualizações.

## 2. `Nexus.Desktop.exe --selftest-ui ARQ.json [--no-screenshots]`

Abre a janela de verdade e:

1. espera a leitura do PC (até 4 min);
2. abre **cada página de `MainViewModel.Pages`**, na ordem (página nova entra sozinha, sem mudar o autoteste);
3. espera `OnShownAsync` terminar e a página sair de "ocupado" (`Busy`, `Scanning`, `Loading`, `Measuring`…) e a fila
   da interface esvaziar (até 45 s por página), faz `UpdateLayout` e fotografa (`RenderTargetBitmap` → `telas\NN-*.png`);
4. registra por página: exceções não tratadas (DispatcherUnhandledException, AppDomain, tarefas não observadas, falha do
   `OnShownAsync`), **erros de binding** (`PresentationTraceSources.DataBindingSource` com um `TraceListener` que conta
   por página), maior **parada da interface** (relógio próprio, como o `UiWatchdog`), tempo de carga, se existe
   `DataTemplate` para a página, a mensagem da página e a barra de status;
5. grava o JSON e fecha (código 0 sem FALHA; 1 com FALHA).

Classificação de página: **FALHA** = exceção, sem DataTemplate ou interface parada ≥ 10 s; **AVISO** = erro de binding,
parada ≥ 2 s ou não ficou ociosa no tempo limite.

**Modo autoteste** (`MainViewModel.SelfTestMode`): `ApplyAsync`, `RevertAsync` e `PlanAsync` são recusados sem iniciar o
aplicador (contados em `refusedWrites`); `Confirm` responde "não" sem janela; o erro inesperado é registrado em vez de
abrir `MessageBox`; não usa a instância única (não atrapalha um NEXUS aberto). As páginas fazem o que fariam se o usuário
as abrisse (ex.: Medir apaga capturas temporárias antigas do próprio NEXUS e passa a observar processos; o Monitor liga
as amostras). Feche jogos antes de rodar: com um jogo aberto e a medição automática ligada, Medir iniciaria o PresentMon.
As imagens mostram o que a janela mostra (nomes de programas, redes etc.): ficam só no PC; o usuário decide se compartilha.

## 3. CI (`vm-integracao.yml`)

- **Autoteste no PC (nexus selftest)**: logo depois dos testes unitários, antes de qualquer escrita na VM;
  `saida/selftest.json` e `.html` vão nos artefatos.
- **Autoteste da interface**: os runners `windows-latest` do GitHub rodam numa sessão interativa (com área de trabalho),
  então a janela WPF abre de verdade. Se o processo estiver na sessão 0 (sem área de trabalho), o passo registra o motivo
  e não conta como falha. Resultado em `saida/ui/` (JSON, telas e `relatorio.html`).
- **Ponta a ponta** (`Category=E2E`, antes de instalar o serviço): assistente completo com o aplicador real, retomada,
  relatório e "Desfazer tudo" conferido; atualizar drivers (Windows Update real, backup/desfazer com driver real, dispositivos
  sem driver). Ver `docs/modules/assistant.md` e `docs/modules/driverupdates.md`. Saída em `saida/e2e/`.
- **Matriz**: o job inteiro roda em `windows-2022` e `windows-2025`, em paralelo; artifacts `vm-integracao-windows-2022` e
  `vm-integracao-windows-2025`.
- **Veredito dos autotestes** (no fim, `if: always()`): falha o job se `nexus selftest`, a interface ou os testes de ponta a
  ponta tiverem FALHA. Fica no fim para uma falha não esconder os testes de integração e do serviço.

Atualizar drivers no autoteste agora também conta **dispositivos**, se o **código de problema foi lido**, **sem driver**,
**com driver que não carrega** e **desativados** (mesma leitura do cartão "Dispositivos sem driver").

## 4. Ler o resultado

1. Abra `relatorio.html`: falhas primeiro, com a exceção e a pilha em "exceção e pilha".
2. Para um defeito da interface, veja a tela correspondente em "Telas" e os erros de binding da página.
3. Para enviar a quem corrige: `relatorio.json` + `ui.json` bastam (sem imagens, sem dados pessoais). As imagens só se
   precisar e depois de olhar o que aparece nelas.
