# 11 — Atualização do programa

Pedido do responsável (01/11): *"colocar opção de atualização da versão do programa, atualizar sozinho quando tiver versão
nova disponível"*. Esta é a única exceção à regra "sem downloads automáticos": o NEXUS baixa **só o próprio instalador**,
assinado, do GitHub.

## Como funciona para o usuário

- **Ajustes → Atualizações**: versão atual, "Procurar atualização agora", "Atualizar automaticamente" (ligado por padrão,
  gravado em `settings.json`), última procura, situação e progresso ("Baixando 42 %", "baixado e conferido", "pronto").
- **Automático ligado**: procura 30 s depois de abrir e a cada 6 h. Achou versão nova: baixa em segundo plano, confere e
  mostra uma faixa na janela: **"NEXUS x.y.z pronto — Reiniciar e atualizar / Depois"**.
  - *Reiniciar e atualizar*: fecha o NEXUS, roda o instalador em silêncio (o Windows pede permissão **uma vez**, porque o
    NEXUS fica em Arquivos de Programas) e o instalador reabre o NEXUS novo, na conta do usuário (não como administrador).
  - *Depois*: instala quando o usuário fechar o NEXUS (sem reabrir). Se o aviso do Windows for recusado, não insiste a cada
    saída para a mesma versão (`updateExitAttempted` em `settings.json`); a faixa volta na próxima vez que abrir.
  - Nunca instala com uma operação em andamento (aplicar, Assistente, leitura: `AppUpdates.IsAppBusy`).
  - Falhou ou recusou o aviso: o NEXUS reabre com "A atualização não foi concluída" (nada mudou).
- **Cópia portátil (ZIP)**: só avisa, com o botão "Abrir downloads" (página pública de Releases). O instalador iria para
  outra pasta. Portátil = não há o desinstalador registrado apontando para a pasta do NEXUS (`UpdateInstallation.Classify`).
- **Versão de desenvolvimento** (compilada no PC, sem `-p:NexusPackaged=true`) e **autoteste da interface**: desligado.

## Onde está o código

| O quê | Arquivo |
|---|---|
| Versões SemVer (0.9.0-ci.7 < 0.9.0 < 0.9.1) | `src/Nexus.Updates/SemVersion.cs` |
| Manifesto (`nexus.update/1`) e validação | `src/Nexus.Updates/UpdateManifest.cs` |
| Chave embutida, hosts permitidos, limites, assinatura | `src/Nexus.Updates/UpdatePolicy.cs` |
| Procurar e baixar (redirecionamentos conferidos, SHA-256) | `src/Nexus.Updates/UpdateClient.cs` |
| Versão do app, tipo de cópia, linha do instalador | `src/Nexus.Updates/UpdateInstallation.cs` |
| Estado, temporizador, faixa, instalar ao sair | `src/Nexus.Desktop/ViewModels/AppUpdates.cs` |
| Tela | `Views/Pages/Settings.xaml` (seção Atualizações) e `MainWindow.xaml` (faixa) |
| CLI | `nexus update --check` / `nexus update --verify-manifest <arquivo>` (`src/Nexus.Cli/UpdateCommand.cs`) |
| Gerar e assinar o manifesto | `tools/packaging/sign-update.py` |
| Reabrir depois de instalar | `tools/packaging/nexus.iss` (`[Run]` com `runasoriginaluser` e `Check: RelaunchRequested`) |
| Testes | `tests/Nexus.Tests/Modules/UpdatesTests.cs` (todas as regras, com par de chaves de teste) |

## Confiança: o que é conferido (qualquer falha = nada roda)

1. **Fonte**: `https://github.com/robinexe/nexus-releases/releases/latest/download/nexus-update.json` (+ `.sig`). O
   repositório do código é privado e os assets de Release privados não baixam sem login; por isso os pacotes vão para um
   repositório **público só de Releases**. `NEXUS_UPDATE_FEED` (variável de ambiente) troca o endereço para testes; a
   assinatura continua obrigatória.
2. **Assinatura**: `nexus-update.json.sig` = base64 de uma assinatura **ECDSA P-256 / SHA-256 em DER** (RFC 3279, o formato
   do `openssl dgst -sha256 -sign`) sobre os **bytes exatos** do manifesto, conferida com a chave pública **embutida** no
   NEXUS (`UpdatePolicy.TrustedPublicKeyPem`). Sem assinatura ou com assinatura de outra chave: ignorado.
3. **Manifesto**: `schema` = `nexus.update/1`; `channel` = `stable`; `version` SemVer **estritamente maior** que a atual
   (uma versão `ci` recebe também a estável seguinte); `installer.url` só `https` em `github.com`,
   `objects.githubusercontent.com` ou `release-assets.githubusercontent.com` (porta padrão, sem usuário/senha);
   `sha256` com 64 hex; `size` de 1 byte a 300 MB; `minimumVersion` opcional (abaixo dela, só avisa).
4. **Download**: cada redirecionamento passa pela mesma lista de hosts **antes** de ser pedido; `Content-Length` e o total
   recebido têm de ser iguais ao `size`; SHA-256 calculado durante o download. Grava em
   `%LocalAppData%\NEXUS\updates\<versão>\<arquivo>.partial` e só renomeia depois de conferir; o que falha é apagado.
   Downloads de outras versões são removidos.
