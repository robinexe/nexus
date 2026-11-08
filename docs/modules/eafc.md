# EA SPORTS FC (FC 25 / FC 26) — cartão só com Medir/Comparar

Código: `src/Nexus.GameProfiles/EaFc/EaFcCard.cs` · Catálogo: `catalog/eafc.v1.json` (0 ações, 3 mitos) · Testes:
`tests/Nexus.Tests/Modules/GameCardsSetBTests.cs`. Pesquisa de 08/11/2026.

## O que se sabe do arquivo (e por que não entra)

- Arquivo: `fcsetup.ini`, em `%LOCALAPPDATA%\EA SPORTS FC 25\settings\` (FC 25) e `%LOCALAPPDATA%\FC26\settings\` (FC 26) — ou seja,
  a pasta muda de nome a cada edição. Fonte: relatos no fórum da EA
  (https://forums.ea.com/discussions/fc-25-bug-reports-archive-en/fc-25-pc-refresh-rate-locked-to-60hz/7853548,
  https://forums.ea.com/discussions/fc-26-technical-issues-en/fps-drop-and-file-overwriting/13628037).
- Chaves (FC 25): `WAITFORVSYNC`, `MAX_FRAME_RATE`, `REFRESH_RATE`, `RENDERINGQUALITY`, `MSAA_LEVEL`, `DYNAMIC_RESOLUTION`… (FC 26):
  `WAIT_FOR_VSYNC`, `TARGET_FRAME_RATE`, `RENDERING_QUALITY`, `RENDERING_SCALE`, `GRAPHICS_PRESET`… — **os nomes mudaram entre
  edições** (https://forums.ea.com/discussions/fc-25-technical-issues-en/re-fc-25-not-running-smooth-on-pc-again/7835322 e o
  tópico do FC 26 acima). Existe ainda `overrideAutodetect.lua` ao lado.
- **O jogo regrava o arquivo:** o tópico "Remove the 60 FPS limiter FC 26" manda marcar somente leitura e "conferir antes de cada
  login" porque o arquivo volta ao padrão (https://forums.ea.com/discussions/fc-26-technical-issues-en/remove-the-60-fps-limiter-fc-26/13164666);
  o de "FPS drop and file overwriting" sugere negar permissão de escrita no arquivo.
- A EA não documenta o arquivo; o anticheat é o EA Javelin (kernel). Uma edição que o jogo pode desfazer sozinho não tem
  desfazer confiável nem "estado real" honesto. Decisão: **sem ação de arquivo**; as mesmas opções (V-Sync, limite de quadros,
  taxa de atualização, qualidade) estão no menu Configurações › Vídeo do jogo.

## O que o cartão oferece

Medir FPS / Comparar com e sem e as opções genéricas da tela Jogos (driver NVIDIA por jogo, placa de alto desempenho, jogos em
janela). Executáveis: `FC26.exe`, `FC25.exe`, `FC24.exe` (`GameLibrary` lista "EA SPORTS FC" com `FC25.exe`; o EA app e a
Steam são lidos pelas bibliotecas).

## Falta testar em máquina real

- Confirmar se o FC 26 regrava `fcsetup.ini` **a cada abertura** ou só quando o menu muda (com o jogo fechado, uma edição
  poderia até persistir). Se um dia a EA documentar o arquivo e ele se mostrar estável entre aberturas, as chaves
  `WAIT_FOR_VSYNC=0` e `TARGET_FRAME_RATE=0` (FC 26) seriam candidatas pelo handler `GameConfigValue` (sintaxe `Equals`).
- Conferir que Medir pega `FC26.exe` (o jogo abre direto pelo EA app, sem inicializador separado).
