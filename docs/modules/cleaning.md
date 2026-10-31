# Módulo Limpeza (`Nexus.Cleaning`)

Página **Limpeza** (grupo "Limpar"). Mede o que pode ser liberado e, quando você pede, move os arquivos para uma
**quarentena** do NEXUS. Nada é apagado de verdade, e cada limpeza pode ser desfeita.

## O que faz

| Parte | Onde | Escreve? |
|---|---|---|
| Regras declarativas (34 de quarentena + 3 só de medição) | `src/Nexus.Cleaning/Rules/cleaning-rules.json` (recurso embutido) | Não |
| Varredura rápida (paralela, cancelável, sem seguir links/junções) | `CleaningScanner`, `CleaningEnumerator`, `CleaningPaths` | Não (roda na interface) |
| Handler `CleaningRule` (Detect/Apply/Restore) | `CleaningRuleHandler`, `QuarantineMover` (interno) | Sim, só no aplicador |
| Leitura da quarentena (lotes, tamanhos) | `QuarantineStore` | Não |
| Pastas reais do Windows (perfil por SID, Steam, Lixeira) | `WindowsCleaningFolders`, `RecycleBinInfo` | Não |
| Catálogo | `catalog/cleaning.v1.json` (uma ação `cleaning.<regra>` por regra de quarentena) | — |
| Página | `ViewModels/Modules/CleaningPage.cs`, `Views/Pages/Cleaning.xaml` | Não: escreve só via `Host.ApplyAsync`/`RevertAsync` |

Categorias e regras:

- **Sistema:** temporários da conta (>24 h), `Windows\Temp` (>48 h, administrador), miniaturas do Explorador, relatórios
  de erro WER (conta e sistema), `CbsPersist_*` com mais de 30 dias (nunca o `CBS.log` atual), despejos de travamento
  (`CrashDumps`; `Minidump`, `LiveKernelReports` e `MEMORY.DMP` como administrador).
  **Só medição + ferramenta oficial:** Lixeira (esvaziar é irreversível; abrimos a Lixeira), Otimização de Entrega
  (pertence a um serviço; abrimos Configurações > Armazenamento), downloads do Windows Update (exigem o serviço parado; idem).
- **Navegadores:** cache, Code Cache, GPUCache e caches de shader de Chrome, Edge, Brave, Opera/Opera GX e Firefox, em todos
  os perfis. Nunca cookies, senhas, histórico ou sessões. Histórico e cookies de Chrome/Edge/Brave existem como regras
  **opcionais, desmarcadas, com aviso forte**, que também vão para a quarentena (o "marcar todos" nunca as marca).
- **Apps:** Discord (incl. PTB/Canary), Spotify (cache de streaming; downloads offline ficam), Teams (novo e clássico), Slack.
- **Jogos:** caches de shader NVIDIA/AMD/Intel/DirectX (desmarcados, com a explicação honesta de que a primeira partida pode
  engasgar), Steam (htmlcache; logs/dumps e shadercache como administrador), Epic (webcache, logs), Battle.net.

Cada regra declara: raízes com placeholder (`%UserTemp%`, `%LocalAppData%`, `%LocalAppDataLow%`, `%AppData%` para o perfil;
`%ProgramData%`, `%WinDir%`, `%SteamPath%` para a máquina), segmentos com curinga (ex.: `User Data\*\Cache` = todos os
perfis), padrões de arquivo, idade mínima, exclusões, se exige administrador, programas que precisam estar fechados,
explicação, ganho esperado e fonte.

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
   `nome (restaurado).ext` (idêntico = só remove a cópia duplicada). Recria pastas apagadas, sem atravessar links.

### Como o diário encontra o manifesto

O alvo da ação é `{ rule, batch, scope? , userSid? }`. O `batch` (id do lote, `AAAAMMDDTHHMMSSZ-xxxxxxxx`) é escolhido pela
interface e validado pela regex fechada de `userParameters`; o `CatalogBoundHandler` reconstrói e confere o alvo a partir do
catálogo antes de restaurar. Como o caminho do manifesto é função determinística de `DataDirectory + batch + rule`, o diário
(alvo + `StateSnapshot` original `CleaningBatch:not-applied`) basta para desfazer ou recuperar:

