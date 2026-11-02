# 08 — Empacotamento e distribuição (Windows)

Três formas de entregar o NEXUS, como o Kudu: **instalador .exe**, **ZIP portátil** e (ver abaixo) por que não há um
"EXE portátil único". Tudo é gerado por `tools/packaging/` e pelo workflow `.github/workflows/release.yml`.

| Pacote | Arquivo | Para quem |
|---|---|---|
| Instalador | `NEXUS-<versão>-win-x64-instalador.exe` | Recomendado. Instala em `%ProgramFiles%\NEXUS` (pede administrador). |
| ZIP portátil | `NEXUS-<versão>-win-x64-portatil.zip` | Rodar sem instalar (pen drive, PC de terceiros, teste rápido). |
| SHA-256 | `SHA256SUMS.txt` | Conferir o download: `Get-FileHash .\arquivo -Algorithm SHA256`. |

win-arm64 compila (`publish.py --rid win-arm64`, opção `arm64` no workflow manual), mas **não foi testado num PC ARM**;
não sai por padrão.

## 1. Publicação (pasta única)

`python tools/packaging/publish.py [--rid win-x64] [--rid win-arm64] [--version-suffix rc.1]`

- **Self-contained** (não precisa do .NET instalado), **ReadyToRun** (menos compilação na hora de abrir: a interface WPF
  abre mais rápido), **sem trimming** (WPF e reflexão não suportam; `PublishTrimmed=false` explícito no csproj da interface).
