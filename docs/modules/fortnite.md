# Fortnite (catalog/fortnite.v1.json)

Pesquisa de 03/11/2026 em páginas da Epic, no código do Unreal Engine, em medições publicadas, no r/FortniteCompetitive e no
r/FortNiteBR, e em ferramentas do GitHub. Regra: só entra o que tem mecanismo e fonte, e o ganho é dito com honestidade.
Tudo passa pelo aplicador, com diário e desfazer.

## O que faz

O NEXUS muda **só chaves do `GameUserSettings.ini` do seu perfil**:
`%LOCALAPPDATA%\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini`. É o arquivo que o próprio menu de vídeo grava,
e a Epic diz que é o único feito para ser mudado (patch 1.7.2, 2017, Michael Noland: "Only the settings in GameUserSettings.ini
(resolution, window mode, etc...) are meant to be modifiable").

| id | título | classe | o que muda |
|---|---|---|---|
| `fortnite.render.rhi.dx12` | Renderizador DirectX 12 (base dos dois modos atuais) | SupportedDocumented | `[D3DRHIPreference] PreferredRHI=dx12` |
| `fortnite.render.performance-mode` | Modo desempenho (mais FPS em PC fraco) | Conditional | `[D3DRHIPreference] PreferredFeatureLevel=es31` |
| `fortnite.render.dx12-full` | DirectX 12 completo com pré-compilação de sombreadores (placas fortes) | Conditional | `PreferredFeatureLevel=sm6` |
| `fortnite.fps.limit` | Limite de FPS (3 abaixo da taxa do monitor) | Conditional | `FrameRateLimit=141.000000` (lista fechada) |
| `fortnite.latency.reflex` | NVIDIA Reflex | Conditional | `LatencyTweak2=0/1/2` (desligado/ligado/ligado + boost) |
| `fortnite.display.vsync.off` | V-Sync do jogo desligado | Conditional | `bUseVSync=False` |
| `fortnite.visual.motion-blur.off` | Sem desfoque de movimento | SupportedDocumented | `bMotionBlur=False` |
| `fortnite.visual.shadows.off` | Sombras desligadas (maior ganho no DX12) | Conditional | `[ScalabilityGroups] sg.ShadowQuality=0` |
| `fortnite.visual.grass.off` | Sem grama (chave escondida, efeito não comprovado) | Experimental | `bShowGrass=False` |

Os dois modos dependem de `fortnite.render.rhi.dx12` (`dependsOn`) e conflitam entre si (`conflictsWith`); a tela sempre manda
o par.

### Onde aparece

- **Jogos (modo simples)**: no cartão do Fortnite (aparece quando o manifesto da Epic tem o jogo), três opções de um clique
  com "Feito ✓ — Desfazer": *Mais FPS em PC fraco (Modo desempenho)*, *Menos atraso (Reflex + limite de N FPS)* — o limite é
  a taxa do monitor principal menos 3; o Reflex só entra com GPU principal NVIDIA — e *Imagem nítida ao virar (sem desfoque)*.
  Mostra o estado atual ("Agora: Modo desempenho · limite 141 FPS · Reflex ligado") e "Feche o Fortnite antes" (ou "o
  Fortnite está aberto agora").
- **Placa de vídeo › Ajustes por jogo › Fortnite (avançado)**: todas as ações, com escolha de modo, limite e Reflex, estado
  de cada chave, dica pela VRAM da GPU principal.
- **Aplicar / Testar sem aplicar**: as ações aparecem na lista do catálogo, como as demais.

## Como é seguro (handler `FortniteIniValue`, `src/Nexus.GameProfiles/Fortnite`)

- **Lista fechada** (`FortniteSettings.Allowed`): seção + chave exatas e a regra do valor que pode ser escrito. Qualquer outra
  chave, campo extra no alvo ou valor fora da regra é recusado no Assess e de novo no Apply. O caminho do arquivo é fixo e
  nunca vem do catálogo nem do pedido.
- **Perfil certo**: alvo com `"scope": "User"`; o aplicador como serviço anexa o `userSid` de quem pede e o handler lê a pasta
  do perfil em `HKLM\...\ProfileList\<SID>` (como a Limpeza), nunca de variáveis de ambiente. Com outra conta e sem SID: recusa
  sem ler nada. Pastas de rede (`\\servidor`) são recusadas. Links/junções no caminho: recusados (`SafeFiles.EnsureUnderRoot`).
- **Jogo fechado**: o Fortnite regrava o arquivo ao sair, então Assess, Apply e Restore recusam se
  `FortniteClient-Win64-Shipping` (ou as variantes `_EAC`, `_EAC_EOS`, `_BE`, ou `FortniteLauncher`) está aberto. Na dúvida (não
  deu para listar processos), recusa.
- **Sem perdas** (`IniDocument`): cada linha fica como estava (ordem, comentários, linhas desconhecidas, CRLF/LF, linha final);
  só a linha da chave muda. Chave nova entra no fim da sua seção; seção nova vai para o fim do arquivo. Chave repetida, seção
  repetida ou chave com prefixo de lista do Unreal (`+ - . !`) = ambíguo, recusado. Valor com quebra de linha ou caractere de
  controle é recusado (não dá para "injetar" outra chave).
- **Codificação**: UTF-8 com/sem BOM e UTF-16 LE/BE com BOM (o Unreal grava UTF-16 quando há acento, ex.: nome do microfone).
  Bytes que não são UTF-8 válido: recusa em vez de corromper. A escrita mantém a mesma codificação.
- **Escrita atômica com hash**: temporário na mesma pasta + troca, só se o arquivo ainda for o lido (outro programa mexeu:
  recusa). Nunca cria o arquivo (sem ele: "abra o jogo uma vez"). Arquivo somente leitura: recusa e explica como desmarcar.
- **Desfazer**: devolve o valor original exato (byte a byte, testado) ou remove só a linha, se a chave não existia; o executor só
  desfaz se o valor atual ainda é o que o NEXUS escreveu.

## Evidências

- **Chaves e leitura pelo motor** — código do Unreal Engine 5 (`Engine/Source/Runtime/RHI/Private/Windows/WindowsDynamicRHI.cpp`):
  `GConfig->GetString("D3DRHIPreference", "PreferredRHI", ..., GGameUserSettingsIni)` (dx11/dx12) e `PreferredFeatureLevel`
  (`sm5`, `sm6`, `es31`). As demais chaves (`bMotionBlur`, `bShowGrass`, `LatencyTweak2`, `bDisableMouseAcceleration`,
  `FrameRateLimit`, `bUseVSync`) estão em arquivos reais do jogo (2024-2026) e são propriedades `Config` de `UFortGameUserSettings`.
- **DirectX 11 saiu do menu na 40.10 (abril de 2026)** — Epic: https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/gameplay-c-202300000001721/has-the-directx-11-option-been-permanently-removed-from-rendering-mode-in-fortnite-a202300000083887
  Hoje o menu tem "Desempenho" e "DirectX 12", os dois com `PreferredRHI=dx12`.
- **Modo desempenho** — anúncio da Epic (dez/2020): troca qualidade visual por menos memória e menos carga de CPU/GPU, para PCs
  no requisito mínimo; "bem mais de 100%" em gráficos integrados a 720p (https://www.techpowerup.com/275970/).
  Medido pela PC Gamer: Radeon HD 8610G a 720p, 18 → 45 FPS (https://www.pcgamer.com/fortnites-new-performance-mode-gives-a-huge-boost-in-frames-for-low-end-gaming-pcs).
  Contraponto: no r/FortniteCompetitive (https://www.reddit.com/r/FortniteCompetitive/s/UPAScT9vmc) há relatos de mínimos (1%)
  piores no Modo desempenho/DX11 que no DX12 em PCs AMD, e de média maior com mais engasgos. Estimativas sem método publicado
  (ProSettings, 2026) dão Modo desempenho muito acima do DX12 em qualquer placa. Conclusão honesta: grande ganho de média em placa
  fraca; meça os mínimos com o Medir do NEXUS.
- **DirectX 12 e pré-compilação** — Epic, blog técnico do Unreal (fev/2025,
  https://www.unrealengine.com/en-US/tech-blog/game-engines-and-shader-stuttering-unreal-engines-solution-to-the-problem):
  PSO precaching; o Fortnite Battle Royale compila ~30.000 PSOs por partida e usa ~10.000; com o cache do driver vazio a entrada
  na partida demora 20-30 s a mais, e o cache é apagado quando se instala um driver novo. Não existe chave de "pré-compilar":
  é automático no modo DirectX 12. Requisitos dos recursos do UE5 (Nanite, Lumen, sombras virtuais): DirectX 12, recomendado
  RTX 2080 / RX 5700 (https://www.fortnite.com/news/drop-into-the-next-generation-of-fortnite-battle-royale-powered-by-unreal-engine-5-1).
- **Reflex** — NVIDIA: atraso do PC até 46% menor no Battle Royale com RTX 40
  (https://www.nvidia.com/en-us/geforce/news/reflex-low-latency-apex-legends-valorant-fortnite-cs2/). Mapeamento 0/1/2 de
  `LatencyTweak2` vem de arquivos reais e guias (r/FortNiteBR "Ultimate optimization guide": Reflex ligado = 1; perfis "On+Boost"
  = 2); conferir no menu.
- **Limite de FPS** — Blur Busters, G-SYNC 101 (https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/): limitar
  ~3 FPS abaixo da taxa máxima mantém o jogo na faixa do VRR. Valores fora da lista do menu (141, 237) são usados há anos; ver
  "o que conferir".
- **Sombras e desfoque** — PC Gamer (GTX 1060/RX 580, motor antigo): sombras Épico → Baixo ~80% mais FPS; desfoque ligado
  ~4-5% a menos (https://www.pcgamer.com/fortnite-system-requirements-and-settings/). UE5 (2023): sombras Épico ~44%, Alto ~25%
  (r/OptimizedGaming).

## Mitos (não entram, aparecem como "não fazer")

- `myth.fortnite.mouse-acceleration-key`: `bDisableMouseAcceleration=True` não faz nada no Windows. Michael Noland (Epic): a
  chave só alimenta `io.Mac.HighPrecisionDisablesMouseAcceleration`; no Windows o jogo lê o mouse em modo bruto, sem aceleração
  (https://devtrackers.gg/fortnite/p/309f5baf-can-t-turn-off-mouse-acceleration-anymore-since-s8). Por isso a opção pedida
  "Mira consistente (sem aceleração)" **não** foi feita: virou *Imagem nítida ao virar (sem desfoque)*, e o cartão diz que a
  mira já não tem aceleração.
- `myth.fortnite.useallavailablecores`: no código do Unreal, `-USEALLAVAILABLECORES` só muda as threads de compressão de
  arquivos (`Archive.cpp`, `GNumUnusedThreads_SerializeCompressed`), apesar da descrição genérica na documentação de argumentos.
- `myth.fortnite.notexturestreaming`: "Highest quality textures are always loaded" (documentação do Unreal) — enche a VRAM de
  placas de 4-6 GB e leva ao "Out of video memory".
- `myth.fortnite.engine-ini-tweaks`: Engine.ini (r.*, fog, folhagem) foi bloqueado no 1.7.2 e tirar elementos visuais por
  variável é proibido nas regras competitivas. O NEXUS nunca escreve Engine.ini, Scalability.ini, Input.ini, arquivos do jogo,
  .pak ou nada do Easy Anti-Cheat.
- `myth.fortnite.read-only-settings-file`: "travar" o arquivo como somente leitura impede o jogo de salvar o que você muda no
  menu (artigo da Epic sobre o modo de renderização que não salva).
- `myth.fortnite.high-priority-launch-args`: `-high` é de jogos da Valve (o Unreal não lê); `-PREFERREDPROCESSOR` fixa afinidade
  (piora).
- `myth.fortnite.delete-shader-cache`: apagar DXCache/NV_Cache só faz o jogo recompilar (20-30 s a mais, Epic).
- `myth.fortnite.force-dx11` (Obsoleto): saiu do menu na 40.10; `-d3d11` volta ao caminho ES 3.1 antigo.

## Easy Anti-Cheat e regras

O EAC protege o processo e os arquivos do jogo; não há registro de punição por mudar o `GameUserSettings.ini` (é o que o menu
faz). Punições conhecidas vêm de código injetado no jogo. O NEXUS não injeta nada, não abre o processo do jogo e não toca
binários, `.pak`, `Engine.ini` nem argumentos de inicialização.

## O que conferir num PC com Fortnite

1. Jogos (simples): o cartão "Fortnite" aparece, mostra o estado ("Agora: …") e o aviso. Com o jogo aberto, aplicar responde
   "Feche o Fortnite antes…" sem mudar o arquivo.
2. Com o jogo fechado: *Mais FPS em PC fraco* → abrir o jogo → Vídeo › Modo de renderização = **Desempenho** (pede reinício do
   jogo). Desfazer → volta ao modo anterior. Comparar o arquivo antes/depois (só 1-2 linhas diferentes).
3. *Menos atraso*: no menu, Limite de taxa de quadros mostra o valor (ex.: 141) — **conferir se um valor fora da lista do menu
   é aceito e mantido** depois de mudar outra opção e sair; Reflex = "Ligado".
4. Se o arquivo for UTF-16 (acentos em nomes de dispositivos), conferir que continua UTF-16 e o jogo abre normal.
5. Avançado: DX12 completo — primeira partida depois de atualizar o driver demora mais; as seguintes não.
6. Medir FPS antes e depois (Modo desempenho vs DX12) com o Medir do NEXUS, olhando os mínimos.

## Limitações conhecidas

- Se a seção inteira não existia e o NEXUS a criou, desfazer remove a chave mas deixa o cabeçalho `[seção]` vazio (inofensivo).
  Na prática o Fortnite sempre grava as três seções usadas.
- Se você mudar no menu do jogo depois, vale o do menu e o desfazer do NEXUS não sobrescreve (o valor já não é o dele).
- `nexus-recover` (CLI) não inclui handlers de módulos (mesma limitação dos outros módulos); desfazer pela interface.
- Mapeamento de `LatencyTweak2` e aceitação de limites fora da lista do menu: base comunitária, a conferir no PC.
