# Módulo Programas (`src/Nexus.Programs`, página "Programas e atualizações")

Grupo **Limpar**. Três abas: **Programas** (inventário + desinstalar), **Atualizações** (winget/Chocolatey/Scoop/npm) e
**Sobras** (o que o desinstalador deixou para trás). Referência de concorrência: Kudu (Program Uninstaller + Software Updater).

## O que faz

| Parte | Como | Escreve no sistema? |
|---|---|---|
| Inventário | Lê `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (visões 64 e 32 bits) e o mesmo caminho no HKCU. Nome, editor, versão, tamanho (`EstimatedSize`, em KB), data (`InstallDate`), pasta, `UninstallString`/`QuietUninstallString`. Some como em "Aplicativos instalados": `SystemComponent=1`, `ParentKeyName`, `ReleaseType` de atualização, chaves `KB…`, sem nome ou sem desinstalador; duplicatas entre visões viram uma. Busca por nome/editor, ordem por nome, tamanho, data ou editor. | Não |
| Apps da Store (opcional) | `Get-AppxPackage` (cmdlet de leitura) em PowerShell oculto, sem frameworks e sem pacotes de sistema; script fixo, saída JSON. Remoção: abre Configurações > Aplicativos. | Não |
| Desinstalar | Abre o desinstalador **oficial** do fornecedor, visível (`UseShellExecute`, aviso do Windows quando ele pede), depois de `Host.Confirm`. | Quem escreve é o fornecedor (exceção controlada do brief) |
| Sobras | Varredura só leitura + remoção pelo aplicador (handler `ProgramLeftover`, reversível). | Sim, via broker |
| Atualizações | Detecta (existência + versão) e lista atualizações em processos ocultos; atualizar abre uma janela **visível** do PowerShell rodando o próprio gerenciador, depois de `Host.Confirm` com a lista. | Quem escreve é o gerenciador (exceção controlada) |

### Desinstalador (`UninstallCommandParser`)

- `MsiExec.exe /I{GUID}` ou `/X{GUID}` → `%SystemRoot%\System32\msiexec.exe /x {GUID}` (sempre remover; `/I` abriria "reparar").
  GUID validado; demais opções descartadas.
- Caminho entre aspas, com ou sem argumentos; caminho **sem** aspas com espaços: escolhe o primeiro prefixo terminado em
  `.exe` que existe — nunca um prefixo sem extensão (a falha clássica `C:\Program.exe`); variáveis de ambiente; nomes 8.3;
  `rundll32 x.dll,Função` (resolvido em System32).
- Recusa: interpretadores (`cmd`, `powershell`, `pwsh`, `wscript`, `cscript`, `mshta`, `wsl`...), não-`.exe`, caminho relativo,
  quebra de linha, arquivo inexistente. Nunca `cmd /c` com texto concatenado: executável e argumentos vão separados para o
  `ShellExecute`. O modo silencioso (`QuietUninstallString`) é mostrado mas nunca usado.
- Depois que o processo termina (muitos desinstaladores se relançam de `%TEMP%`; por isso também há o botão manual), a
  página relê a lista. Se o programa sumiu, procura sobras automaticamente; se não, avisa.
- Ponto de restauração: o Desktop não tem API para criar um. O texto da confirmação explica que as mudanças de sistema do
  NEXUS criam ponto automático e que aqui quem age é o fornecedor; sugere criar um antes ("Criar um ponto de restauração").

### Sobras (`LeftoverScanner` + `LeftoverPolicy`)

Procura em Program Files (64/32), ProgramData, AppData Roaming/Local/LocalLow e `LocalAppData\Programs`:

| Achado | Pontuação |
|---|---|
| `InstallLocation` registrada pelo próprio programa, ainda existente | 95 |
| Pasta onde ficava o desinstalador | 90 |
| `Editor\Programa` (ou a pasta do editor quando só tem este programa / está vazia) | 85 / 60 |
| Pasta com o nome do programa (sem versão, arquitetura, parênteses; também sem o nome do editor na frente) | 80 |
| Chave `Software\Editor\Programa` (HKCU/HKLM, também `WOW6432Node`) | 80 |
| Chave `Software\Programa` | 70 |
| Chave do editor sem subchaves | 50 |
| Nome que **contém** o do programa ("FooEditorUpdater") | 40 |

Alta ≥ 75 (vem marcada), média 50–74, baixa < 50 (desmarcada). Exclusões fortes, as mesmas na varredura e no handler:
pastas/chaves do Windows, Microsoft, Common Files, WindowsApps, Package Cache, drivers (NVIDIA/AMD/Intel/Realtek...),
"Programs" sozinho, a raiz; pastas de editores com vários produtos (Steam, Epic, Google, Adobe, Mozilla...) só por subpasta;
nada que contenha ou esteja dentro da pasta (InstallLocation ou pasta do desinstalador) de um programa **ainda instalado**;
nenhum item com o nome de um programa ou editor ainda instalado (a pasta "Mozilla" com o Thunderbird instalado); profundidade
máxima `Raiz\Editor\Programa\…`; links/junções nunca são seguidos (o alvo e as pastas entre ele e a raiz são conferidos).
Só procura sobras de um programa que **já saiu** da lista de instalados.

### Handler `ProgramLeftover` (aplicador)

Catálogo `catalog/programs.v1.json`: 16 vagas `programs.leftover.user.NN` (privilégio User, `scope=HKCU`) e 16
`programs.leftover.machine.NN` (Admin, `scope=HKLM` → aviso do Windows + ponto de restauração automático). Vagas porque o
broker recusa a mesma ação repetida num pedido. Alvo `{kind, scope, path}`; `kind` (`file|registry`) e `path` são
`userParameters` com regex estrita (caminho `X:\…` sem `..`, sem barra final, sem caracteres proibidos/controle, até 8 níveis;
ou `Software\…` com 1–4 níveis). Como serviço, o broker acrescenta o `userSid` (escopo HKCU) e o handler usa `HKU\<SID>` e a
pasta do perfil pelo `ProfileList`; com outra conta e sem SID, recusa.

- **Revalida tudo** (Assess, Apply e CheckRestore): forma do alvo, raízes do escopo certo, `LeftoverPolicy`, `SafetyPolicy`,
  programas instalados (lidos de novo no aplicador), pasta de dados do NEXUS, links; HKLM exige aplicador elevado.
- **Pasta/arquivo** → `DataDirectory\quarantine\programs\<id>\payload` + `manifest.json` (gravado ANTES de mover: lista de
  arquivos com tamanho e SHA-256; recusa links dentro, mais de 20 mil arquivos ou 4 GB). Mesmo volume: renomear (atômico; arquivo
  em uso → o Windows recusa e nada muda). Outro volume: copia, confere tamanho + hash de cada arquivo, só então apaga a origem.
- **Chave** → árvore completa exportada para o manifesto (valores com tipo: String, ExpandString, DWord, QWord, Binary,
  MultiString, None; valor padrão incluído; tipos que não sabemos recriar exatamente fazem a remoção ser recusada), depois
  `DeleteSubKeyTree` e releitura.
- **Estados**: `present` (no lugar), `quarantined` (fora do lugar e guardado), ausente. Desejado: `quarantined`.
- **Restore**: devolve ao caminho original; se algo novo ocupou o lugar, restaura ao lado com " (restaurado pelo NEXUS)" (nunca
  sobrescreve). Chave: recria chaves e valores com os tipos exatos. Antes, o CheckRestore confere que o conteúdo da quarentena
  é exatamente o do manifesto (nenhum arquivo a mais, a menos ou trocado). Sobras da máquina: pasta e manifesto com dono
  Administradores e ACL protegida (`QuarantineAcl`), e `AdminOwnedFile.Check` no manifesto — um usuário comum não consegue
  trocar o que voltaria para Program Files com privilégio de administrador.

### Atualizações

| Gerenciador | Detecção | Lista | Atualizar (janela visível) |
|---|---|---|---|
| winget | `%LocalAppData%\Microsoft\WindowsApps\winget.exe` ou PATH; `--version` | `winget upgrade --include-unknown --disable-interactivity` (tabela) | `winget upgrade --id <ID> --exact --source <origem>` por pacote |
| Chocolatey | `%ChocolateyInstall%`, `%ProgramData%\chocolatey\bin`, PATH | `choco outdated -r` (`nome\|atual\|disponível\|fixado`) | `choco upgrade <ids>` com `runas` (aviso do Windows) |
| Scoop | `%SCOOP%\shims\scoop.ps1`, `~\scoop\shims`, PATH | `scoop status` (tabela nova e formato antigo `app: 1.0 -> 2.0`) | `scoop update <apps>`, nunca elevado |
| npm | `npm.cmd` no PATH ou `Program Files\nodejs` (lê com `node.exe npm-cli.js` quando existe, sem cmd) | `npm outdated -g --json` (código 1 é normal) | `npm install -g <pkg>@latest`, nunca elevado |

- **winget não tem saída JSON para `upgrade`/`list`** (pesquisado em 31/10/2026: issue microsoft/winget-cli#1753 fechada
  apontando para o módulo PowerShell Microsoft.WinGet.Client e a API COM; `winget export` só lista IDs instalados, sem versão
  disponível). Por isso o parser primário é a tabela, recortada pela **largura de exibição** (caracteres asiáticos ocupam 2),
  independente do idioma (colunas pela posição: Nome/Name, ID, Versão/Version, Disponível/Available, Origem/Source), ignorando
  barras de progresso (`\r`) e cores ANSI, e marcando a segunda tabela ("require explicit targeting"). Se a tabela não puder
  ser lida, tenta `Get-WinGetPackage` (módulo oficial, se instalado) com projeção JSON.
- Nome truncado com "…" é só exibição; **ID truncado** não pode ser pedido (marcado, não selecionável). Pacote fixado (pinned)
  também não. IDs passam por regex estrita por gerenciador antes de entrar no script; o script vai por `-EncodedCommand`, com
  cada ID entre aspas simples, e o caminho do gerenciador não pode ter `'`, `` ` ``, `$` ou quebra de linha.
