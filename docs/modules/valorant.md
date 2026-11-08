# VALORANT — cartão em Jogos (só Medir e Comparar)

Código: `src/Nexus.GameProfiles/Valorant/ValorantCard.cs` · Testes: `tests/Nexus.Tests/Modules/GameCardTests.cs` · Pesquisa: 08/11/2026.

## Decisão: o NEXUS não edita arquivo do VALORANT

O cartão mostra o jogo (detectado pela Riot em `%ProgramData%\Riot Games\Metadata`, executável `VALORANT-Win64-Shipping.exe`;
`VALORANT.exe` é só o inicializador), com **Medir FPS**, **Comparar com e sem** (opções do driver NVIDIA e de jogos em janela) e a
orientação do que mudar dentro do jogo (`Simple.Games.Guide.Valorant`). Nenhuma opção escreve no arquivo do jogo. Motivos:

1. **As configurações ficam na conta Riot.** A própria mensagem de erro do jogo, documentada pela Riot, é *"Error retrieving server
   settings, changes to settings will not be saved on the server"*: as preferências sincronizam com o servidor e uma edição local pode
   ser sobrescrita no próximo login. Fonte: [Riot Support — How to fix "Error retrieving settings from server"](https://support.riotgames.com/en-us/valorant/support-tools/how-to-fix-error-retrieving-settings-from-server)
   (indica a pasta `%LOCALAPPDATA%\VALORANT\Saved\Config\` e manda apagar os arquivos, que o cliente recria ao logar).
2. **Vanguard (anticheat de kernel).** A Riot não publica posição autorizando programas de terceiros a alterar os arquivos de
   configuração; o FAQ do Vanguard diz que "não existe lista de permissão" para ferramentas. Fonte: [Riot Games — Vanguard FAQ (DevRel)](https://www.riotgames.com/en/DevRel/vanguard-faq).
   Regra do projeto: na dúvida, recusa.
3. **Caminho por conta.** O arquivo fica em `%LOCALAPPDATA%\VALORANT\Saved\Config\<id da conta>\Windows\GameUserSettings.ini`; com mais de
   uma conta no PC não há como escolher sem o usuário.

## O que os guias prometem (e por que não entra)

| Pista | Situação |
|---|---|
| `FrameRateLimit`, `bUseVSync`, `sg.*` no `GameUserSettings.ini` | chaves reais do Unreal, mas sujeitas ao item 1 (servidor) e ao item 2; o menu Vídeo do jogo grava exatamente os mesmos valores — oriente o usuário a usar o menu |
| `Engine.ini` (`r.Streaming.PoolSize`, `r.GTSyncType`, `r.FinishCurrentFrame`…) | sem medição publicada; a Riot pode repor o arquivo em cada patch. Mito para o NEXUS, igual a `myth.fortnite.engine-ini-tweaks` |
| "Raw Input Buffer" / Reflex | são opções do menu (Geral › Raw Input Buffer; Vídeo › NVIDIA Reflex Low Latency) e sincronizam na conta |
| `bSmoothFrameRate`, `MaxFPS`/`MinFPS` | sem efeito no VALORANT (relato da comunidade, não medido) |

## O que o cartão faz

- Detecção: `GameLibraries.RiotGames` (yaml da Riot) → `VALORANT-Win64-Shipping.exe`; alias `VALORANT.exe` em `GameAliases`.
- Estado: "Instalado · aberto agora · última partida medida: N FPS" (medição do próprio NEXUS; nunca FPS prometido).
- Opções que valem: driver NVIDIA por jogo (menos atraso, placa no máximo, FPS sem limite) e jogos em janela — as mesmas de qualquer
  jogo da biblioteca, aplicadas ao executável que desenha.

## Falta testar em máquina real

- Confirmar que o `product_settings.yaml` da Riot ainda aponta `ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe` na versão atual.
- Observar se a Riot publica orientação oficial sobre edição do `GameUserSettings.ini`; se um dia autorizar, o handler
  `GameConfigValue` já serve (sintaxe `Equals`, raiz `AppData\Local` precisaria entrar em `ConfigRoot`).
