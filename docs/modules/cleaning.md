# Módulo Limpeza (`Nexus.Cleaning`)

Página **Limpeza** (grupo "Limpar"), em quatro abas: **Limpar**, **Quarentena**, **Histórico** e **Regras**. Mede o que pode ser
liberado e, quando você pede, move os arquivos para uma **quarentena** do NEXUS: nada é apagado de verdade, e cada limpeza
pode ser desfeita. Apagar de vez (esvaziar a quarentena, esvaziar a Lixeira) existe, mas como **ação irreversível**, pelo
contrato de `docs/modules/disk.md` (seção "Ações irreversíveis"): duas confirmações com o tamanho exato e sem "Desfazer".

## O que faz

| Parte | Onde | Escreve? |
|---|---|---|
| Regras declarativas (50 de quarentena + 3 só de medição) | `src/Nexus.Cleaning/Rules/cleaning-rules.json` (recurso embutido) | Não |
| Regras do usuário (JSON, mesmo formato) | `%AppData%\NEXUS\rules\*.json`, `UserCleaningRules` (guia: `cleaning-rules-guide.md`) | Não |
| Varredura rápida (paralela, cancelável, sem seguir links/junções) | `CleaningScanner`, `CleaningEnumerator`, `CleaningPaths` | Não (roda na interface) |
| Handler `CleaningRule` (regras embutidas) e `CleaningUserRule` (regras do usuário) | `CleaningRuleHandler.cs` (base `CleaningBatchHandlerBase`), `QuarantineMover` (interno) | Sim, só no aplicador |
| Handler `CleaningQuarantinePurge` (esvaziar, **irreversível**) | `QuarantinePurge.cs` (`QuarantinePurger` interno) | Sim, só no aplicador |
| Handler `CleaningRecycleBin` (esvaziar a Lixeira, **irreversível**) | `QuarantinePurge.cs`, `WindowsRecycleBin` (Shell) | Sim, só no aplicador |
| Leitura da quarentena (lotes, tamanhos, expirados) | `QuarantineStore` (`ReadBatches`, `ExpiredBatches`, `MeasureOnDisk`) | Não |
| Histórico agregado (sessões, totais, meses, CSV) | `CleaningHistory` (manifestos + diário somente leitura) | Não |
| Pastas reais do Windows (perfil por SID, Steam, Lixeira) | `WindowsCleaningFolders`, `RecycleBinInfo` | Não |
| Catálogo | `catalog/cleaning.v1.json`: uma ação `cleaning.<regra>` por regra de quarentena + 6 ações fixas (abaixo) | — |
| Página | `ViewModels/Modules/CleaningPage.cs`, `Views/Pages/Cleaning.xaml` | Não: escreve só via `Host.ApplyAsync`/`RevertAsync` |

Ações fixas do catálogo:

| Ação | Handler | Nível | Irreversível | Parâmetros |
|---|---|---|---|---|
| `cleaning.quarantine.purge` | `CleaningQuarantinePurge` | User (quarentena da conta) | sim | `batch` |
| `cleaning.quarantine.purge.machine` | `CleaningQuarantinePurge` | Admin (quarentena do administrador) | sim | `batch` |
| `cleaning.quarantine.purge-older` | `CleaningQuarantinePurge` | User | sim | `days` = `[1-9][0-9]{0,2}` |
| `cleaning.quarantine.purge-older.machine` | `CleaningQuarantinePurge` | Admin | sim | `days` |
| `cleaning.recycle-bin.empty` | `CleaningRecycleBin` | User | sim | `drive` = `[A-Z]\|all` |
| `cleaning.user-rule` | `CleaningUserRule` | User | não (quarentena) | `rule` (id), `batch` |

### Categorias e regras embutidas

