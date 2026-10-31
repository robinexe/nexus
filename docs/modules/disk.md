# Módulo Disco (`Nexus.DiskTools`, página "Espaço em disco")

Duas partes com riscos muito diferentes:

1. **Análise de disco** — somente leitura. Mostra o que ocupa cada unidade/pasta e explica os itens grandes conhecidos.
   Roda na própria interface (leitura é permitida lá) e nunca apaga nada.
2. **Exclusão segura** — a única escrita do módulo. Sobrescreve e apaga **um arquivo** escolhido pelo usuário. É
   **irreversível** e por isso usa o contrato de ações irreversíveis descrito no fim deste documento.

## 1. Análise de disco

### O que faz

| Recurso | Como | Onde |
|---|---|---|
| Lista de unidades | `DriveInfo` (livre, total, sistema de arquivos) + tipo de mídia por `IOCTL_STORAGE_QUERY_PROPERTY` no volume (`\\.\C:`, aberto sem direitos de leitura/escrita, sem administrador): `StorageDeviceSeekPenaltyProperty` (sem penalidade de busca = SSD) e `StorageDeviceProperty` (barramento NVMe/SATA/USB e modelo). Falhou = "desconhecido", nunca chute. | `Volumes/Volumes.cs` |
| Varredura | Lê só metadados da listagem (nome, tamanho, data, atributos), não abre arquivos. Paralela nos dois primeiros níveis (até 8 threads), cancelável, com progresso a cada ~150 ms. | `Analysis/DiskScanner.cs` |
| Links | Pastas e arquivos com `ReparsePoint` **e** destino (`LinkTarget`: link simbólico, junção) são contados e **nunca seguidos**: sem laços, sem contar duas vezes (ex.: `C:\Documents and Settings`). Reparse points sem destino (arquivos do OneDrive, deduplicação) são conteúdo real e entram. | idem |
| Acesso negado | Contado, guardado (até 20 exemplos) e pulado; a pasta fica marcada como "mínimo" (`Incomplete`, "+" na lista). | idem |
| Nuvem | Arquivos só na nuvem (`Offline`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`) não ocupam o disco: somados à parte. | idem |
| Árvore | Pasta → subpastas da maior para a menor; até 6 maiores arquivos por pasta para o treemap; o resto vira "Outros N arquivos". | `Analysis/DiskModel.cs` |
| 50 maiores arquivos | Fila de prioridade global com limiar atômico (sem travar a cada arquivo). | `DiskScanner` |
| Por tipo | Extensão + contexto da pasta: tudo dentro de bibliotecas de jogos (`steamapps\common`, `Epic Games`, `XboxGames`, `Riot Games`, `GOG Games`…) conta como "Jogos"; `.exe` em Downloads conta como "Instaladores"; `node_modules`/`.git` como "Programação". | `Analysis/FileCategories.cs` |
| Pastas conhecidas | Reconhecidas pelo nome e pelos pais (funciona em qualquer letra de unidade). Ver tabela abaixo. | `Analysis/KnownFolders.cs` |
| Nomes dos jogos | Steam: `steamapps\appmanifest_*.acf` (`name`/`installdir`); Epic: `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` (`DisplayName`/`InstallLocation`). Só leitura, com limite de tamanho. | `Analysis/GameLibraries.cs` |
| Treemap | Algoritmo *squarified* (Bruls, Huizing, van Wijk, 2000), puro e testado; desenhado num único elemento WPF (`OnRender`), clique numa pasta entra nela, migalhas de pão e "Subir". | `Analysis/Treemap.cs`, `src/Nexus.Desktop/Views/Modules/Disk/TreemapView.cs` |
| Abrir no Explorer | `explorer.exe /select,"<caminho>"` (só abre a janela; caminho existente e sem aspas). | `DiskPage.ShowInExplorer` |

Tamanhos são **lógicos** (o que o Explorer mostra em "Tamanho"), não o espaço alocado. Arquivos compactados pelo NTFS ou
esparsos ocupam menos; links físicos (muito comuns em `WinSxS`) aparecem em cada caminho. A explicação do WinSxS diz isso.

### Pastas e arquivos explicados

| Item | Tipo | O que o NEXUS diz para fazer |
|---|---|---|
| `Windows\WinSxS` | Não apague à mão | Limpeza de Disco → "Limpeza de atualizações do Windows" ou `Dism /Online /Cleanup-Image /StartComponentCleanup`; tamanho real com `/AnalyzeComponentStore` (links físicos inflam o número). |
| `hiberfil.sys` (raiz) | Ferramenta oficial | `powercfg /h off` remove, mas desliga hibernação **e** Inicialização rápida (boot a frio um pouco mais lento); meio-termo `powercfg /h /type reduced`; voltar com `powercfg /h on`. |
| `pagefile.sys` | Não apague | Deixe o Windows gerenciar: sem paginação, jogos fecham por "memória insuficiente" e não há relatório de tela azul. |
| `swapfile.sys` | Não apague | Gerenciado com a paginação. |
| `Windows\Installer`, `ProgramData\Package Cache` | Não apague | Sem eles, programas não desinstalam/atualizam. |
| `System Volume Information` | Não apague | Limitar em Proteção do Sistema; ver com `vssadmin list shadowstorage`. |
| `Windows\SoftwareDistribution`, `Temp` | Ferramenta oficial | Limpeza de Disco / página Limpeza do NEXUS. |
| `Windows.old`, `$WINDOWS.~BT`, `$Windows.~WS` | Ferramenta oficial | Limpeza de Disco; avisa que depois não dá para voltar de versão. |
| `Minidump`, `LiveKernelReports`, `MEMORY.DMP` | Ferramenta oficial | Limpeza de Disco, se não for investigar a falha. |
| `steamapps\common\<jogo>` | Jogo | Nome do jogo pelo manifesto; desinstalar ou mover a biblioteca **pela Steam**, nunca apagar a pasta. |
| `Epic Games\<jogo>`, `XboxGames\<jogo>`, `Riot Games\<jogo>` | Jogo | Idem pela loja correspondente. |
| `steamapps\shadercache`, caches do driver (`NVIDIA\DXCache`, `GLCache`, `AMD\DxCache`, `D3DSCache`) | Cache | **Deixe**: apagar causa engasgos até recompilar; só limpar se houver gráficos corrompidos após atualizar driver. |
| `steamapps\downloading`, `steamapps\workshop` | Ferramenta oficial / Jogo | Retomar/cancelar na Steam; cancelar inscrição na Oficina. |
| `Users\<x>\Downloads` | Decisão sua | Mostra quanto são arquivos sem alteração há mais de 90 dias. |
| `node_modules` grande (≥ 300 MB) | Decisão sua | Apagar a pasta do projeto parado; volta com `npm install`. |
| `ext4.vhdx` (WSL), `docker_data.vhdx` | Decisão sua | Não apague; compactar (`wsl --manage <distro> --set-sparse true`) ou `docker system prune`. |

Comandos aparecem num campo somente leitura para o usuário copiar. **O NEXUS não executa nenhum deles.**

### O que NÃO faz (e por quê)

- Não apaga nada pela análise. Liberar espaço de verdade é da página Limpeza (quarentena, desfazer) ou das ferramentas oficiais.
- Não mede espaço alocado no disco (exigiria `GetCompressedFileSize` por arquivo e deixaria a varredura bem mais lenta).
- Não segue links nem junções; não sobe privilégio para ler pastas protegidas (o tamanho delas aparece como mínimo).

## 2. Exclusão segura (`disk.secure-delete@1`, handler `DiskSecureDelete`)

### O que faz

O usuário escolhe **um arquivo** num `OpenFileDialog` (sem seguir atalhos). A página faz uma pré-verificação somente leitura
com a mesma política do aplicador, mostra o tipo de mídia e pede **duas confirmações** (a segunda, do aplicador, com o nome
e o caminho do arquivo). O pedido vai ao aplicador com `ConfirmIrreversible = true`. No aplicador:

1. O broker valida `filePath` pela regra do catálogo (`SecureDeletePolicy.PathPattern`: caminho absoluto com letra, sem `..`,
   sem nomes curtos `~1`, sem `:` de fluxo alternativo, sem curingas, sem UNC/`\\?\`).
2. O handler recusa (`Assess`, e de novo no `Apply`): aplicador como serviço/outra conta ou **elevado** (assim o NEXUS só
   apaga o que o próprio usuário apagaria no Explorer); pastas do Windows, `Program Files*`, `ProgramData`, raízes do sistema
   (`$Recycle.Bin`, `System Volume Information`, `Recovery`, `Windows.old`…) em qualquer unidade; raiz do disco do sistema;
   `C:\Users` solto, perfil `Default`, **raiz do perfil** (onde fica o `NTUSER.DAT`), `AppData\…\Microsoft\{Protect,
   Credentials,Crypto,SystemCertificates,Windows}`; a pasta de dados do NEXUS (diário); `pagefile.sys`, `hiberfil.sys`,
   `ntuser.*`, `usrclass.dat*`, nomes de dispositivo (`CON`, `NUL`…); pasta (só arquivos); link simbólico; **qualquer link ou
   junção no caminho**; arquivo só na nuvem; compactado ou esparso pelo NTFS (a sobrescrita iria para outros setores);
   somente leitura; de sistema; maior que 32 GB; **em uso** (abertura exclusiva falha).
3. Abre com `FileShare.None` e, no Windows, confere **pelo próprio handle**: sem reparse point, `NumberOfLinks == 1`
   (`GetFileInformationByHandle`; com vários links físicos a sobrescrita mudaria o "outro" arquivo) e caminho final
   (`GetFinalPathNameByHandleW`) igual ao pedido — fecha a janela entre verificar e abrir (TOCTOU).
4. Uma passada de bytes aleatórios criptograficamente fortes (`RandomNumberGenerator`) do início ao fim, `FlushFileBuffers`.
5. Renomeia para 16 hex aleatórios na mesma pasta, trunca para 0, zera as datas (2000-01-01) e apaga.
6. O executor relê: o caminho não existe = `Verified`.

Se algo falha **depois** da sobrescrita (ex.: não conseguiu apagar o nome aleatório), a mensagem diz o nome temporário e
que o conteúdo original já não existe nele. O diário fecha a entrada como `Accepted` com essa nota (ver contrato abaixo).

### Por que uma passada só

As 35 passadas de Gutmann (1996) foram desenhadas para codificações de HD antigas (MFM/RLL) e o próprio autor diz que não
fazem sentido em discos atuais. O **NIST SP 800-88 Rev. 1** considera uma única sobrescrita suficiente ("Clear") para HDs
modernos. Mais passadas só gastam tempo e, em SSD, vida útil. O catálogo registra o modo multipassada como rejeitado
(`disk.secure-delete.multipass`, "Ineffective").

### Aviso honesto sobre SSD/NVMe

Em SSD/NVMe o controlador grava os dados novos em **outras células** (nivelamento de desgaste) e o **TRIM** pode já ter
marcado as antigas como livres: nenhuma sobrescrita por software garante destruir as cópias físicas (Wei et al., USENIX
FAST 2011). A página detecta o tipo de mídia (somente leitura, IOCTL) e mostra o aviso antes da confirmação; com tipo
desconhecido, mostra o aviso também. Recomendação: **BitLocker** no disco (os restos ficam ilegíveis sem a chave) e
`cipher /w:C:\` para sobrescrever o espaço livre (ferramenta oficial; o NEXUS não reimplementa — item rejeitado
`disk.free-space-wipe`). Backups, nuvem, Histórico de Arquivos e cópias de sombra não são tocados, e a página diz isso.

### Riscos e como desfaz

**Não desfaz.** Não há quarentena, cópia nem ponto de restauração (ação de usuário). Desfazer e recuperar recusam com
mensagem clara. Os riscos são mitigados pelas recusas acima, pelas duas confirmações e por rodar só com a conta do usuário.

### Evidências

- NIST SP 800-88 Rev. 1, *Guidelines for Media Sanitization* (seção 2.4; Apêndice A).
- Microsoft Learn: `cipher` (`/w`), `FlushFileBuffers`, `GetFinalPathNameByHandleW`, `GetFileInformationByHandle`,
  `IOCTL_STORAGE_QUERY_PROPERTY` / `DEVICE_SEEK_PENALTY_DESCRIPTOR`, `powercfg /hibernate`, Limpeza de Disco e
  `DISM /StartComponentCleanup` (WinSxS).
- Wei, Grupp, Spada, Swanson, *Reliably Erasing Data From Flash-Based Solid State Drives*, USENIX FAST 2011.

### O que NÃO faz

- Pastas inteiras (opcional no plano; deixado de fora: o risco de um recursivo irreversível não compensa nesta versão).
- Espaço livre (use `cipher /w`), disco inteiro (use a ferramenta do fabricante: Secure Erase/Sanitize, ou "Redefinir o PC"
  com limpeza da unidade), arquivos em uso (nada de "apagar no próximo boot").

## Ações irreversíveis (contrato para todos os módulos)

Algumas ações não têm como ser desfeitas (exclusão segura; no futuro, por exemplo, **esvaziar a quarentena**). Em vez de
fingir um backup, o NEXUS tem um contrato explícito, mínimo e retrocompatível:

### Catálogo

```json
{ "id": "modulo.acao", "version": 1, "handlerKind": "...", "irreversible": true,
  "persistence": "Persistent", "...": "..." }
```

- `"irreversible"`: `true`/`false` (ausente = `false`). Qualquer outro valor é erro de carga.
- `"irreversible": true` com `"persistence": "Session"` é **erro de carga** (sessões são revertidas ao encerrar).
- Vira `ActionDefinition.Irreversible` (propriedade `init`, fora do construtor posicional). É gravado no diário junto da
  ação (JSON); entradas antigas sem o campo continuam reversíveis.

### Broker (protocolo, sem mudar a versão)

- `ActionRequest(string ActionId, string? Value = null, IReadOnlyDictionary<string,string>? Arguments = null, bool? ConfirmIrreversible = null)`.
- `Execute` com uma ação irreversível sem `ConfirmIrreversible == true` → resposta `ok:false`, código
  **`irreversible-unconfirmed`**, antes de travar, gravar ou ler qualquer coisa.
- `null` não vai para o fio (`WhenWritingNull`): pedidos antigos são idênticos byte a byte.
- `Plan` não exige confirmação (não escreve) e acrescenta à mensagem um aviso de ação IRREVERSÍVEL.
- O broker passa ao executor o conjunto das chaves confirmadas.

### Executor (`Nexus.Core.ActionExecutor`)

- `Execute(runId, plan, context)` (o de sempre) = nenhuma confirmação: toda ação irreversível pendente sai `Blocked` com
  `ActionExecutor.IrreversibleNotConfirmed`, **sem entrada no diário**.
- `Execute(runId, plan, context, IReadOnlyCollection<string> confirmedIrreversible)`: só aplica as irreversíveis cujas chaves
  `id@versão` estão no conjunto. Ações reversíveis não mudam em nada.
- Falha no meio (Apply lançou, releitura falhou): se o alvo ficou igual ao original, `Reverted` como sempre; senão a entrada
  vai para **`Accepted`** com nota "não há como desfazer" (nunca uma "compensação pendente" impossível), resultado `Failed`.
- `Revert`/`RevertRun`: **nunca chamam `Restore`**. Resultado **`RevertOutcome.Irreversible`** ("Ação irreversível: não há
  como desfazer…"); se a entrada estava pendente (interrompida/falha) é fechada como `Accepted`; `Verified` continua
  `Verified`. Interrompida em `BackedUp` (Apply nem começou) → `Reverted`/`NothingToRevert` ("nada foi feito").
- `RevertOutcome.Irreversible` **não** conta como restaurado: `FullyRestored = false` (honesto).

### Recuperação (`RecoveryService.Recover`)

- Entradas irreversíveis pendentes **não são restauradas**: são encerradas como `Accepted` com nota no diário, e o relatório
  mostra `Irreversible`. Na próxima varredura não aparecem mais.

### Handler

- `Restore` deve lançar (defesa extra) e, se implementar `IRestoreValidator`, recusar sempre.
- `Assess` deve recusar se `action.Definition.Irreversible` for falso (catálogo errado não vira ação "reversível" que apaga).

### Interface

- Duas confirmações: `Host.Confirm` explicando o que é irreversível (e, aqui, o aviso de SSD), depois `Host.ApplyAsync` com
  o nome do item na mensagem; só então o pedido com `ConfirmIrreversible: true`.
- Não oferecer "Desfazer" para essas execuções; mostrar que não há como desfazer.

### Testes do contrato

`tests/Nexus.Tests/Modules/DiskToolsTests.cs` → `IrreversibleContractTests`: catálogo (mapeamento, sessão proibida, valor
inválido), executor (recusa sem confirmação e sem diário; confirmação de outra chave não vale; executa com; Restore nunca
chamado; falha no meio vira Accepted), recuperação (não tenta, marca Accepted, não reaparece; interrompida antes do Apply),
diário SQLite (campo persiste; JSON antigo = reversível), broker (`irreversible-unconfirmed` com null/false, executa com,
RevertRun `Irreversible` + `FullyRestored=false`, Recover não tenta) e fio (campo omitido quando nulo).

## Como validar no Windows

```powershell
dotnet build Nexus.slnx -c Release
dotnet test tests\Nexus.Tests -c Release --no-build --filter "FullyQualifiedName~Nexus.Tests.Modules"
```

Na interface: "Espaço em disco" → escolher a unidade → "Analisar esta unidade" (cancelar no meio deve parar na hora) →
clicar em pastas no treemap, voltar pelas migalhas, "Mostrar no Explorer". Exclusão segura: criar um arquivo de teste
em Documentos, escolher em "Exclusão segura…", conferir o aviso de SSD/HD, confirmar duas vezes; tentar um arquivo em
`C:\Windows` ou aberto no Bloco de Notas deve ser recusado com o motivo. Em VM: `tests/Nexus.IntegrationTests/Modules/DiskToolsIntegrationTests.cs`
(verificação real de link físico e junção, e o contrato pelo broker com diário SQLite).
