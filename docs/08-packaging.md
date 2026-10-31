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
- Publica `nexus-broker` (aplicador), `nexus` (linha de comando), `nexus-recover` e `Nexus.Desktop` e junta tudo em
  `artifacts/<rid>/NEXUS/`, com `catalog\`, `tools\service\` (serviço opcional) e os scripts do instalador.
- **Junção conferida:** arquivo com o mesmo nome precisa ser idêntico. A única exceção é assembly gerenciado listado nos
  `deps.json` dos dois lados, e aí vale a regra do host do .NET (maior `assemblyVersion`, depois maior `fileVersion`).
  Casos reais hoje: `WindowsBase.dll` (fachada 4.0 do runtime base × WindowsBase 10.0 do WPF: fica a do WPF),
  `System.Diagnostics.EventLog.dll` e `SQLitePCLRaw.provider.e_sqlite3.dll` (variantes net6.0 e net6.0-windows7.0 do mesmo
  pacote e versão; a mesma que o layout de desenvolvimento já usa). Qualquer outro conflito interrompe a publicação.
- **Estrutura conferida:** os quatro `.exe` lado a lado com a arquitetura PE certa; catálogo idêntico ao do repositório;
  todo `*.runtimeconfig.json` self-contained; runtime, WPF, SQLite nativo e ServiceController presentes.
  A interface e o `nexus` iniciam o aplicador por `LocalBroker.Executable(AppContext.BaseDirectory)` → `nexus-broker.exe` da
  mesma pasta; a tarefa semanal roda o `nexus.exe` da mesma pasta (`ScheduledScanHandler`); o catálogo é lido de
  `<pasta>\catalog`.
- **Versão única:** `<VersionPrefix>` em `Directory.Build.props` (0.9.0) para todos os executáveis; a publicação passa o hash
  do commit (`InformationalVersion` = `0.9.0+<hash>`) e grava `VERSAO.txt` na pasta.
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
- **Verificação semanal agendada:** a tarefa do Windows guarda o caminho completo do `nexus.exe`. Se a pasta mudar de lugar
  ou de nome, a tarefa para de funcionar (o NEXUS mostra "aponta para outro nexus.exe"); basta ligar o agendamento de novo
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
- Atalho no Menu Iniciar (todos os usuários); Área de Trabalho opcional (desmarcado).
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
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.9.0 /DNumericVersion=0.9.0.0 /DRid=win-x64 `
    /DSourceDir="$PWD\artifacts\win-x64\NEXUS" /DOutputDir="$PWD\artifacts" tools\packaging\nexus.iss
```

## 4. Workflow `release.yml`

Dispara em tag `v*` (ex.: `git tag v0.9.0 && git push origin v0.9.0`) e manualmente (Actions → release → Run workflow).

1. Versão: na tag, `v<X.Y.Z>[-sufixo]` precisa bater com `<VersionPrefix>`; manual = `<prefixo>-ci.<execução>`.
2. `restore --locked-mode`, build Release, testes unitários.
3. `publish.py --no-zip` → (assinatura, se ligada) → `publish.py --zip-only`.
4. Inno Setup: usa o do runner se existir; senão `choco install innosetup` (o passo registra qual caminho usou e a versão).
5. **Teste na VM descartável:** instala em silêncio, confere arquivos, atalho, ACL (`Test-NexusFolderSafety.ps1` = 0),
   `nexus version`, `nexus-recover scan`, `nexus plan win.gamemode.enable` (inicia o aplicador da pasta instalada e só
   simula), abre a interface por 20 s, desinstala e confere que os dados ficaram e que não sobrou tarefa do NEXUS. Extrai o
   ZIP e roda o `nexus` de lá.
6. `SHA256SUMS.txt` de todos os `.zip`/`.exe`; upload como artifact; em tag, **GitHub Release em rascunho** (o responsável
   revisa e publica; sufixo `-rc` vira pré-lançamento).

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
separado (`nexus-broker.exe`) iniciado da mesma pasta, e a tarefa semanal precisa de um `nexus.exe` com caminho fixo. Um
executável único teria de se extrair numa pasta do usuário a cada execução, o que recria exatamente o problema da seção 2
(binários elevados em pasta gravável pelo usuário) e quebra o agendamento. O ZIP portátil cobre o caso de uso sem esconder
isso. Se for necessário no futuro: extrair para `%ProgramData%\NEXUS\app\<versão>` com ACL só de administradores exige
elevação na primeira execução, ou seja, vira um instalador.