- Publica `nexus-broker` (aplicador), `nexus` (linha de comando), `nexus-task` (a mesma linha de comando sem console, para as
  tarefas agendadas não abrirem janela), `nexus-recover` e `Nexus.Desktop` e junta tudo em
  `artifacts/<rid>/NEXUS/`, com `catalog\`, `tools\service\` (serviço opcional), os scripts do instalador,
  `tools\PresentMon\PresentMon.exe` + `LICENSE.txt` (PresentMon 2.6.0 oficial da Intel, copiado pelo build da interface e da CLI;
  a publicação confere o SHA-256) e `THIRD-PARTY-NOTICES.txt`. O PresentMon é x64; no win-arm64 roda pela emulação do Windows
  (não testado num PC ARM).
- **Junção conferida:** arquivo com o mesmo nome precisa ser idêntico. A única exceção é assembly gerenciado listado nos
  `deps.json` dos dois lados, e aí vale a regra do host do .NET (maior `assemblyVersion`, depois maior `fileVersion`).
  Casos reais hoje: `WindowsBase.dll` (fachada 4.0 do runtime base × WindowsBase 10.0 do WPF: fica a do WPF),
  `System.Diagnostics.EventLog.dll` e `SQLitePCLRaw.provider.e_sqlite3.dll` (variantes net6.0 e net6.0-windows7.0 do mesmo
  pacote e versão; a mesma que o layout de desenvolvimento já usa). Qualquer outro conflito interrompe a publicação.
- **Estrutura conferida:** os cinco `.exe` lado a lado com a arquitetura PE certa e o subsistema certo (`nexus-task.exe` e
  `Nexus.Desktop.exe` = janela; `nexus.exe` e `nexus-recover.exe` = console); catálogo idêntico ao do repositório;
  todo `*.runtimeconfig.json` self-contained; runtime, WPF, SQLite nativo e ServiceController presentes.
  A interface e o `nexus` iniciam o aplicador por `LocalBroker.Executable(AppContext.BaseDirectory)` → `nexus-broker.exe` da
  mesma pasta; as tarefas agendadas rodam o `nexus-task.exe` da mesma pasta (`ScheduledScanHandler`, sem janela); o catálogo é lido de
  `<pasta>\catalog`.
- **Versão única:** `<VersionPrefix>` em `Directory.Build.props` (1.0.0) para todos os executáveis; a publicação passa o hash
  do commit (`InformationalVersion` = `1.0.0+<hash>`) e grava `VERSAO.txt` na pasta. Como sai cada tipo de build: seção 8.
- **Lock files:** a solução é restaurada com `--locked-mode` antes; publicar com `-r <RID>` acrescentaria seções por RID em
  todos os `packages.lock.json`, então a publicação roda numa cópia (`artifacts/src`) e confere que nos lock files da cópia
  só foram **acrescentadas** seções `<tfm>/<rid>` (os pacotes, versões e hashes são os mesmos já conferidos).
- Tamanho: ~140 MB a pasta, ~60 MB o ZIP (x64).

Conferido no Linux (contêiner, SDK 10.0.403, `EnableWindowsTargeting`): win-x64 e win-arm64 publicam, juntam e passam na
conferência; o ícone e a versão entram no `Nexus.Desktop.exe` mesmo compilando no Linux.

## 2. ZIP portátil

Extrair o ZIP inteiro e abrir `Nexus.Desktop.exe`. Nada é instalado; os dados ficam onde já ficam hoje:

| Dados | Onde |
|---|---|
| Configurações, relatórios, sessões de medição, log de erros, diário e quarentena das ações de usuário | `%LocalAppData%\NEXUS` |
| Diário e quarentena das ações de administrador | `%ProgramData%\NEXUS` (só administradores alteram) |

- **Apagar a pasta do ZIP não apaga os dados nem desfaz alterações.** Use "Desfazer" antes.
- **Tarefas agendadas:** a tarefa do Windows guarda o caminho completo do `nexus-task.exe`. Se a pasta mudar de lugar
  ou de nome, a tarefa para de funcionar (o NEXUS mostra "aponta para outra pasta ou versão"); basta ligar o agendamento de novo
  (ou `nexus schedule on`). O instalador não tem esse problema (pasta fixa).

### Por que o portátil é menos protegido no modo administrador

Ações de administrador iniciam `nexus-broker.exe` **elevado** (aviso do Windows) a partir da pasta do NEXUS. Numa pasta do
usuário (Downloads, Área de Trabalho, `D:\` com a ACL padrão, onde "Usuários autenticados" podem modificar), **qualquer
programa rodando com a conta do usuário** pode trocar `nexus-broker.exe`, uma dll ou o catálogo antes do clique em "Sim".
O aviso mostraria "NEXUS", mas rodaria o código que esse programa escolheu, como administrador. Em `%ProgramFiles%\NEXUS` só
administradores alteram os arquivos, então isso não acontece.

Mitigações:

1. **Instalador como caminho recomendado** para quem usa ações de administrador.
2. `tools\Test-NexusFolderSafety.ps1` (vai na pasta publicada; somente leitura): diz se a pasta, os arquivos dentro dela e as
   pastas acima estão protegidos (código 0) ou não (1, com o motivo). O instalador roda esse teste depois de instalar e
   avisa se o usuário escolheu uma pasta gravável por contas comuns. O workflow confere que a pasta instalada passa.
3. **Proposta para o código (não aplicada aqui; o aplicador é de outro agente nesta rodada):** antes de pedir a elevação,
   `LocalBroker.Start` recusa iniciar o aplicador elevado de uma pasta que contas comuns alteram, com mensagem clara
   ("use o instalador ou mova a pasta"). A checagem tem de ser no **cliente, antes** de elevar: depois de elevado, um binário
   trocado não se denunciaria. O serviço instalado continua funcionando (é tentado antes). Para desenvolvimento e o CI
   (a pasta `bin` é gravável), `NEXUS_ALLOW_WRITABLE_BROKER_DIR=1`; o `vm-integracao.yml` precisaria dessa variável no passo
   "Aplicador sob demanda". Diff completo, compilado com 0 avisos: `tools/packaging/proposto/LocalBroker-folder-check.patch`
   (`git apply` na raiz). Limite honesto: protege contra troca de arquivos na pasta; não protege contra um programa malicioso
   que já roda com a conta do usuário e abre o próprio aviso do Windows fingindo ser outra coisa.

## 3. Instalador (Inno Setup 6)

`tools/packaging/nexus.iss`, em português do Brasil (`BrazilianPortuguese.isl`, que acompanha o Inno Setup).

- Instala em `%ProgramFiles%\NEXUS` (`PrivilegesRequired=admin`) **com a ACL herdada de Arquivos de Programas**: só
  administradores, SYSTEM e TrustedInstaller alteram. Nenhuma permissão extra é dada a usuários.
- Atalho no Menu Iniciar (todos os usuários) e "NEXUS - avisos de terceiros" (abre `THIRD-PARTY-NOTICES.txt`); Área de
  Trabalho opcional (desmarcado).
- **PresentMon embutido** em `%ProgramFiles%\NEXUS\tools\PresentMon` (pasta protegida). O Medir confere hash/assinatura antes de
  executar (`docs/modules/measure.md`).
- **"Medir FPS sem pedir permissão toda vez"**: opção **marcada por padrão** (também vale em `/VERYSILENT`; para não aplicar,
  `/MERGETASKS="!perflog"`). Coloca a conta de quem abriu o instalador (o mesmo SID descoberto para o serviço, com `whoami /user`
  rodado como o usuário original) no grupo "Usuários do log de desempenho" (S-1-5-32-559) por
  `tools\Set-NexusPerfLogGroup.ps1 -Join` (Add-LocalGroupMember pelo SID; ADSI se o módulo faltar) e grava a marca
  `HKLM\SOFTWARE\NEXUS\PerfLogGroup\<SID> = "installer"`. Se a conta já era membro, nada muda e não há marca. Avisa que vale
  depois de sair e entrar de novo. Sem SID descoberto, não faz e explica (o botão da página Medir faz depois).
- **Serviço do aplicador**: opção desmarcada. Marcada, roda `tools\service\Install-Broker.ps1 -AllowSid <SID de quem abriu o
  instalador> -AllowHost` (a conta é descoberta com `whoami /user` rodado como o usuário original; se não der para saber,
  por exemplo elevação com a senha de outra conta, o serviço não é instalado e o instalador mostra o comando manual).
  Numa atualização, se o serviço já existir, ele é atualizado junto (precisa da mesma versão da interface).
  Observação: o `Install-Broker.ps1` copia o aplicador para `%ProgramFiles%\NEXUS\broker`; como a origem não pode ser a
  pasta-mãe do destino, o instalador extrai uma cópia temporária só para isso (o Inno guarda cada arquivo uma vez).
- **Desinstalador:**
  - pede para fechar o NEXUS se estiver aberto;
  - remove o serviço do aplicador (`Uninstall-Broker.ps1`, que preserva `%ProgramData%\NEXUS`) e as tarefas agendadas com a
    marca do NEXUS (`Remove-NexusTasks.ps1`; tarefa com o mesmo nome criada por outro programa não é tocada);
  - tira do grupo "Usuários do log de desempenho" **só as contas com a marca do NEXUS** (`Set-NexusPerfLogGroup.ps1 -Leave`):
    as que o instalador ou a ação `measure.perflog-group.join` do aplicativo adicionaram. Quem já era membro antes não tem marca
    e fica como estava;
  - **pergunta, com padrão "Não"**, se remove os dados (`%LocalAppData%\NEXUS` da conta que desinstala e
    `%ProgramData%\NEXUS`), explicando que sem o histórico o NEXUS não consegue mais desfazer o que alterou;
  - se `nexus-recover scan` indicar operações interrompidas, **não oferece** remover: avisa e mantém tudo;
  - a **quarentena** (arquivos que o NEXUS tirou do lugar; podem ser do usuário) tem **pergunta própria**, com o número de
    arquivos, ícone de alerta e padrão "Não". Respondendo "Não", a pasta `quarantine` fica (cada item tem `manifest.json` com
    o caminho original);
  - desinstalação silenciosa (`/VERYSILENT`) **nunca** apaga dados;
  - a exclusão **não segue links nem junções** (`SafeDelTree`): `%LocalAppData%\NEXUS` é gravável pelo usuário e o
    desinstalador roda como administrador; um link plantado ali é removido como link, o destino não é tocado.
- Ícone do instalador e do desinstalador = ícone do app.

Compilar à mão (Windows, Inno Setup 6 instalado):

```powershell
python tools\packaging\publish.py
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 /DNumericVersion=1.0.0.0 /DRid=win-x64 `
    /DSourceDir="$PWD\artifacts\win-x64\NEXUS" /DOutputDir="$PWD\artifacts" tools\packaging\nexus.iss
```