- **Sistema:** temporários da conta (>24 h), `Windows\Temp` (>48 h, administrador), miniaturas do Explorador, relatórios
  de erro WER (conta e sistema), `CbsPersist_*` com mais de 30 dias (nunca o `CBS.log` atual), despejos de travamento
  (`CrashDumps`; `Minidump`, `LiveKernelReports` e `MEMORY.DMP` como administrador), **registros antigos de instalação**
  (`Windows\Logs\DISM`, `MoSetup`, `NetSetup`: só `*.log` sem escrita há 30 dias, desmarcado; Panther e WindowsUpdate ficam).
  **Só medição + ferramenta oficial:** Otimização de Entrega e downloads do Windows Update (pertencem a um serviço; abrimos
  Configurações > Armazenamento). A **Lixeira** tem cartão próprio na aba Limpar (medir, abrir, esvaziar).
- **Navegadores:** cache, Code Cache, GPUCache e caches de shader de Chrome, Edge, Brave, Opera/Opera GX e Firefox, em todos
  os perfis. Nunca cookies, senhas, histórico ou sessões. Histórico e cookies de Chrome/Edge/Brave existem como regras
  **opcionais, desmarcadas, com aviso forte**, que também vão para a quarentena (o "marcar todos" nunca as marca).
- **Apps:** Discord (incl. PTB/Canary), Spotify, Teams (novo e clássico), Slack, **Microsoft Store** (só `LocalState\Cache`,
  >24 h, desmarcado; nunca `wsreset`), **OBS Studio** (logs e relatórios de travamento >7 dias).
- **Jogos:** caches de shader NVIDIA/AMD/Intel/DirectX e **NV_Cache** global da NVIDIA (desmarcados, com a explicação honesta
  de que a primeira partida pode engasgar), Steam (htmlcache; logs/dumps e shadercache como administrador), Epic (webcache,
  logs), Battle.net, **instaladores extraídos da NVIDIA** (`C:\NVIDIA\DisplayDriver`) e **da AMD** (`C:\AMD`) com mais de 30
  dias e sem instalador aberto (administrador, desmarcados), **logs** do Riot Client/VALORANT, Minecraft (Java, `.log.gz`,
  nunca `latest.log`), EA app e Ubisoft Connect (>3 dias).
- **Desenvolvedor** (sempre desmarcado, rotulado "desenvolvedor"): VS Code (caches Electron, `CachedData`,
  `CachedExtensionVSIXs`), npm (`npm-cache\_cacache`), pip (`pip\Cache`), NuGet (só `v3-cache`/`plugins-cache`, nunca
  `.nuget\packages`), Gradle (`%UserProfile%\.gradle\caches`, >7 dias), Java (cache de implantação).
- **Suas regras:** as regras válidas de `%AppData%\NEXUS\rules` (ver abaixo).

Cada regra declara: raízes com placeholder (`%UserTemp%`, `%LocalAppData%`, `%LocalAppDataLow%`, `%AppData%`,
`%UserProfile%` para o perfil; `%ProgramData%`, `%WinDir%`, `%SteamPath%`, `%SystemDrive%` para a máquina), segmentos com
curinga (ex.: `User Data\*\Cache` = todos os perfis), padrões de arquivo, idade mínima, exclusões, se exige administrador,
programas que precisam estar fechados, explicação, ganho esperado e fonte. `%UserProfile%` e `%SystemDrive%` nunca são raiz
sozinhos: `%UserProfile%` exige uma pasta literal abaixo (ex.: `.gradle`), `%SystemDrive%` só aceita `NVIDIA` e `AMD`.

## Como funciona (e por que é seguro)

1. **Elegível** = dentro de uma raiz da regra, casa um padrão, não excluído, `LastWriteTime` mais antigo que a idade mínima
   (data no futuro nunca conta como velha), e **nenhum link simbólico ou junção** no caminho (atributo `ReparsePoint`
   verificado em cada pasta e arquivo; curingas só casam pastas reais).
2. **Alvos proibidos sempre** (`CleaningPaths.ForbiddenReason`): raiz de unidade, `System32`/`SysWOW64`/`WinSxS`/
   `SystemApps`/`servicing`/`Boot`, a pasta do perfil inteira, Documentos/Desktop/Imagens/Vídeos/Músicas/Downloads/OneDrive,
   Arquivos de Programas, as pastas de dados inteiras e a própria quarentena. `%WinDir%` como raiz só com nome exato e sem
   subpastas (`MEMORY.DMP`).
