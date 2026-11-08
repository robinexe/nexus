# Call of Duty (Warzone / Black Ops / Modern Warfare) — cartão em Jogos (só Medir e Comparar)

Código: `src/Nexus.GameProfiles/CallOfDuty/CallOfDutyCard.cs` · Testes: `tests/Nexus.Tests/Modules/GameCardTests.cs` · Pesquisa: 08/11/2026.

## Decisão: o NEXUS não edita o arquivo de opções do Call of Duty

O cartão mostra o jogo (Battle.net pelos "Programas instalados" → `_retail_\cod.exe`; Steam pelo manifesto), com **Medir FPS**,
**Comparar com e sem** e a orientação do que mudar dentro do jogo (`Games.Card.Guide.Cod`). Nenhuma opção escreve no arquivo. Motivos:

1. **O arquivo muda de nome e formato a cada título.** Em `Documentos\Call of Duty\players\` (ou `\<id Steam>\`, `\<id Battle.net>\`):
   `options.3.cod22.cst` (MW2 2022), `options.4.cod23.cst` (MW3/Warzone 2023), `s.1.0.cod24.txt` (BO6/Warzone 2024) e assim por
   diante. Fonte: [COD-Settings-Changer (bradselph) — lista de arquivos por título](https://github.com/bradselph/COD-Settings-Changer).
   Uma lista fechada de caminho fixo (regra do handler) quebraria a cada ano.
2. **Sintaxe própria.** Linhas no formato `Chave:0.0 = "valor" // comentário` (comentário na mesma linha), que o `ConfigLines`
   atual não cobre; sem amostra real verificada do título vigente, o projeto não escreve.
3. **O jogo repõe o valor.** Os guias de "configurações não salvam" mandam marcar o arquivo como **somente leitura** para o jogo não
   sobrescrever; o NEXUS não faz isso (o próprio jogo deixaria de salvar o menu). Fontes:
   [WindowsReport — Warzone settings not saving](https://windowsreport.com/?p=835115),
   [Steam Community — settings reset every time I start the game](https://steamcommunity.com/app/1938090/discussions/0/3959288354317852246).
4. **Ricochet (anticheat de kernel).** Sem posição da Activision autorizando edição por programa. Na dúvida, recusa.

## O que os guias prometem (e por que não entra)

| Pista | Situação |
|---|---|
| `VSync`, limite de FPS, `NVIDIA Reflex` no arquivo | são opções do menu Gráficos (Exibição) e gravam os mesmos valores; vale a orientação |
| `VideoMemoryScale` acima de 0,85–0,9 | pode travar/fechar o jogo por falta de VRAM; nunca recomendado |
| `RendererWorkerCount` | o jogo já escolhe pelo processador; sem medição publicada |

## Falta testar em máquina real

- Nome do arquivo do título atual (`s.1.0.cod25.txt`?) e amostra real para, se um dia houver posição da Activision, escrever um
  parser com teste (comentário na linha, `:0.0` na chave).
- Entrada de "Programas instalados" da Battle.net para o Call of Duty (DisplayName "Call of Duty", InstallLocation) e se o executável
  fica em `_retail_\cod.exe` ou na raiz.
