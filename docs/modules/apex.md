# Apex Legends — cartão em Jogos

Código: `src/Nexus.GameProfiles/Apex/ApexCard.cs` (cartão) · handler `GameConfigValue` em `src/Nexus.GameProfiles/Configs/` (lista
fechada `GameConfigs.All`, id `apex`) · Catálogo: ações `apex.cfg.*` em `catalog/games.v1.json` · Testes:
`tests/Nexus.Tests/Modules/GameCardTests.cs`, `GameConfigTests.cs` · Pesquisa: 06/11/2026 e 08/11/2026.

## Arquivo

`Jogos Salvos\Respawn\Apex\local\videoconfig.txt`: KeyValues da Respawn (`"setting.chave"  "valor"`, com tabulações). O jogo
regrava ao sair; o NEXUS recusa com `r5apex.exe`/`r5apex_dx12.exe` abertos, troca só o valor de linhas existentes e devolve o valor
exato no desfazer. O arquivo não é sincronizado na nuvem (pasta `local`).

## Opções do cartão

| Opção | Chaves (valores do menu Vídeo) | Público | Mecanismo |
|---|---|---|---|
| Sem V-Sync do jogo | `setting.mat_vsync_mode=0` | competitivo | o quadro vai para a tela assim que fica pronto: menos atraso; pode rasgar sem VRR |
| Efeitos leves | `setting.ssao_enabled=0`, `setting.volumetric_lighting=0`, `setting.mat_depthfeather_enable=0`, `setting.csm_enabled=0` | placa fraca | oclusão de ambiente, luz volumétrica, bordas suaves e sombras do sol são os efeitos mais caros do menu; desligados aliviam a placa |

Fontes: menu Vídeo do próprio jogo (cada chave corresponde a uma opção do menu e o NEXUS só grava o valor que o menu gravaria);
[esportstales — How to increase FPS in Apex Legends: videoconfig, settings, launch options](https://www.esportstales.com/apex-legends/how-to-increase-fps-videoconfig-settings-launch-options)
(caminho `%USERPROFILE%\Saved Games\Respawn\Apex\local\videoconfig.txt`, chaves `setting.mat_depthfeather_enable` e
`setting.csm_enabled`; o guia manda marcar somente leitura porque usa valores FORA do menu, que o jogo repõe ao abrir — o NEXUS
só grava valores do menu e por isso não precisa disso). Sem medição publicada de FPS: o ganho se confere em "Comparar com e sem".

## Fica de fora (`rejected`)

- **Opções de lançamento da Steam/EA app** (`+fps_max 0`, `-novid`, `-dev`, `+cl_showfps`…): são da biblioteca da loja
  (`localconfig.vdf` da Steam / configuração do EA app), não do jogo; alterar esses arquivos mexe em dados da loja e `-dev`/
  `-novid` só tiram vídeos de abertura. O limite de FPS do jogo é o do menu (ou sem limite); o NEXUS nunca limita FPS.
- **`videoconfig.txt` somente leitura** (para o jogo não repor valores fora do menu): o NEXUS não marca arquivos como somente leitura
  (o próprio jogo pararia de salvar o menu). Só grava valores do menu, que o jogo respeita.
- Valores fora do menu (`setting.r_lod_switch_scale`, `mat_picmip` > 1 etc.): sem fonte de medição; alguns são tratados como
  vantagem indevida.

## Detecção

Steam: manifesto (`appmanifest`) → maior executável da pasta (`r5apex.exe`). EA app: "Programas instalados" (DisplayName
`Apex Legends`, `InstallLocation`) → `r5apex.exe`/`r5apex_dx12.exe` (`GameLibraries.FromInstalledPrograms`). Alias
`r5apex_launcher.exe` (`GameAliases`).

## Falta testar em máquina real

- Amostra atual do `videoconfig.txt` (2026) para confirmar a grafia das 5 chaves (o teste usa um exemplo reconstruído da pesquisa).
- Entrada de "Programas instalados" do EA app (DisplayName exato e pasta).
- Aplicar e desfazer pelo aplicador com o jogo fechado e conferir no menu Vídeo.