- queda **depois** de mover arquivos: Detect vê arquivos do lote na quarentena = `applied` (= desejado) → a recuperação
  restaura (caso A1, marcado como inferido);
- queda **antes** de mover qualquer coisa: Detect = `not-applied` (= original) → nada a fazer;
- restauração parcial (ex.: falta permissão): os arquivos que ficaram mantêm o Detect em `applied`, o executor registra
  `Restoring` não confirmado, e uma nova tentativa continua de onde parou.

Perfil do usuário: regras de perfil têm `"scope": "HKCU"` no alvo — é a convenção do núcleo (`UserProfileScope.IsUserScoped`)
para o broker-serviço anexar o `userSid` de quem pediu; o handler então lê as pastas do perfil pelo registro
(`HKU\<SID>\Volatile Environment`, `ProfileList`), nunca do processo do aplicador. Sem SID e com `OtherAccount=true`, a regra de
perfil é recusada. Regras de perfil são `User` (aplicador da própria conta, sem UAC); as de máquina são `Admin` (UAC + ponto de
restauração automático).

## Página

Resumo grande "X recuperáveis" (medido na hora, só leitura), categorias com caixas e tamanhos, itens não instalados
agrupados, aviso de programas abertos, botão principal **Limpar selecionados** (confirmação lista o que vai sair e diz que
tudo vai para a quarentena; parte de administrador é uma segunda confirmação), **Desfazer última limpeza**, histórico (lotes
da quarentena, com data, regras, tamanho e "Restaurar" = `Host.RevertAsync` da execução do diário que limpou aquele lote,
encontrada lendo o diário em modo somente leitura) e o tamanho da quarentena.

## O que NÃO faz (e por quê)

- **Não esvazia a quarentena** nem a Lixeira: exclusão permanente é irreversível e o contrato de ações irreversíveis ainda
  não existe. Consequência honesta (dita na página): no mesmo disco, o espaço só volta de fato quando a quarentena for
  esvaziada.
- **Não mexe em Windows Update / Otimização de Entrega:** só mede e abre a ferramenta oficial.
- **Não promete desempenho:** limpar cache não aumenta FPS; caches de shader limpos pioram a fluidez no começo.
- Não limpa "registro", "RAM", prefetch nem pastas de jogos.
- Não cobre bibliotecas extras do Steam (só a pasta registrada da instalação) nem perfis de navegador fora do local padrão.
- Se o programa recria um arquivo de mesmo nome depois da limpeza (ex.: abrir o Chrome antes de desfazer o histórico), o
  arquivo antigo volta com o sufixo " (restaurado)" e o programa continua usando o novo.

## Riscos

Baixos para caches e temporários (os programas recriam). Histórico/cookies: perda de sessão/histórico até desfazer.
Shaders: engasgos nas primeiras partidas. `Windows\Temp`/WER/CBS: só arquivos velhos e não abertos.

## Fontes

Cada regra cita a sua (campo `evidence`). Principais: Microsoft — "Liberar espaço em disco no Windows", WER Settings,
Collecting User-Mode Dumps, Memory dump file options, `Delete-DeliveryOptimizationCache`, `SHQueryRecycleBinW`, limpar cache
do Teams; Google/Mozilla/Opera/Microsoft — limpar cache e cookies; Spotify — armazenamento; Blizzard — artigo 27596.

## Validar no Windows

1. `dotnet test tests/Nexus.Tests -c Release --filter FullyQualifiedName~Cleaning` (unitários, também passam no Linux).
2. VM: `tests/Nexus.IntegrationTests/Modules/CleaningIntegrationTests.cs` (broker real em `%TEMP%`: move, pula arquivo aberto,
   desfaz).
3. Interface: abrir Limpeza, conferir o total com o tamanho real das pastas (Propriedades no Explorador), abrir o Chrome e ver
   o aviso/bloqueio, limpar temporários, conferir `%LocalAppData%\NEXUS\quarantine\cleaning\<lote>`, Desfazer.