## 4. Workflow `release.yml`

Dispara em tag `v*` (ex.: `git tag v1.0.0 && git push origin v1.0.0`) e manualmente (Actions → release → Run workflow).

1. Versão: na tag, `v<X.Y.Z>[-sufixo]` precisa bater com `<VersionPrefix>`; manual = `<prefixo>-ci.<execução>`; manual com
   **stable** marcado = `<prefixo>` sem sufixo (seção 8).
2. `restore --locked-mode`, build Release, testes unitários.
3. `publish.py --no-zip` → (assinatura, se ligada) → `publish.py --zip-only`.
4. Inno Setup: usa o do runner se existir; senão `choco install innosetup` (o passo registra qual caminho usou e a versão).
5. **Teste na VM descartável:** instala em silêncio, confere arquivos, atalho, ACL (`Test-NexusFolderSafety.ps1` = 0),
   `nexus version`, `nexus-recover scan`, `nexus plan win.gamemode.enable` (inicia o aplicador da pasta instalada e só
   simula); **PresentMon**: SHA-256 e `Get-AuthenticodeSignature` (Valid, CN=Intel Corporation) do arquivo instalado,
   `nexus selftest --only Medir --with-benchmarks --json` rodado **da pasta instalada** ("Medir · PresentMon encontrado?" = OK
   com origem `embutido` e `conferido`; "PresentMon executa" não pode ser FALHA) e uma **captura real** de 5 s com o PresentMon
   instalado contra `Nexus.Desktop.exe` (aberto na hora) e `dwm.exe` (código 0 obrigatório; zero quadros vira aviso
   `::warning::`, documentado: VM sem GPU usa o adaptador básico e pode não apresentar quadros; os CSV ficam em `saida/`);
   **grupo**: a conta do runner virou membro (opção marcada) e a marca `installer` existe; abre a interface por 20 s,
   desinstala e confere que os dados ficaram, que não sobrou tarefa do NEXUS e que a conta saiu do grupo (se a marca era do
   instalador) ou continuou (se já era membro antes). Extrai o ZIP e roda o `nexus` de lá.
