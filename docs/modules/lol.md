# League of Legends — cartão em Jogos (só Medir e Comparar)

Código: `src/Nexus.GameProfiles/LeagueOfLegends/LeagueCard.cs` · Testes: `tests/Nexus.Tests/Modules/GameCardTests.cs` · Pesquisa: 08/11/2026.

## Decisão: o NEXUS não edita arquivo do League of Legends

O cartão mostra o jogo (detectado pela Riot em `%ProgramData%\Riot Games\Metadata`, executável `League of Legends.exe`;
`LeagueClient.exe`/`LeagueClientUx.exe` são o cliente), com **Medir FPS**, **Comparar com e sem** e a orientação do que mudar dentro do
jogo (`Games.Card.Guide.Lol`). Nenhuma opção escreve no arquivo do jogo. Motivos:

1. **O arquivo fica na pasta do jogo, fora do perfil.** `game.cfg` (vídeo: `[General] FrameCapType`, `WindowMode`, `[Performance]
   ShadowQuality`…) e `PersistedSettings.json` ficam em `C:\Riot Games\League of Legends\Config` (ou onde o jogo foi instalado). Fonte:
   [Riot Support — Resetting your configuration files](https://support.riotgames.com/en-us/riot/performance/reset-configuration-files).
   O handler `GameConfigValue` escreve só dentro da pasta do perfil (ProfileList pelo SID, sem seguir links): o caminho não é coberto
   pelas regras de segurança atuais.
2. **Preferências sincronizadas na conta.** `PersistedSettings.json` guarda as preferências da conta Riot e é reposto pelo cliente; o
   artigo acima diz que arquivos "reaparecem" ao abrir o cliente ou logar.
3. **Vanguard (anticheat de kernel, desde 2024).** Sem posição da Riot autorizando edição por programa
   ([Riot Vanguard FAQ — League of Legends](https://support-leagueoflegends.riotgames.com/hc/articles/24169857932435),
   [Vanguard FAQ (DevRel)](https://www.riotgames.com/en/DevRel/vanguard-faq)). Regra do projeto: na dúvida, recusa.

## O que os guias prometem (e por que não entra)

| Pista | Situação |
|---|---|
| `FrameCapType` / `FrameCapFPS` em `game.cfg` | chaves reais, mas o menu Vídeo › Limite de quadros grava os mesmos valores; vale a orientação |
| "janela sem bordas" por arquivo | `WindowMode` do mesmo arquivo; menu Vídeo › Modo de janela |
| `-dx9`/`-useLegacyLauncher` e `.bat` de inicialização | fora do escopo: modificam a forma de iniciar o jogo |

## Falta testar em máquina real

- Confirmar o caminho `Game\League of Legends.exe` no `product_settings.yaml` atual.
- Se a Riot documentar oficialmente a edição de `game.cfg`, seria preciso um `ConfigRoot` novo (pasta de instalação lida do yaml da
  Riot em `%ProgramData%`) com as mesmas garantias (sem links, caminho fixo, jogo fechado).