5. **Antes de executar**: tamanho e SHA-256 conferidos de novo. Instalador: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART
   /CLOSEAPPLICATIONS [/RELAUNCH=1] /LOG=...` (log em `updates\<versão>\instalacao.log`). Sem verbo "runas": o Inno se eleva
   sozinho e mantém a parte sem elevação, que reabre o NEXUS como o usuário original.
6. **Futuro**: conferir a assinatura Authenticode do instalador quando houver certificado (hoje desligada; ver
   `docs/08-packaging.md`).

Formato do manifesto:

```json
{
  "schema": "nexus.update/1",
  "version": "0.9.1",
  "channel": "stable",
  "publishedAt": "2026-11-01T12:00:00Z",
  "notes": "Correções e melhorias.",
  "minimumVersion": "0.9.0",
  "installer": {
    "url": "https://github.com/robinexe/nexus-releases/releases/download/v0.9.1/NEXUS-0.9.1-win-x64-instalador.exe",
    "sha256": "<64 hex>",
    "size": 123456789
  }
}
```

Privacidade: nenhuma conta, nenhum identificador. A cada procura o NEXUS faz dois GETs ao GitHub (manifesto e assinatura),
com o cabeçalho `User-Agent: NEXUS-Updater/<versão>`.

## Publicar uma versão

O `release.yml` faz tudo quando os dois segredos existem (Settings → Secrets and variables → Actions do repositório
`robinexe/nexus`):

| Segredo | O que é |
|---|---|
| `NEXUS_UPDATE_SIGNING_KEY` | Chave **privada** EC P-256 em PEM PKCS#8 (`-----BEGIN PRIVATE KEY-----`), par da chave pública embutida. |
| `NEXUS_RELEASES_TOKEN` | Token fine-grained com **Contents: Read and write** só no repositório `robinexe/nexus-releases`. |

1. Actions → release → Run workflow (marque **publish**; opcional: **notas**), ou envie uma tag `v0.9.1`.
2. O CI compila, testa, gera instalador e ZIP, testa os dois numa VM, gera `nexus-update.json`, assina
   (`sign-update.py`, com o openssl do Git), confere com `nexus update --verify-manifest` (código e chave **do aplicativo
   publicado**: segredo errado = falha) e cria o Release `v<versão>` em `robinexe/nexus-releases` marcado como *latest*,
   com instalador, ZIP, `SHA256SUMS.txt`, `nexus-update.json` e `.sig`.
3. Sem `NEXUS_UPDATE_SIGNING_KEY`: o manifesto não é gerado (aviso). Sem `NEXUS_RELEASES_TOKEN` ou sem `publish`: nada é
   publicado (aviso). Os pacotes continuam nos artifacts da execução.

Versões: a manual sai como `1.0.0-ci.<execução>`; a tag `v1.0.0` (ou Run workflow com **stable**) sai como `1.0.0`. Depois de
publicar a `1.0.0` estável, suba `VersionPrefix` (Directory.Build.props) para `1.0.1`, senão as `1.0.0-ci.N` seguintes ficam
"mais velhas" que a estável e ninguém as recebe. Passo a passo: `docs/08-packaging.md`, seção 8. O texto do aviso (`notes` do
manifesto) é o campo **notas** do Run workflow ou, vazio, a linha `>` da seção da versão no `CHANGELOG.md`.

Para conferir um manifesto à mão: `nexus update --verify-manifest nexus-update.json` (procura `nexus-update.json.sig` ao lado).

## Preparar uma vez (responsável)

1. Criar o repositório **público** `robinexe/nexus-releases` (vazio, com um README dizendo que é só para downloads).
2. Guardar a chave privada correspondente à pública embutida no segredo `NEXUS_UPDATE_SIGNING_KEY`. (Se a privada se perdeu:
   ver "Trocar a chave".)
3. Criar o token do item acima e guardá-lo em `NEXUS_RELEASES_TOKEN`.
4. Rodar o release com publish=true. Os PCs com uma versão **anterior a esta funcionalidade** precisam instalar esta à mão
   uma vez; daí em diante, atualizam sozinhos.

Gerar um par novo (no PC, com o openssl do Git):

```powershell
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out nexus-update.key
openssl pkey -in nexus-update.key -pubout            # chave pública: vai em UpdatePolicy.TrustedPublicKeyPem
```

A privada nunca vai para o repositório nem para a conversa: só para o segredo do GitHub (e um cofre offline).

## Trocar a chave (rotação)

A chave pública vem dentro do NEXUS; um NEXUS só aceita manifestos da chave que ele carrega.

1. Gere o par novo. Troque `UpdatePolicy.TrustedPublicKeyPem` pela pública nova **em uma versão assinada com a chave
   ANTIGA** (o segredo ainda é o antigo): os PCs instalam essa versão e passam a confiar só na nova.
2. Espere os PCs atualizarem (ou aceite que quem não abrir o NEXUS nesse período vai precisar instalar à mão).
3. Troque o segredo `NEXUS_UPDATE_SIGNING_KEY` pela privada nova. As próximas versões saem assinadas com ela.

Chave privada vazada: troque já (passos 1 e 3 juntos, numa versão assinada com a antiga — quem vazou também conseguiria
assinar, então avise os usuários para conferirem que estão na versão nova). Chave privada perdida: não há como assinar para
os PCs antigos; publique a versão com a chave nova e peça para instalarem à mão uma vez.