- Se o winget pedir os termos de uma origem, o NEXUS **não aceita por você**: avisa para rodar `winget upgrade` uma vez.
- "Atualizar tudo" mostra a lista completa na confirmação e pede cada ID (nunca `--all`).

## Fontes / evidências

- Chaves Uninstall e seus valores: <https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key>.
- Pastas conhecidas (Program Files, ProgramData, AppData): <https://learn.microsoft.com/windows/win32/shell/knownfolderid>.
- `msiexec /x`: <https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec>.
- winget upgrade: <https://learn.microsoft.com/windows/package-manager/winget/upgrade>; falta de saída estruturada:
  <https://github.com/microsoft/winget-cli/issues/1753>.
- Limpadores de registro (não aceleram; sem suporte da Microsoft): <https://support.microsoft.com/help/2563254>.

## Riscos

- Desinstalar e atualizar são feitos pelo fornecedor e **não podem ser desfeitos pelo NEXUS** (dito na confirmação).
- Uma sobra pode guardar dados que você quer (saves, configurações): só as de confiança alta vêm marcadas; tudo é reversível.
- Remover sobras libera espaço e organiza; **não deixa o PC mais rápido** (catálogo: `programs.registry-leftovers.speedup`
  como Ineffective).

## Como desfazer

Botão **Desfazer** na aba Sobras (reverte as execuções desta sessão), ou a página Aplicar / `nexus-recover revert`. A quarentena
fica em `%LocalAppData%\NEXUS\quarantine\programs` (perfil) ou `%ProgramData%\NEXUS\quarantine\programs` (máquina).

