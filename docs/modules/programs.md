# Módulo Programas (`src/Nexus.Programs`, página "Programas e atualizações")

Grupo **Limpar**. Três abas: **Programas** (inventário + desinstalar), **Atualizações** (winget/Chocolatey/Scoop/npm) e
**Sobras** (o que o desinstalador deixou para trás). Referência de concorrência: Kudu (Program Uninstaller + Software Updater).

## O que faz

| Parte | Como | Escreve no sistema? |
|---|---|---|
| Inventário | Lê `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (visões 64 e 32 bits) e o mesmo caminho no HKCU. Nome, editor, versão, tamanho (`EstimatedSize`, em KB), data (`InstallDate`), pasta, `UninstallString`/`QuietUninstallString`. Some como em "Aplicativos instalados": `SystemComponent=1`, `ParentKeyName`, `ReleaseType` de atualização, chaves `KB…`, sem nome ou sem desinstalador; duplicatas entre visões viram uma. Busca por nome/editor, ordem por nome, tamanho, data ou editor. | Não |
| Apps da Store (opcional) | `Get-AppxPackage` (cmdlet de leitura) em PowerShell oculto, sem frameworks e sem pacotes de sistema; script fixo, saída JSON. Remoção: abre Configurações > Aplicativos. | Não |
| Desinstalar | Abre o desinstalador **oficial** do fornecedor, visível (`UseShellExecute`, aviso do Windows quando ele pede), depois de `Host.Confirm`. | Quem escreve é o fornecedor (exceção controlada do brief) |
| Desinstalação em massa | Caixa de seleção por linha, fila sequencial com progresso, modo silencioso só quando o fornecedor declarou (ou MSI com barra), ponto de restauração antes pelo aplicador, resumo e sobras de todos. | Fornecedor (desinstaladores) + aplicador (ponto de restauração, sobras) |
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
  `ShellExecute`. O botão "Desinstalar" de um programa sempre abre a janela do fornecedor; o modo silencioso só existe na
  desinstalação em massa (abaixo).
- Depois que o processo termina (muitos desinstaladores se relançam de `%TEMP%`; por isso também há o botão manual), a
  página relê a lista. Se o programa sumiu, procura sobras automaticamente; se não, avisa.
- Ponto de restauração (um programa): o texto da confirmação sugere criar um antes (página Ferramentas do sistema). Na
  desinstalação em massa o NEXUS pede o ponto pelo aplicador antes do lote (abaixo).

### Desinstalação em massa (`BulkUninstallPolicy`, `BulkQueueBuilder`, `BulkUninstaller`)

**Seleção.** Cada linha da aba Programas tem uma caixa; "Selecionar visíveis" marca os da busca atual, "Limpar seleção"
desmarca tudo; o contador mostra "N selecionado(s) · X GB" (tamanho declarado em `EstimatedSize`). A coluna "Lote" e a dica
da caixa dizem o motivo de cada exceção:

| Regra | Efeito |
|---|---|
| App da Store | **Fora** (Configurações > Aplicativos) |
| O próprio NEXUS (nome `NEXUS…` com editor NEXUS/robinexe/ausente, ou pasta/desinstalador dentro da pasta do NEXUS) | **Fora** |
| `NoRemove=1` | **Fora** |
| Componentes de atualização do Windows (Update Health Tools, PC Health Check, `Update for…`) | **Fora** |
| Desinstalador que o `UninstallCommandParser` recusa (interpretador, sem caminho, inexistente) | **Fora**, com a mensagem do validador |
| Visual C++ Redistributable, .NET (Runtime/Desktop/ASP.NET Core/Framework), DirectX/XNA/GameInput/Vulkan/OpenAL, Edge/WebView2, drivers de vídeo (NVIDIA driver/PhysX, AMD Software, Intel Graphics), chipset/placa (AMD Chipset, Intel ME/Serial IO/RST/Wi-Fi/Bluetooth, `Windows Driver Package`), áudio/rede/touchpad (Realtek, Synaptics, ELAN) | **Atenção**: marcável só um a um (não entra em "selecionar visíveis"), vai por último na fila e pede uma confirmação extra que lista cada um com o motivo |

As regras são por nome no registro: conservadoras, não uma garantia (a confirmação diz isso). "NVIDIA App"/"GeForce
Experience" não são driver e entram normalmente.

**Fila.** Antes de montar, a página relê o inventário (nunca roda um `UninstallString` velho). Ordem: os de janela primeiro
(você está olhando), depois os silenciosos, e os de "atenção" por último (os outros podem depender deles); por nome dentro de
cada grupo. Modo por item (opção "Modo silencioso quando o fornecedor declarar", ligada por padrão):

- **MSI** → `%SystemRoot%\System32\msiexec.exe /x {GUID} /qb- /norestart`: interface básica (só barra de progresso, sem caixas
  modais) e sem reiniciar sozinho; `/qb` e não `/qn` porque a interface básica ainda mostra o **aviso de administrador** para
  produtos instalados para todos os usuários (com `/qn` a remoção falharia sem você ver). Um `QuietUninstallString` MSI também é
  normalizado para isso (opções do registro como `REBOOT=Force` são descartadas).
- **Silencioso do fornecedor** → o `QuietUninstallString` gravado pelo próprio instalador (é o que o winget usa; "Aplicativos
  instalados" não usa), passado pelo **mesmo** validador do desinstalador normal e aceito só se aponta para o mesmo executável
  ou a mesma pasta do `UninstallString`. Senão, cai para a janela do fornecedor com uma nota.
- **Janela do fornecedor** → todo o resto. O NEXUS **não inventa** opções (`/S`, `/VERYSILENT`): cada instalador entende uma coisa.

**Confirmações** (nesta ordem): (1) só se houver itens de "atenção", a lista deles com o motivo; (2) se "Criar ponto de
restauração antes" estiver marcado (padrão) e a Proteção do Sistema estiver desligada (registro do SPP, sem unidade protegida)
ou bloqueada por política, avisa e só segue sem ponto se você aceitar; (3) a confirmação do lote: cada item com nome,
tamanho, editor, tipo de instalador e modo, o total, o que ficou de fora e por quê, o aviso de que cada desinstalador é do
fornecedor e **o NEXUS não desfaz uma desinstalação**; (4) o ponto de restauração em si: `Host.ApplyAsync` com a ação
`system.restore.create-point@1` do módulo Ferramentas do sistema (Admin → aviso do Windows; o aplicador também cria o seu ponto
automático; se o Windows já criou um nas últimas 24 h, ele vale). Se o ponto falhar, pergunta se continua sem ele. São janelas
separadas porque `Host.ApplyAsync` sempre confirma por conta própria.

**Execução** (`BulkUninstaller`, um por vez — nunca em paralelo: o Windows Installer só aceita uma instalação por vez, erro
1618): abre o desinstalador com `UninstallLauncher.Start` (ShellExecute), espera o processo, espera 2 s e relê as chaves
Uninstall.

| Situação | Resultado |
|---|---|
| Entrada sumiu | Removido (qualquer código) |
| Saiu com 0 / sem código, entrada continua | "Esperando o desinstalador terminar": muitos se copiam para `%TEMP%` e continuam em outra janela; relê a cada 3 s até 10 min |
| 3010 / 1641 | Removido + "reiniciar" |
| 1618 (outra instalação em andamento) | Volta para o fim da fila e tenta de novo depois de 45 s (até 2 vezes); depois, falha com o motivo |
| 1602 / 1223 | Pulado ("cancelado na janela do desinstalador") |
| Outro código, entrada continua | Falha, com o código e a explicação (1603, 1605, 1612, 1619…) |
| Aviso do Windows recusado | Pulado |
| Processo passou do tempo (60 min com janela, 30 min silencioso) ou espera dos filhos esgotada | Pulado, "o desinstalador não foi fechado" |

"Pular este" para de esperar o atual e segue; "Parar depois do atual" deixa o atual terminar e marca o resto como pulado.
**O NEXUS nunca fecha nem mata um desinstalador.** A página mostra o atual, a barra, "Na fila · Removidos · Falhas · Pulados"
e o estado de cada item. Reinício pendente: além de 3010/1641, compara antes/depois do lote as marcas do Windows
(`Component Based Servicing\RebootPending`, `WindowsUpdate\Auto Update\RebootRequired`, `PendingFileRenameOperations`); uma
marca que já existia antes não é atribuída ao lote.

**Depois.** Relê a lista, mostra o resumo (removidos, falhas com código/motivo, pulados, espaço liberado **estimado** pelo
`EstimatedSize` dos removidos, quantos sem tamanho, aviso de reinício) e oferece "Procurar as sobras dos N removidos": a aba
Sobras varre todos de uma vez (achados repetidos entre programas viram um; coluna "Programa"), e "Mover escolhidas para a
quarentena" usa o lote do núcleo — uma confirmação e um desfazer por escopo (perfil / todos os usuários); acima de 64 sobras
num escopo, vai em partes de 64 (limite do aplicador), cada parte com sua confirmação.

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

Catálogo `catalog/programs.v1.json` (2.1.0): `programs.leftover.user` (privilégio User, `scope=HKCU`) e
`programs.leftover.machine` (Admin, `scope=HKLM` → aviso do Windows + ponto de restauração automático), cada uma repetida
no mesmo pedido, uma instância por sobra (lote do núcleo v2; até 64 por escopo). As antigas 16+16 vagas
`programs.leftover.{user|machine}.NN@1` ficam em `"supersedes"`: não podem mais ser pedidas, mas execuções feitas com elas
continuam desfazendo e recuperando (o broker as valida contra a ação nova). Alvo `{kind, scope, path}`; `kind` (`file|registry`) e `path` são
`userParameters` com regex estrita (caminho `X:\…` sem `..`, sem barra final, sem caracteres proibidos/controle, até 8 níveis;
ou `Software\…` com 1–4 níveis). Como serviço, o broker acrescenta o `userSid` (escopo HKCU) e o handler usa `HKU\<SID>` e a
pasta do perfil pelo `ProfileList`; com outra conta e sem SID, recusa.

- **Revalida tudo** (Assess, Apply e CheckRestore): forma do alvo, raízes do escopo certo, `LeftoverPolicy`, `SafetyPolicy`,
  programas instalados (lidos de novo no aplicador), pasta de dados do NEXUS, links; HKLM exige aplicador elevado.
- **Pasta/arquivo** → `DataDirectory\quarantine\programs\<id>\payload` + `manifest.json` (gravado ANTES de mover: lista de
  arquivos com tamanho e SHA-256; recusa links dentro, mais de 20 mil arquivos ou 4 GB). Mesmo volume: renomear por handle
  com `SafeFileMover` (pasta-mãe aberta sem seguir junção e presa contra renomeação, caminho final conferido, rename relativo
  ao handle — fecha a corrida "conferiu, trocaram por junção, moveu"; arquivo em uso → recusa e nada muda). Outro volume:
  copia, confere tamanho + hash de cada arquivo, só então apaga a origem.
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
- `msiexec /x`, `/qb-`, `/norestart`: <https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec>;
  opções de linha de comando do Windows Installer: <https://learn.microsoft.com/windows/win32/msi/command-line-options>.
- Códigos de saída do Windows Installer (1602, 1603, 1605, 1618, 1641, 3010): <https://learn.microsoft.com/windows/win32/msi/error-codes>.
- `QuietUninstallString` como campo do instalador usado pelo winget: <https://learn.microsoft.com/windows/package-manager/package/manifest>.
- Ponto de restauração: ação `system.restore.create-point` (`docs/modules/system.md`, `catalog/system.v1.json`).
- winget upgrade: <https://learn.microsoft.com/windows/package-manager/winget/upgrade>; falta de saída estruturada:
  <https://github.com/microsoft/winget-cli/issues/1753>.
- Limpadores de registro (não aceleram; sem suporte da Microsoft): <https://support.microsoft.com/help/2563254>.

## Riscos

- Desinstalar e atualizar são feitos pelo fornecedor e **não podem ser desfeitos pelo NEXUS** (dito na confirmação).
- Em massa: um item de "atenção" removido pode fazer jogos/programas pararem de abrir ou um dispositivo parar — por isso
  confirmação extra, marcação um a um e ponto de restauração antes. Um silencioso declarado pelo fornecedor roda sem perguntas
  (é o que você confirmou na lista); se o fornecedor gravou um comando errado, o programa pode não sair (vira falha/pulado).
- Uma sobra pode guardar dados que você quer (saves, configurações): só as de confiança alta vêm marcadas; tudo é reversível.
- Remover sobras libera espaço e organiza; **não deixa o PC mais rápido** (catálogo: `programs.registry-leftovers.speedup`
  como Ineffective).

## Como desfazer

Botão **Desfazer** na aba Sobras (reverte as execuções desta sessão), ou a página Aplicar / `nexus-recover revert`. A quarentena
fica em `%LocalAppData%\NEXUS\quarantine\programs` (perfil) ou `%ProgramData%\NEXUS\quarantine\programs` (máquina).

## O que NÃO faz, e por quê

- Desinstalação "forçada" (apagar pasta + chave Uninstall de um programa ainda registrado): quebra reparo, atualização e pode
  deixar serviços/drivers órfãos (catálogo: `rejected`). Inventar opções silenciosas ou usar `/qn` (catálogo:
  `programs.uninstall.silent`, `rejected`): só o silencioso declarado pelo fornecedor ou MSI `/qb-`, depois da confirmação.
- Desinstalar em paralelo, fechar/matar um desinstalador travado, ou reiniciar o PC por você.
- Saber que programa depende de qual runtime: a lista de "atenção" é por nome, conservadora.
- Remover apps da Store (`Remove-AppxPackage`): seria escrita fora do aplicador; abre Configurações.
- Limpar a pasta do editor quando outro produto dele está instalado; tocar em serviços, drivers, tarefas agendadas ou
  entradas de inicialização deixadas por desinstaladores (são de outros módulos).
- Aceitar termos de origem do winget ou rodar Chocolatey/Scoop elevado sem você ver.
- Ícones dos programas (exigiria System.Drawing/WinForms no Desktop); a lista mostra nome, editor e tamanho.
- Esvaziar a quarentena: hoje ela só cresce; apagar de vez fica para uma ação futura com confirmação própria.

## Testes

`tests/Nexus.Tests/Modules/ProgramsBulkTests.cs` (Unit, 72 casos): regras do lote por categoria (runtimes, drivers, NEXUS,
Store, NoRemove, componentes do Windows, desinstalador inválido, nomes parecidos), montagem da fila (ordem, MSI `/qb-`,
`QuietUninstallString` com/sem aspas, mesma pasta, outro executável, recusado, MSI normalizado, sem opções inventadas,
duplicatas), executor com processo/registro/relógio falsos (sucesso em ordem e um por vez, falha com código, código ≠ 0 com
entrada sumida, 1618 → fim da fila e nova tentativa, desistência após 2, 3010/1641, marca de reinício do Windows antes/depois,
tempo esgotado → pulado sem fechar, filho esperado pelo registro, espera de filho esgotada, 1602/1223, aviso do Windows
recusado, parar depois do atual, pular o atual, erro de leitura nunca vira "removido") e resumo.
`tests/Nexus.Tests/Modules/ProgramsTests.cs` (Unit, 100 casos): UninstallString (MSI /I e /X, aspas, sem aspas, 8.3,
variáveis, rundll32, recusas), filtros do inventário com fonte falsa, Store JSON, regras e pontuação de sobras com árvore de
pastas temporária e registro em memória (inclusive links), handler Detect/Apply/Restore (pasta, arquivo, outro volume, chave
com todos os tipos, restaurar ausente, quarentena adulterada, ACL da máquina), motor completo com diário, catálogo (ids,
handlers registrados, regex dos parâmetros, reconstrução pelo `CatalogBoundHandler`), parsers dos 4 gerenciadores com saídas
realistas (inglês e português, nomes e IDs truncados com "…", largura asiática, tabela explícita, formatos antigo/novo do
Scoop) e o script de atualização. `tests/Nexus.IntegrationTests/Modules/ProgramsIntegrationTests.cs` (VmIntegration): pasta em
AppData + chave HKCU com vários tipos, e (elevado) ProgramData + HKLM, pelo broker real, com reversão.
