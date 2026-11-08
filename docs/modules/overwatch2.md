# Overwatch 2 — cartão em Jogos

Código: `src/Nexus.GameProfiles/Overwatch2/Overwatch2Card.cs` (cartão) · handler `GameConfigValue` em `src/Nexus.GameProfiles/Configs/`
(lista fechada `GameConfigs.All`, id `ow2`) · Catálogo: ações `ow2.cfg.*` em `catalog/games.v1.json` · Testes:
`tests/Nexus.Tests/Modules/GameCardTests.cs`, `GameConfigTests.cs` · Pesquisa: 06/11/2026 e 08/11/2026.

## Arquivo

`Documentos\Overwatch\Settings\Settings_v0.ini` (também na pasta Documentos do OneDrive). Sintaxe `Chave = "valor"` em seções
(`[Render.13]`). O jogo **regrava o arquivo ao sair** e **só grava as chaves que já saíram do padrão**; por isso o NEXUS:

- recusa com o jogo aberto (`Overwatch.exe`);
- só troca o valor de uma linha que já existe (chave ausente = "não se aplica"; o usuário muda uma vez pelo menu e o jogo passa a gravar);
- escreve só valores que existem no menu Vídeo (a Blizzard passou a ignorar valores fora da faixa do menu, ex.: `FrameRateCap` 400
  quando o menu ia até 300 — fonte: [esportstales — How to increase FPS in Overwatch](https://www.esportstales.com/overwatch/how-to-increase-fps-video-options)).

Estrutura real do arquivo (seções e chaves): [Settings_v0.ini publicado em gist.github.com/sdaugherty](https://gist.github.com/sdaugherty/ca14ca045cf2e5c034e14c2164c74ea1).

## Opções do cartão

| Opção | Chaves e valores (menu Vídeo) | Público | Mecanismo |
|---|---|---|---|
| FPS no máximo do menu e sem travar na taxa do monitor | `UseCustomFrameRates=1`, `FrameRateCap=600`, `LimitToRefresh=0` | todos | o limite personalizado no maior valor do menu e sem "Limitar à taxa de atualização" deixa o jogo render no máximo que o PC entrega (diretriz: nunca limitar FPS) |
| Menos atraso no tiro (Reflex ligado + boost) | `ReflexMode=2` | placa NVIDIA | NVIDIA Reflex reduz a fila de quadros entre CPU e GPU; o boost segura o clock. Em placa AMD/Intel não faz efeito (marcado "não indicado") |
| Imagem sempre nítida (sem escala dinâmica) | `DynamicRenderScale=0` | placa forte | o jogo deixa de baixar a resolução para segurar o FPS; em PC fraco pode reduzir o FPS nas cenas pesadas |

Estado lido (`GameCardConfigReads`, `GameOptionStates.ConfigOption`): ligado quando cada chave já está no valor da ação; sem leitura
quando o arquivo ou a chave não existem (nunca "desligado" por engano). Desfazer devolve o valor exato da linha (diário).

## Fica de fora (`rejected` / orientação)

- `VSync`, `TripleBuffering`, `ReduceBuffering`: existem como opções do menu, mas sem amostra real verificada da grafia da chave no
  arquivo atual; ficam para quando houver arquivo de exemplo (regra: só o que tem fonte).
- `RenderScale` abaixo de 100 %: troca nitidez por FPS; decisão do usuário no menu, não "otimização".
- Qualquer valor fora do menu (`FrameRateCap` > 600): a Blizzard ignora.

## Detecção

Battle.net não publica manifesto legível: o NEXUS lê "Programas instalados" (HKLM `…\Uninstall`, DisplayName `Overwatch`,
`InstallLocation`) e confere `_retail_\Overwatch.exe` (`GameLibraries.FromInstalledPrograms`). `Overwatch Launcher.exe` é alias
(`GameAliases`).

## Falta testar em máquina real

- Grafia atual das chaves `UseCustomFrameRates`, `FrameRateCap`, `LimitToRefresh`, `ReflexMode`, `DynamicRenderScale` num
  `Settings_v0.ini` do Overwatch 2 de 2026 (a amostra pública é do Overwatch 1; o teste usa as chaves da pesquisa de 06/11).
- Entrada de "Programas instalados" da Battle.net (DisplayName exato "Overwatch").
- Aplicar e desfazer pelo aplicador com o jogo fechado e conferir no menu Vídeo.