## O que NÃO faz, e por quê

- Desinstalação silenciosa ou "forçada" (apagar pasta + chave Uninstall de um programa ainda registrado): quebra reparo,
  atualização e pode deixar serviços/drivers órfãos (catálogo: `rejected`).
- Remover apps da Store (`Remove-AppxPackage`): seria escrita fora do aplicador; abre Configurações.
- Limpar a pasta do editor quando outro produto dele está instalado; tocar em serviços, drivers, tarefas agendadas ou
  entradas de inicialização deixadas por desinstaladores (são de outros módulos).
- Aceitar termos de origem do winget ou rodar Chocolatey/Scoop elevado sem você ver.
- Ícones dos programas (exigiria System.Drawing/WinForms no Desktop); a lista mostra nome, editor e tamanho.
- Esvaziar a quarentena: hoje ela só cresce; apagar de vez fica para uma ação futura com confirmação própria.

## Testes

`tests/Nexus.Tests/Modules/ProgramsTests.cs` (Unit, 100 casos): UninstallString (MSI /I e /X, aspas, sem aspas, 8.3,
variáveis, rundll32, recusas), filtros do inventário com fonte falsa, Store JSON, regras e pontuação de sobras com árvore de
pastas temporária e registro em memória (inclusive links), handler Detect/Apply/Restore (pasta, arquivo, outro volume, chave
com todos os tipos, restaurar ausente, quarentena adulterada, ACL da máquina), motor completo com diário, catálogo (ids,
handlers registrados, regex dos parâmetros, reconstrução pelo `CatalogBoundHandler`), parsers dos 4 gerenciadores com saídas
realistas (inglês e português, nomes e IDs truncados com "…", largura asiática, tabela explícita, formatos antigo/novo do
Scoop) e o script de atualização. `tests/Nexus.IntegrationTests/Modules/ProgramsIntegrationTests.cs` (VmIntegration): pasta em
AppData + chave HKCU com vários tipos, e (elevado) ProgramData + HKLM, pelo broker real, com reversão.