6. `SHA256SUMS.txt` de todos os `.zip`/`.exe`; upload como artifact; em tag (ou stable=true), **GitHub Release em rascunho**
   (o responsável revisa e publica; sufixo `-rc` vira pré-lançamento). O texto do Release é a seção da versão no
   `CHANGELOG.md` + downloads + aviso do SmartScreen + SHA-256 (`tools/packaging/release_notes.py --full`).
7. **Atualização automática** (docs/11-atualizacao.md): com o segredo `NEXUS_UPDATE_SIGNING_KEY`, gera e assina
   `nexus-update.json` e confere com `nexus update --verify-manifest` (chave embutida); com `NEXUS_RELEASES_TOKEN` e (tag ou
   publish=true), publica instalador, ZIP, manifesto e assinatura em `robinexe/nexus-releases` (público, *latest*). A VM
   também roda `nexus update --check` da pasta instalada (espera `Installed`) e do ZIP (espera `Portable`), tolerante.
   A publicação (`publish.py`) passa `-p:NexusPackaged=true`: sem essa marca o NEXUS é "versão de desenvolvimento" e não
   se atualiza.

Validado localmente com `actionlint` 1.7.7 (sem erros). O instalador só é compilado e testado no runner Windows.

## 5. Assinatura Authenticode (preparada, desligada)

Sem certificado de assinatura de código o Windows mostra "Editor desconhecido" no aviso do Windows e o SmartScreen pode
alertar no download. Para ligar:

1. Obter um certificado de assinatura de código (OV/EV) e exportar `.pfx`.
2. Segredos do repositório: `NEXUS_SIGN_PFX_BASE64` (o `.pfx` em base64) e `NEXUS_SIGN_PFX_PASSWORD`.
3. Variável do repositório `NEXUS_SIGN=true`.

