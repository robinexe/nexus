# Roblox — cartão e ajuste de arquivo

Código: `src/Nexus.GameProfiles/Roblox/` (`RobloxSettings.cs`, `RobloxCard.cs`) · Handler: `GameConfigValue` com a sintaxe
nova `XmlNamed` (`Configs/ConfigLines.cs`) · Catálogo: `catalog/roblox.v1.json` (1 ação, 3 mitos) · Testes:
`tests/Nexus.Tests/Modules/GameCardsSetBTests.cs`. Pesquisa de 08/11/2026.

## O que o cartão oferece

| Opção | Ação | Arquivo |
|---|---|---|
| FPS máximo 240 | `<int name="FramerateCap">240</int>` (menu Configurações › Maximum Frame Rate: Padrão/60, 120, 144, 240) | `%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml` |

É a opção que a própria Roblox recomenda em vez de "FPS unlockers". O NEXUS só troca o número do elemento (uma linha), com o
Roblox fechado (`RobloxPlayerBeta`, `RobloxPlayerLauncher`, `RobloxCrashHandler`), e desfaz devolvendo o valor exato. O
parser `XmlNamed` só reconhece um elemento inteiro por linha (`<tipo name="Chave">valor</tipo>`), recusa valor com `<`, `>`
ou `&` e não mexe em blocos (`<Item>`, `<Properties>`): o arquivo continua XML válido (teste com `XDocument.Parse`).
Nunca limita FPS: o único valor gravável é 240 (o máximo do menu).

**Estado real** (`RobloxCardState`): ligado quando `FramerateCap` = 240; arquivo ausente = sem leitura.

## Posição oficial da Roblox sobre Fast Flags (por que não há mais opções)

- 30/05/2024 — "Introducing the Maximum Framerate setting": opção no menu (60/120/144/240, só Windows), com a frase "We
  recommend using the in-game setting instead of using an external unlocker" e o aviso de que a flag interna "was never meant
  to be a stable public API and may change without prior notice". https://devforum.roblox.com/t/introducing-the-maximum-framerate-setting/2995965
- 29/09/2025 — "Allowlist for local client configuration via Fast Flags": `ClientAppSettings.json` passa a aceitar só uma lista
  fechada (texturas, MSAA, distância de LOD, API gráfica, DPI, cinza do céu…); **todas as outras são ignoradas**, incluindo
  `DFIntTaskSchedulerTargetFps`. https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569
- Logo, Bloxstrap/Fishstrap/"FFlags de FPS" são ineficazes ou instáveis; e as flags permitidas são de qualidade visual, sem
  medição publicada de ganho. Ficam em `rejected`.
- Relato de que valores acima de 240 voltam a -1 e o jogo trava em 240 de qualquer jeito:
  https://devforum.roblox.com/t/roblox-caps-fps-at-240-regardless-of-globalbasicsettings13xml-framerate-cap/4451048

## Detecção

`GameLibrary` ("Roblox", `RobloxPlayerBeta.exe`). O Roblox instala em `%LOCALAPPDATA%\Roblox\Versions\version-xxxx\RobloxPlayerBeta.exe`.
A versão da Microsoft Store (`ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr`) guarda o XML em `Packages\...\LocalState` e **não** é
coberta (não se aplica, sem erro).

## Falta testar em máquina real

- Confirmar o nome exato do elemento (`FramerateCap`, tipo `int`) e o valor gravado para "Padrão" (relato aponta -1) num
  `GlobalBasicSettings_13.xml` real; confirmar que o Roblox lê 240 do arquivo ao abrir (o menu mostra 240).
- Confirmar que o arquivo é gravado uma linha por elemento (como as amostras públicas) e em UTF-8 sem BOM.
- Processo `RobloxPlayerBeta.exe` aberto → edição recusada; fechado → aplicada e desfeita.
