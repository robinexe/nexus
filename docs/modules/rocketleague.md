# Rocket League — cartão só com Medir/Comparar

Código: `src/Nexus.GameProfiles/RocketLeague/RocketLeagueCard.cs` · Catálogo: `catalog/rocketleague.v1.json` (0 ações, 3 mitos) ·
Testes: `tests/Nexus.Tests/Modules/GameCardsSetBTests.cs`. Pesquisa de 08/11/2026.

## Por que não há ajuste de arquivo

- O arquivo que os guias editam é `Documentos\My Games\Rocket League\TAGame\Config\TASystemSettings.ini` (seção
  `[SystemSettings]`: sombras, bloom, LOD, `TEXTUREGROUP_*`, `AllowPerFrameSleep`…). **O jogo regrava o arquivo ao abrir**, por
  isso todos os guias mandam marcar o arquivo como somente leitura — e a Epic, no suporte oficial, diz que o arquivo somente
  leitura é a causa de "Rocket League não salva minhas configurações" e manda desmarcar:
  https://www.epicgames.com/help/rocket-league-c-202300000001622/technical-support-c-202300000001679/what-do-i-do-if-rocket-league-doesn-t-save-my-settings-a202300000082700
- Guia típico (ESL/esportstales), que confirma o regravar e a exigência de somente leitura:
  https://esportstales.com/rocket-league/how-to-increase-fps-tasystemsettings-video-options
- O NEXUS nunca marca arquivo como somente leitura nem edita um arquivo que o jogo vai sobrescrever (decisão já registrada em
  `jogos-config.md`, 06/11). As opções de desempenho ficam no menu Vídeo do jogo: **FPS máximo** (até 250), **V-Sync** (desligar),
  qualidade "Desempenho". O limitador acima de 250 via `AllowPerFrameSleep` não tem fonte da Psyonix/Epic.
- Desde 2026 o jogo usa Easy Anti-Cheat (`RocketLeague_BE.exe`/EAC): mais um motivo para não tocar em arquivos fora do menu.

## O que o cartão oferece

Medir FPS / Comparar com e sem (laboratório A/B) e as opções genéricas da tela Jogos (perfil do driver NVIDIA por jogo, placa
de alto desempenho em notebook, otimizações para jogos em janela). Executáveis: `RocketLeague.exe` (desenha),
`RocketLeague_BE.exe` (inicializador; `GameAliases` já mapeia).

## Falta testar em máquina real

- Nada a aplicar; só conferir que o cartão aparece quando o jogo é achado pela Steam/Epic e que Medir pega o `RocketLeague.exe`.
- Se um dia a Psyonix documentar um arquivo que o jogo **não** regrava ao abrir (ex.: `TASettings.ini` para a câmera não serve),
  reavaliar.
