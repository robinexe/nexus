# Grand Theft Auto V (Legacy e Enhanced) — cartão e ajustes de arquivo

Código: `src/Nexus.GameProfiles/GtaV/` (`GtaVSettings.cs`, `GtaVCard.cs`) · Handler: `GameConfigValue` com a sintaxe nova
`XmlAttribute` (`Configs/ConfigLines.cs`) · Catálogo: `catalog/gtav.v1.json` (4 ações, 3 mitos) · Testes:
`tests/Nexus.Tests/Modules/GameCardsSetBTests.cs`. Pesquisa de 08/11/2026.

## O que o cartão oferece

| Opção | Chave (seção `<video>`) | Menu do jogo | Arquivos |
|---|---|---|---|
| VSync desligado | `<VSync value="0" />` | Configurações › Gráficos › VSync: Desligado | `Documentos\Rockstar Games\GTA V\settings.xml` (Legacy) e `Documentos\Rockstar Games\GTAV Enhanced\settings.xml` (Enhanced) |
| Continuar rodando ao alternar de janela | `<PauseOnFocusLoss value="0" />` | Gráficos › Pausar jogo ao perder o foco: Desligado | idem |

Cada opção do cartão aplica a mesma chave nas duas edições (`gtav` e `gtav-enhanced`): a edição não instalada responde
"não se aplica" e nada muda. O GTA V **não tem limitador de FPS** no arquivo (não existe `FrameLimit`): a única trava é o
VSync. Jogo fechado (`GTA5`, `GTA5_Enhanced`, `PlayGTAV`, `GTAVLauncher`; o Rockstar Games Launcher em si não bloqueia),
escrita atômica, desfazer exato. O parser `XmlAttribute` só reconhece `<Chave value="…" />` inteiro numa linha e recusa valor
com `<`, `>`, `&` ou aspas; o arquivo continua XML válido (teste com `XDocument.Parse`).

**Estado real** (`GtaVCardState`): ligado quando todas as edições **instaladas** estão em 0; nenhuma instalada = sem leitura.

## Fontes

- Estrutura do `settings.xml` (raiz `<Settings>`, `<version value="27"/>`, seção `<video>` com `VSync`, `PauseOnFocusLoss`,
  `Windowed`, `RefreshRate`; sem `FrameLimit`): arquivo real publicado em https://pastebin.com/8JvbMYp2 (nível "Observed").
- Pastas: `Documents\Rockstar Games\GTA V` (Legacy) e `Documents\Rockstar Games\GTAV Enhanced` (Enhanced):
  https://www.minitool.com/news/grand-theft-auto-v-enhanced-save-file-location.html (comunidade; falta confirmar o
  `settings.xml` na raiz da pasta Enhanced em máquina real).
- Documentos redirecionado ao OneDrive é procurado também (`GameConfigs.RootCandidates`).
- Detecção: `GameAliases` (`PlayGTAV.exe → GTA5.exe / GTA5_Enhanced.exe`), `GameLibrary` ("GTA V", "GTA V Enhanced"); Steam,
  Epic e Rockstar Games Launcher pelas bibliotecas.

## Fora (em `rejected`, com fonte)

- `gameconfig.xml` "otimizado" (arquivo do jogo dentro do `update.rpf`; exige mods; integridade/BattlEye).
- `commandline.txt` (`-DX10`, `-ignoreDifferentVideoCard`…): pasta de instalação, argumentos para contornar problemas, não para FPS.
- `settings.xml` somente leitura: impede o próprio jogo de salvar (o clássico "GTA V não salva as configurações").
- Outras chaves de `<graphics>` (sombras, MSAA, tesselação…) têm valores de menu, mas sem mapa valor→opção confirmado por fonte;
  ficam para quando houver uma amostra real verificada (como o PUBG em `jogos-config.md`).

## Falta testar em máquina real

- **Enhanced:** confirmar que `settings.xml` existe em `Documentos\Rockstar Games\GTAV Enhanced\` (e não dentro de `Profiles\<id>`)
  e que a seção `<video>` tem as mesmas chaves (`VSync`, `PauseOnFocusLoss`).
- Confirmar o valor gravado pelo menu para VSync "Desligado" = 0 nas duas edições (a amostra pública é de um Legacy).
- Jogo aberto → recusa; fechado → aplica, o menu mostra o novo valor, desfazer devolve o byte a byte (CRLF e UTF-8 mantidos).
- BattlEye (GTA Online, Enhanced): a edição é no perfil do usuário, não em arquivo do jogo; confirmar que nada é sinalizado.
