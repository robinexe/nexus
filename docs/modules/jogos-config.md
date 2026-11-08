# Configuração de jogos (handler `GameConfigValue`)

Código: `src/Nexus.GameProfiles/Configs/` · Catálogo: `catalog/games.v1.json` · Testes: `tests/Nexus.Tests/Modules/GameConfigTests.cs`.

Edita o arquivo de configuração que o próprio jogo grava, com o jogo fechado, trocando só o **valor** de uma linha que já
existe. Nunca cria arquivo, nunca acrescenta chave, nunca marca o arquivo como somente leitura. Desfazer devolve o valor
exato. Lista fechada de jogos, chaves e valores (`GameConfigs.All`), sempre valores do menu do jogo. **Nunca limita FPS.**

| Jogo | Arquivo | Ajustes |
|---|---|---|
| Minecraft (Java) | `%APPDATA%\.minecraft\options.txt` | V-Sync off, FPS ilimitado (260), gráficos rápidos, sem nuvens, partículas mínimas, sem sombras de entidades |
| Rainbow Six Siege | `Documentos\My Games\Rainbow Six - Siege\<conta>\GameSettings.ini` | FPSLimit 0, Reflex + boost, V-Sync off, 1 quadro na fila da GPU |
| Overwatch 2 | `Documentos\Overwatch\Settings\Settings_v0.ini` | limite personalizado 600 (máximo do menu), sem limitar à taxa do monitor, Reflex + boost, sem escala dinâmica |
| Apex Legends | `Jogos Salvos\Respawn\Apex\local\videoconfig.txt` | V-Sync off, SSAO off, iluminação volumétrica off, partículas suaves off, sombras do sol off |
| Minecraft (Java) — complemento 08/11 | idem | distância de renderização 8, de simulação 6 (`catalog/minecraft.v1.json`, `docs/modules/minecraft.md`) |
| Minecraft (Bedrock) | `%LOCALAPPDATA%\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang\minecraftpe\options.txt` | V-Sync off (`gfx_vsync`) |
| Roblox | `%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml` (sintaxe `XmlNamed`) | FPS máximo 240 (`FramerateCap`, o máximo do menu) — `docs/modules/roblox.md` |
| GTA V (Legacy / Enhanced) | `Documentos\Rockstar Games\GTA V\settings.xml` / `…\GTAV Enhanced\settings.xml` (sintaxe `XmlAttribute`) | VSync off, não pausar ao perder o foco — `docs/modules/gtav.md` |

Jogos novos entram pela pasta deles (`src/Nexus.GameProfiles/<Jogo>/`) com uma classe `IGameConfigProvider` achada por reflexão,
sem editar a lista central. Sintaxes: `Equals`, `Colon`, `KeyValues`, `XmlAttribute` (`<Chave value="…" />`) e `XmlNamed`
(`<tipo name="Chave">valor</tipo>`), sempre um elemento por linha e sem mexer na estrutura.

- **Recusas:**
  - jogo aberto (ele regravaria o arquivo ao sair);
  - arquivo somente leitura;
  - codificação desconhecida;
  - chave repetida;
  - várias contas (R6 com mais de uma pasta: o NEXUS não escolhe).
- **"Não se aplica"** (sem ruído no relatório):
  - jogo não instalado ou nunca aberto;
  - versão do jogo que não grava a chave.
- **Pastas:** Documentos também é procurado no OneDrive. A pasta do perfil vem do ProfileList pelo SID, os links são recusados e nada sai da pasta do perfil.
- **No "Otimizar meu PC" (Desempenho máximo):** entram todos os ajustes acima, exceto Reflex em placa que não é NVIDIA.

## Cartões na tela Jogos (08/11/2026)

Overwatch 2 e Apex Legends têm cartão próprio (`src/Nexus.GameProfiles/Overwatch2/Overwatch2Card.cs`, `Apex/ApexCard.cs`) que agrupa
estas ações em opções de um clique com estado lido (`GameCardConfigReads`); detalhes e fontes em `docs/modules/overwatch2.md` e
`docs/modules/apex.md`. VALORANT, League of Legends e Call of Duty têm cartão só com Medir/Comparar (`docs/modules/valorant.md`,
`lol.md`, `cod.md`).

## Fora, só com orientação no jogo (pesquisa de 06/11/2026, confirmada em 08/11/2026)

- **VALORANT, League of Legends** (Vanguard): o arquivo sincroniza na conta e a Riot não autoriza edição.
- **Call of Duty** (Ricochet): os guias exigem arquivo somente leitura; sem posição da Activision.
- **Marvel Rivals:** a NetEase avisou em 08/2026 que editar a configuração pode dar punição.
- **Rocket League** (EAC desde 2026): exigiria arquivo somente leitura (`docs/modules/rocketleague.md`).
- **EA SPORTS FC 25/26:** `fcsetup.ini` regravado pelo jogo, chaves mudam a cada edição, sem documentação da EA (`docs/modules/eafc.md`).
- **PUBG:** os ajustes de qualidade ficam dentro de um bloco `ScalabilityGroups=(...)`; fica para quando houver amostra real verificada.
- **CS2 `cs2_video.txt`:** o Steam Cloud sincroniza o arquivo e ele pode ser sobrescrito. O `fps_max` continua pelo `autoexec` (laboratório, com o dump do jogo).
