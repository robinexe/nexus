# Minecraft (Java e Bedrock) — cartão e ajustes de arquivo

Código: `src/Nexus.GameProfiles/Minecraft/` (`MinecraftSettings.cs` lista fechada, `MinecraftCard.cs` cartão + leitor de estado) ·
Handler: `GameConfigValue` (`src/Nexus.GameProfiles/Configs/`, ver `docs/modules/jogos-config.md`) · Catálogo: `catalog/games.v1.json`
(6 ações do Java, de 06/11) + `catalog/minecraft.v1.json` (2 do Java + 1 do Bedrock, 2 mitos) · Testes:
`tests/Nexus.Tests/Modules/GameCardsSetBTests.cs`, `GameConfigTests.cs`. Pesquisa de 08/11/2026.

## O que o cartão oferece

| Opção | Ações | Para quem | Arquivo |
|---|---|---|---|
| FPS sem limite (Java) | `enableVsync:false` + `maxFps:260` (260 = "Ilimitado" no controle) | todos | `%APPDATA%\.minecraft\options.txt` |
| Gráficos leves (Java) | `graphicsMode:0` (Rápido), `renderClouds:false`, `particles:2` (Mínimo), `entityShadows:false` | PC fraco, notebook | idem |
| Mundo mais perto (Java) | `renderDistance:8`, `simulationDistance:6` (padrão 12/12; menu 2–32 e 5–32) | PC fraco, pouca RAM | idem |
| V-Sync desligado (Bedrock) | `gfx_vsync:0` | todos | `%LOCALAPPDATA%\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang\minecraftpe\options.txt` |

Regras do handler: só troca o **valor** de uma linha que o jogo já gravou (sintaxe `chave:valor`), com o jogo fechado
(`javaw`, `Minecraft`, `MinecraftLauncher` para o Java; `Minecraft.Windows` para o Bedrock), escrita atômica com hash,
desfazer devolve o valor exato. Versão sem a chave (ex.: `simulationDistance` só existe desde a 1.18) → "não se aplica",
nada é acrescentado. Edição não instalada → "não se aplica".

**Estado real** (`MinecraftCardState`): lê o mesmo arquivo com o mesmo parser; a opção aparece "ligada" só quando TODAS as
chaves dela estão no valor da ação. Arquivo ausente = sem leitura (nunca "desligado").

## Fontes

- Chaves e valores do `options.txt` do Java (`maxFps` 10–260 com 260 = ilimitado; `renderDistance` 2–32; `simulationDistance`
  5–32; `graphicsMode` 0 rápido/1 detalhado/2 fabuloso; `particles` 0 todas/1 reduzidas/2 mínimas): https://minecraft.wiki/w/Options.txt
  (mesma fonte das 6 ações de 06/11 em `games.v1.json`).
- Caminho e chave `gfx_vsync` do Bedrock: moderador da Mojang orienta "disabling V-Sync (gfx_vsync) in options.txt" em
  https://bugs.mojang.com/browse/MCPE-229062; caminho `%userprofile%\AppData\Local\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang\minecraftpe`
  em https://bugs.mojang.com/browse/MCPE-166745 (teto de 2× a taxa do monitor em notebook híbrido, fechado como "Won't Fix" em
  04/2025 — por isso o risco está escrito na ação).
- Detecção: `GameAliases` (`MinecraftLauncher.exe → javaw.exe / Minecraft.Windows.exe`) e `GameLibrary` ("Minecraft (Bedrock)").

## Fora (em `rejected`, com fonte)

- **`-Xmx` (mais memória para o Java) em `launcher_profiles.json`:** o lançador regrava o JSON, o padrão `-Xmx2G` é o da Mojang
  para o jogo sem mods e não há medição de ganho de FPS; modpacks ajustam no próprio lançador (Instalações › Argumentos JVM). Uma
  "regra honesta pela RAM" exigiria parser JSON, lidar com várias instalações/perfis e com o lançador sobrescrevendo — não
  compensa o risco por um ganho sem fonte.
- **`gfx_max_framerate` do Bedrock:** relatos no rastreador da Mojang (MCPE-110384) mostram o valor ignorado; o menu já tem
  "Ilimitado". Sem mecanismo confirmado.
- Mods de desempenho (Sodium, OptiFine), JVM flags "mágicas" e caches: fora do escopo do NEXUS (não instala nada no jogo).

## Falta testar em máquina real

- Bedrock: confirmar que o `options.txt` do pacote da Store é gravado em `chave:valor` sem aspas (amostra pública confirma `gfx_vsync:1`)
  e que o jogo lê o valor ao abrir sem "perder" a troca; conferir o processo `Minecraft.Windows.exe` bloqueando a edição.
- Java: `renderDistance`/`simulationDistance` em uma versão 1.21 real (valores gravados sem casas decimais); lançadores
  alternativos (Prism, CurseForge) usam outras pastas e não são cobertos (não se aplica, sem erro).
- Minecraft Java instalado via Microsoft Store (lançador unificado) usa o mesmo `%APPDATA%\.minecraft` — confirmar.