3. **Programa aberto** (`closeApps`): a ação fica *Bloqueada* com o motivo ("Feche chrome antes…"); a página avisa antes.
4. **Apply** (no aplicador, após o diário gravar `Applying`): grava o **manifesto** com a lista completa (tudo `pending`)
   ANTES de mover qualquer arquivo; depois move cada um para `DataDirectory\quarantine\cleaning\<lote>\<regra>\files\<n>`
   (mesmo volume: renomear; outro volume: copiar, conferir tamanho + SHA-256, só então remover a origem). Arquivo em uso
   (abrir sem compartilhamento falha) ou movimento recusado é **pulado e contado**. Pastas vazias ficam.
5. **Verificação:** Detect relê o manifesto e confere que os arquivos movidos estão na quarentena → `CleaningBatch:applied`.
6. **Restore:** devolve cada arquivo ao caminho original, revalidado pelo **modelo** da regra (um manifesto adulterado não
   consegue mandar arquivos para fora das raízes). Nunca sobrescreve: se um arquivo novo ocupa o lugar, o antigo volta como
   `nome (restaurado).ext` (idêntico = só remove a cópia duplicada). Recria pastas apagadas, sem atravessar links. Se o lote
   foi **esvaziado**, recusa com "a quarentena deste lote foi esvaziada" (Detect = `CleaningBatch:purged`).

### Como o diário encontra o manifesto

O alvo da ação é `{ rule, batch, scope? , userSid? }`. O `batch` (id do lote, `AAAAMMDDTHHMMSSZ-xxxxxxxx`) é escolhido pela
interface e validado pela regex fechada de `userParameters`; o `CatalogBoundHandler` reconstrói e confere o alvo a partir do
catálogo antes de restaurar. Como o caminho do manifesto é função determinística de `DataDirectory + batch + regra`, o diário
(alvo + `StateSnapshot` original `CleaningBatch:not-applied`) basta para desfazer ou recuperar:

- queda **depois** de mover arquivos: Detect vê arquivos do lote na quarentena = `applied` (= desejado) → a recuperação
  restaura (caso A1, marcado como inferido);
- queda **antes** de mover qualquer coisa: Detect = `not-applied` (= original) → nada a fazer;
- restauração parcial (ex.: falta permissão): os arquivos que ficaram mantêm o Detect em `applied`, o executor registra
  `Restoring` não confirmado, e uma nova tentativa continua de onde parou.

Regras do usuário usam a pasta `u_<id>` no lote (o `_` não existe em ids, então nunca colidem com uma embutida).

Perfil do usuário: regras de perfil têm `"scope": "HKCU"` no alvo — é a convenção do núcleo (`UserProfileScope.IsUserScoped`)
para o broker-serviço anexar o `userSid` de quem pediu; o handler então lê as pastas do perfil pelo registro (`ProfileList`
em HKLM), nunca do processo do aplicador. Sem SID e com `OtherAccount=true`, a regra de perfil é recusada. Regras de perfil
são `User` (aplicador da própria conta, sem UAC); as de máquina são `Admin` (UAC + ponto de restauração automático).

## Esvaziar a quarentena (irreversível)

- **Onde fica cada quarentena:** a da sua conta em `%LocalAppData%\NEXUS\quarantine\cleaning` (aplicador do usuário), a do
  administrador em `%ProgramData%\NEXUS\quarantine\cleaning` (aplicador elevado; pasta só de administradores). A página lê as
  duas e manda cada lote para o aplicador certo: `cleaning.quarantine.purge` (User) ou `.machine` (Admin, com UAC).