O workflow então assina os binários do NEXUS (não os do runtime, já assinados pela Microsoft) antes de zipar, e o Inno
assina instalador e desinstalador (`SignTool=nexussign`, carimbo de tempo RFC 3161). Com a assinatura, o próximo passo é o
broker conferir a assinatura do cliente (pendência B3 do `tools/service/README.md`).

## 6. Ícone

Desenho próprio: fundo `#0E1320` com borda âmbar sutil e um traço âmbar `#FFB547` que forma um N como a linha de um gráfico,
terminando num ponto de medida. Fonte única `tools/packaging/icon/nexus.svg`; `python3 tools/packaging/icon/build_icon.py`
(rsvg-convert + Pillow) gera `src/Nexus.Desktop/Assets/nexus.ico` (16 a 256 px, cada tamanho rasterizado do SVG) e as
imagens pequenas do instalador. Os arquivos gerados são versionados; o build não depende de Python.

## 7. Por que não há "EXE portátil único"

O Kudu oferece um `.exe` portátil. No NEXUS, a interface **não escreve no sistema**: quem escreve é o aplicador, um processo
separado (`nexus-broker.exe`) iniciado da mesma pasta, e as tarefas agendadas precisam de um `nexus-task.exe` com caminho fixo. Um
executável único teria de se extrair numa pasta do usuário a cada execução, o que recria exatamente o problema da seção 2
(binários elevados em pasta gravável pelo usuário) e quebra o agendamento. O ZIP portátil cobre o caso de uso sem esconder
isso. Se for necessário no futuro: extrair para `%ProgramData%\NEXUS\app\<versão>` com ACL só de administradores exige
elevação na primeira execução, ou seja, vira um instalador.

## 8. Versões e como lançar uma

| Build | Versão | Como sai |
|---|---|---|
| Desenvolvimento (compilado no PC, `Abrir NEXUS.cmd dev`) | `1.0.0` sem marca de publicado | não se atualiza (docs/11-atualizacao.md) |
| CI (Actions → release → Run workflow) | `1.0.0-ci.<execução>` | artifacts da execução; com **publish**, vai para `nexus-releases` |
| Estável | `1.0.0` | tag `v1.0.0` **ou** Run workflow com **stable** marcado |
| Pré-lançamento | `1.0.0-rc.1` | tag `v1.0.0-rc.1` (Release marcado como pré-lançamento) |

A atualização automática compara por SemVer (`src/Nexus.Updates/SemVersion.cs`, testes em `UpdatesTests`):
`0.9.x < 1.0.0-ci.N < 1.0.0-rc.N < 1.0.0 < 1.0.1-ci.N < 1.0.1`. Um PC com uma `-ci` recebe a estável da mesma linha.

### Lançar a 1.0.0 (passo a passo)

1. Em `main`, com o CI verde: confira `<VersionPrefix>1.0.0</VersionPrefix>` em `Directory.Build.props` e a seção
   `## 1.0.0` do `CHANGELOG.md`. A linha que começa com `>` é o aviso de atualização dentro do NEXUS (curta); o resto da
   seção vai para a página do Release.
2. Dispare o build estável, de um destes jeitos:
   - `git tag v1.0.0 && git push origin v1.0.0` (publica em `nexus-releases` automaticamente, se os segredos existirem); ou
   - Actions → release → Run workflow → marque **stable** (e **publish**, para os NEXUS instalados receberem).
3. O workflow testa instalador e ZIP numa VM, gera `SHA256SUMS.txt`, cria o **rascunho** do Release `v1.0.0` em
   `robinexe/nexus` e (com publish/tag e os segredos de docs/11-atualizacao.md) publica em `robinexe/nexus-releases`.
4. Revise o rascunho (texto, arquivos, SHA-256) e clique em **Publish release**.
5. **Logo depois**, suba `<VersionPrefix>` para `1.0.1` e abra a seção `## 1.0.1` no `CHANGELOG.md`. Sem isso, os builds de CI
   seguintes sairiam `1.0.0-ci.N`, "mais velhos" que a estável, e ninguém os receberia.

Uma versão estável não pode ser refeita com o mesmo número (o PC que instalou a primeira não recebe a segunda). Corrigiu algo
depois de publicar? Lance `1.0.1`.