- **Handler:** recusa se o catálogo não marcar a ação como irreversível; nível `user` só no aplicador da própria conta (nunca
  no serviço); nível `machine` só elevado. Antes de apagar, marca cada manifesto como `purging` (o desfazer da limpeza já passa a
  recusar); apaga os arquivos de `files\` **sem seguir links** (uma junção/link dentro da quarentena tem só a própria entrada
  removida; o destino nunca é tocado); marca `purged` com data, arquivos e bytes apagados. Os manifestos ficam (o histórico
  mostra "esvaziada"; o espaço deles é desprezível).
- **Detect/verificação:** arquivos e bytes de fato presentes no disco nos lotes do alvo (`MeasureOnDisk`); `0` = `purged`.
  Falha no meio: o executor fecha como `Accepted` com "não há como desfazer" (contrato), e a página mostra o motivo.
- **Página:** "Esvaziar" por lote, "Esvaziar quarentena" (tudo) e "Esvaziar mais antigos" (7/14/30/60/90 dias), todos com
  aparência Danger. Primeira confirmação (`Host.Confirm`) com a **lista** (data, regras, tamanho por lote) e o **tamanho exato
  medido no disco agora** (bytes); segunda no `Host.ApplyAsync`; pedido com `ConfirmIrreversible: true`. Um lote por ação
  (lista exata), até 64 por pedido. Desfazer não é oferecido.
- **`purge-older` (por idade):** existe para o Automático e a linha de comando. A página usa a lista exata (por lote), para que
  o que foi mostrado seja exatamente o que sai.

### Expiração automática (opcional, desligada por padrão)

Aba Quarentena > "Expiração automática": Desligado / 7 / 14 / 30 / 60 / 90 dias (preferência local em
`%LocalAppData%\NEXUS\cleaning-settings.json`). Ligada, a página **só sugere** ("N limpezas com mais de X dias ocupam Y.
Esvaziar?") e o botão segue o mesmo fluxo de duas confirmações. **Nada é apagado sozinho** (o NEXUS nunca agenda ações que
escrevem; ver `automation.md`). Para o módulo Automático: `QuarantineStore.ExpiredBatches(dataDirectory, days, nowUtc)` lista os
lotes vencidos, e a ação `cleaning.quarantine.purge-older` (parâmetro `days`, regex fechada, 1–999 dias) pode ser **sugerida**;
como tem parâmetro do usuário e é irreversível, nunca entra no "Otimizar agora".

## Esvaziar a Lixeira (irreversível)

`cleaning.recycle-bin.empty`, nível User, parâmetro `drive` (letra ou `all`). Usa `SHEmptyRecycleBinW` com
`SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND` (o NEXUS já confirmou duas vezes); tamanho antes e depois por
`SHQueryRecycleBinW`; verificado = 0 itens e 0 bytes. **Recusado** no aplicador como serviço ou com outra conta (a Lixeira é de
cada usuário). Página: cartão "Lixeira" na aba Limpar, com a unidade (todas ou uma), "Esvaziar Lixeira" (Danger) e "Abrir a
Lixeira" para revisar antes. O tamanho na confirmação é relido na hora.

## Regras do usuário

Arquivos `%AppData%\NEXUS\rules\*.json`, mesmo formato das embutidas; guia completo com exemplos em
[`cleaning-rules-guide.md`](cleaning-rules-guide.md). Resumo da segurança:

- **Validação rígida** (`CleaningRuleSet.ParseUserRules`): só `scope: User`, só `Quarantine`, nada de `admin`/`special`/
  `nativeTool`, só placeholders de **perfil**, uma pasta literal logo depois do placeholder, fora de uma lista de pastas
  protegidas (DPAPI/credenciais/certificados, Menu Iniciar, Recentes, pastas do NEXUS, `%LocalAppData%\Programs`, pastas
  pessoais do perfil, `.ssh`, `.gnupg`) — inclusive por curinga ou por pasta-mãe recursiva; ids que não colidem com as
  embutidas nem entre arquivos; limites de tamanho. Depois, na limpeza, valem todas as proteções das embutidas.
- **Nunca no aplicador elevado nem no serviço** (`CleaningUserRuleHandler` recusa tudo com `Elevated` ou `OtherAccount`, e
  `CleaningHandlers.Create` nem carrega as regras nesses casos): uma regra que o usuário (ou um programa dele) escreve não pode
  virar movimento de arquivo com privilégio.
- **De onde vêm:** o aplicador do usuário acha a pasta pelo **SID do próprio processo + `ProfileList`** (mesmo cuidado da
  revisão de segurança para o serviço), não pelas variáveis de ambiente. Relê e revalida a cada pedido; só aceita ids de regras
  carregadas e válidas.
- **Desfazer:** o manifesto guarda a regra (`ruleSnapshot`); se o arquivo foi apagado ou mudou, o desfazer usa essa cópia,
  revalidada com a mesma política.
- **Página (aba Regras):** caminho da pasta, "Abrir pasta de regras" (cria a pasta e, na primeira vez, copia
  `exemplo-desativado.json`, comentado e com `"enabled": false`), "Recarregar", e cada arquivo com as regras que valem ou o erro
  (JSON quebrado com a linha, regra recusada com o motivo, id repetido). Arquivo com erro não vale inteiro.

## Histórico

Aba Histórico, montado por `CleaningHistory.Build` a partir dos **manifestos** (o que foi movido, devolvido e esvaziado, com
bytes e datas) e do **diário** aberto em somente leitura (a execução que limpou cada lote, para o "Restaurar", e os
esvaziamentos da Lixeira com o tamanho original):

- Totais: **recuperado de verdade** no mês e no total (= quarentena esvaziada + Lixeira esvaziada), **na quarentena**
  (recuperável: ainda ocupa o disco), limpo no mês e devolvido no total.
- Gráfico de barras dos últimos 12 meses (limpo × recuperado de verdade).
- Lista de sessões: data, regras, arquivos, bytes, nível (sua conta/administrador), situação (na quarentena, desfeita,
  esvaziada, parcial), "inclui suas regras".
- **Exportar CSV** (`SaveFileDialog`, UTF-8 com BOM, separador `;`): uma linha por limpeza e por esvaziamento da Lixeira,
  datas ISO 8601 UTC, bytes inteiros; campos escapados e fórmulas neutralizadas (`=`, `+`, `-`, `@` viram texto).

## Limpeza de atualizações do Windows (WinSxS): decisão

Opções avaliadas: `DISM /Online /Cleanup-Image /StartComponentCleanup` (irreversível, demorado), a tarefa agendada
`\Microsoft\Windows\Servicing\StartComponentCleanup` e a Limpeza de Disco (`cleanmgr`). **Escolha: abrir a Limpeza de Disco**
(botão "Abrir a Limpeza de Disco" no cartão "Ferramentas do Windows", depois de `Host.Confirm`), porque é a via oficial em que
o usuário **vê e escolhe** os itens ("Limpar arquivos do sistema" > "Limpeza de atualizações do Windows"), e o Windows já roda
sozinho a tarefa StartComponentCleanup, que espera 30 dias antes de remover versões antigas. O NEXUS **não** roda o DISM: segundo
a Microsoft, `/StartComponentCleanup` pelo DISM remove as versões substituídas **na hora, sem os 30 dias de carência**, e com
`/ResetBase` as atualizações instaladas deixam de poder ser desinstaladas. Não há ganho de desempenho em nenhuma das vias.
Fonte: Microsoft Learn, "Clean Up the WinSxS Folder"
(https://learn.microsoft.com/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder).

## O que NÃO faz (e por quê)

- **Prefetch:** limpar é mito (o Windows recria e o primeiro boot/abertura fica mais lento). Não existe regra.
- **Histórico de verificação do Windows Defender:** não é lixo (é evidência de segurança). Não existe regra.
- **`wsreset`:** apaga também a sessão da Store e roda sem mostrar o que faz; só a pasta de cache conhecida.
- **Cache do EA app e do Ubisoft Connect:** o do EA é limpo pelo próprio app (Ajuda > Recuperação do aplicativo); o do Ubisoft
  fica em Arquivos de Programas (não tocamos). Só os logs.
- **DISM / WinSxS:** ver decisão acima. **Windows Update / Otimização de Entrega:** só mede e abre a ferramenta oficial.
- **Não promete desempenho:** limpar cache não aumenta FPS; caches de shader limpos pioram a fluidez no começo.
- Não limpa "registro", "RAM" nem pastas de jogos; não cobre bibliotecas extras do Steam (só a pasta registrada) nem perfis de
  navegador fora do local padrão.
- Não esvazia nada sozinho (nem com a expiração ligada).
- Se o programa recria um arquivo de mesmo nome depois da limpeza (ex.: abrir o Chrome antes de desfazer o histórico), o
  arquivo antigo volta com o sufixo " (restaurado)" e o programa continua usando o novo.

## Riscos

Baixos para caches e temporários (os programas recriam). Histórico/cookies: perda de sessão/histórico até desfazer.
Shaders: engasgos nas primeiras partidas. `Windows\Temp`/WER/CBS/logs de instalação: só arquivos velhos e não abertos.
Instaladores NVIDIA/AMD: só com mais de 30 dias e sem instalador aberto (um novo instalador extrai de novo).
Desenvolvedor: o próximo build/install baixa de novo (tempo e dados). **Esvaziar quarentena/Lixeira: irreversível** — por isso
as duas confirmações com o tamanho exato.

## Fontes

Cada regra cita a sua (campo `evidence`). Principais: Microsoft — "Liberar espaço em disco no Windows", WER Settings,
Collecting User-Mode Dumps, Memory dump file options, `Delete-DeliveryOptimizationCache`, `SHQueryRecycleBinW`,
`SHEmptyRecycleBinW`, "Clean Up the WinSxS Folder", "Deployment troubleshooting and log files", dados de apps empacotados
(LocalState), NuGet "Managing the global packages, cache, and temp folders", limpar cache do Teams; npm (`npm cache`); pip
(caching); Gradle (directory layout); Google/Mozilla/Opera/Microsoft — limpar cache e cookies; Spotify — armazenamento;
Blizzard — artigo 27596; Minecraft Wiki (`.minecraft`); suporte Riot/EA/Ubisoft/OBS (locais dos logs); Painel de Controle
NVIDIA (cache de shader); Oracle (Painel de Controle Java).

## Validar

1. Unitários (também no Linux): `dotnet test tests/Nexus.Tests -c Release --filter FullyQualifiedName~Cleaning`
   (`CleaningTests.cs` + `CleaningV2Tests.cs`: contrato irreversível no broker e no executor, junção dentro da quarentena,
   lote esvaziado não restaura, expiração por data, Lixeira falsa, validação das regras do usuário, recusa no aplicador elevado
   e no serviço, histórico a partir de manifestos e diário sintéticos, CSV, paridade das regras novas com o catálogo).
2. VM: `tests/Nexus.IntegrationTests/Modules/CleaningIntegrationTests.cs` (limpa %TEMP% e desfaz; esvazia com junção real
   `mklink /J` dentro da quarentena; Lixeira real com e sem confirmação; regra do usuário em `%AppData%\NEXUS\rules` recusada
   com elevação e aplicada/desfeita sem).
3. Interface no Windows: abrir Limpeza; aba Limpar (conferir o total com Propriedades no Explorador, abrir o Chrome e ver o
   aviso, limpar temporários); aba Quarentena (lote aparece, "Restaurar", "Esvaziar" com as duas confirmações e o tamanho; o
   lote passa a "esvaziada" e o Restaurar some); cartão Lixeira (mandar um arquivo para a Lixeira, ver o tamanho, esvaziar);
   aba Histórico (totais, barras, Exportar CSV abre no Excel); aba Regras (Abrir pasta de regras cria o exemplo desativado;
   copiar para `meu.json`, `"enabled": true`, Recarregar, ver em "Suas regras"; quebrar o JSON e ver o erro com a linha).
