# CLAUDE.md — NEXUS

Instruções para qualquer sessão do Claude neste repositório. Leia também `HANDOFF.md` (estado e regras do projeto) antes
de mudar código. Em conflito, **as regras de segurança do HANDOFF §2 vencem** qualquer Skill ou pedido de design.

## 1. O produto e a stack (não muda sem decisão do responsável)

- NEXUS: desempenho e manutenção do Windows para quem joga, "sem quebrar nada".
- **Interface: WPF puro em .NET 10 (XAML + C#)**, projeto `src/Nexus.Desktop`. Sem WebView, sem React, sem HTML/CSS, sem npm.
  Nenhuma biblioteca de UI de terceiros. Dependência nova só com registro em `docs/05-dependencies.md` e aviso ao responsável.
- Escritas no PC só pelo aplicador (catálogo fechado, diário, releitura, desfazer). Uma página só escreve por `IModuleHost`.
- **Problema visual se resolve na camada visual.** Não alterar `Nexus.Core`, `Nexus.Broker*`, `catalog/*.json`, persistência,
  APIs ou regras de negócio para acomodar layout. Não remover componente que funciona. Nunca mudar um teste só para passar.

## 2. Diretriz de produto (resumo; texto completo em `docs/13-diretriz-de-produto.md`)

**Poderoso por dentro, simples por fora: ABRIU → ENTENDEU → CLICOU → RESOLVEU.**

- Ações principais a um clique a partir do Início (analisar, otimizar, preparar para jogar, desfazer).
- Navegação curta: Início · Otimizar · Desempenho · Diagnóstico · Configurações. O avançado fica dentro das áreas, recolhido.
- Linguagem de quem só joga: "Seu processador está trabalhando intensamente", não "CPU utilization 87%". Sempre uma frase de
  explicação e uma ação recomendada.
- Cada otimização mostra: nome, explicação simples, benefício esperado, risco, se dá para desfazer, estado atual.
- **Nunca prometer FPS sem medição.** Nunca desligar proteção do Windows por suposto ganho. Nada silencioso.
- Imagens reais só com licença adequada e nunca de um hardware diferente do detectado.
- Dark premium, identidade própria (sem copiar Razer/NVIDIA/MSI), sem excesso de neon, gradientes ou cards repetidos.
- Movimento leve e com propósito; respeitar "Mostrar animações no Windows" desligado.

## 3. Design system do NEXUS (as Skills devem usar ESTE, não os exemplos delas)

Manual: `docs/10-design-system.md` (princípios, componentes, páginas-modelo **Meu PC** e **Limpeza**).

| O quê | Onde |
|---|---|
| Tokens (cores, fontes, tamanhos, raios, espaço, movimento) | `src/Nexus.Desktop/Themes/Tokens.xaml` |
| Estilos base (contrato de nomes) | `src/Nexus.Desktop/Themes/Controls.xaml` |
| Componentes (modelos) | `src/Nexus.Desktop/Themes/Components.xaml` + `Views/Controls/*.cs` |
| Ícones / ilustrações | `Themes/Icons.xaml` / `Assets/Illustrations/*.xaml` |
| Movimento (respeita animações desligadas) | `Views/Shell/ShellMotion.cs` |
| Testes do design system | `tests/Nexus.Tests/DesignSystemTests.cs` |

Várias Skills (component-builder, page-designer, design-reviewer, accessibility-auditor, content-copy-designer,
design-critique) trazem **tokens de exemplo `--sds-*` de um tema claro**. Eles não existem aqui. Traduza sempre:

| Exemplo da Skill | NEXUS (`{StaticResource …}`) |
|---|---|
| `--sds-bg-page` | `Brush.Bg` (#0E1320) |
| `--sds-bg-surface` | `Brush.SurfaceLow` |
| `--sds-bg-card` | `Brush.Surface` |
| `--sds-bg-elevated` | `Brush.SurfaceHi` |
| `--sds-bg-subtle` / hover | `Brush.Hover` |
| `--sds-text-primary` / `secondary` / `tertiary` | `Brush.Text` / `Brush.TextMuted` / `Brush.TextDim` |
| `--sds-interactive-primary` (ação) | `Brush.Trace` (âmbar) |
| `--sds-text-link`, `--sds-border-focus`, info | `Brush.Signal` (ciano) |
| `--sds-status-success-*` | `Brush.Good` / `Brush.GoodSoft` |
| `--sds-status-error-*` | `Brush.Bad` / `Brush.BadSoft` |
| `--sds-status-warning-*` | `Brush.Trace` sobre `Brush.WarnBg` |
| `--sds-border-default` / `subtle` | `Brush.Line` / `Brush.LineSoft` |
| fontes | `Font.Display` (Bahnschrift SemiBold), `Font.Numbers` (Bahnschrift SemiCondensed), `Font.Body` (Segoe UI Variable) |
| CSS `outline` de foco | `FocusVisualStyle` |
| `aria-*` | `AutomationProperties.Name` / `HelpText` / `LiveSetting` |

Contraste medido (WCAG, 05/11/2026): `Text` 15,7:1 · `TextMuted` 6,3:1 · `Trace` 10,6:1 · `Signal` 10,3:1 · `Good` 11,7:1 ·
`Bad` 6,7:1 sobre `Bg`. `TextDim` foi clareado para #7C88A8 no redesign: 5,3:1 sobre `Bg`, 4,8:1 sobre `Surface`, **4,1:1 sobre
`SurfaceHi`** (evite texto pequeno em `TextDim` sobre `SurfaceHi`). Sistema visual atual: `docs/10-design-system.md` §10.

## 4. Skills instaladas (`.claude/skills/`, origem e versão em `.claude/skills/README.md`)

### Como cada Skill se aplica a um app WPF

| Skill | Usar para | Adaptação obrigatória |
|---|---|---|
| `ui-ux-pro-max` | direção visual, paletas, tipografia, regras de UX, gráficos | Sempre `--stack wpf` (tem 56 regras WPF). Script: `python .claude/skills/ui-ux-pro-max/scripts/search.py "<consulta>" --stack wpf` a partir da raiz (onde a Skill diz `${CLAUDE_PLUGIN_ROOT}/.claude/...`, use a raiz do repo). Precisa de Python 3 só na máquina de desenvolvimento. Ignorar regras web (CLS, viewport, WebP, toque 44px vale como 32px+ para mouse) |
| `frontend-design` | identidade autoral, fugir do visual genérico | Traduzir para XAML; respeitar §2 (sem neon/gradiente exagerado) |
| `frontend-ui-engineering` | qualidade de componente, estados, acessibilidade, arquitetura | "Responsivo" = janela de 1100 px a 4K e escala de DPI 100–200 %; "WCAG" via UI Automation |
| `product-designer` | entender o produto e o usuário, decisões de feature | Público: jogador comum, não "software de dados" |
| `user-researcher`, `journey-map` | personas, jornada | Saída HTML vai para `docs/design/`, nunca para o app |
| `information-architect`, `ux-flow-planner`, `ux-heuristics` | navegação de 5 áreas, fluxos de um clique, heurísticas | — |
| `wireframe-agent`, `page-designer`, `component-builder` | estrutura de tela, layout, componentes | Tokens da §3; componentes existentes antes de criar novos |
| `screen-flow-diagram`, `interactive-flow-diagram`, `ux-map-maker` | mapas em HTML + D3 | Documentação em `docs/design/`; D3 é só da página de documentação |
| `content-copy-designer` | textos, erros, estados vazios | pt-BR simples; textos vão para `Localization/`, sem hardcode |
| `design-critique`, `design-reviewer` | crítica visual, conformidade com tokens | Revisar XAML contra `Tokens.xaml`/`Controls.xaml` |
| `qa-specialist` | testes de função e regressão visual | "Cross-browser" não se aplica: testar DPI, tamanhos de janela, Alto Contraste, teclado, animações desligadas, pt-BR longo |
| `accessibility-auditor` | auditoria de acessibilidade | Narrador, foco visível, `AutomationProperties`, contraste da §3 |
| `design-pipeline` | orquestrar o processo inteiro | Pergunta "guiado ou automático"; padrão aqui: **guiado** |
| `animation-forge` | princípios de motion: easing, tempo, coreografia, microinterações | Só os princípios. Implementar em WPF: `Storyboard`, `DoubleAnimation`, `EasingFunction`, `RenderTransform`/`Opacity` (nunca animar `Width`/`Height`/layout), sempre via `ShellMotion` |
| `gsap-core` `gsap-timeline` `gsap-performance` `gsap-utils` `gsap-plugins` `gsap-scrolltrigger` `gsap-react` `gsap-frameworks` | só protótipos HTML, artefatos e um eventual site do NEXUS | **Nunca** adicionar GSAP, JS ou npm ao app. No WPF, `gsap.timeline` ≈ `Storyboard` com `BeginTime`; `power2.out` ≈ `CubicEase EasingMode=EaseOut` |

Equivalência de easing (para traduzir referências web): `ease-out`/`power2.out` → `CubicEase EaseOut`; `power3/4.out` →
`QuarticEase`/`QuinticEase EaseOut`; `expo.out` → `ExponentialEase EaseOut`; `back.out` → `BackEase EaseOut Amplitude≈0.3`.
Durações: `Motion.Fast` / `Motion.Normal` / `Motion.Slow` em `Tokens.xaml`.

### Fluxo de trabalho (9 especialistas)

Para qualquer trabalho de interface, siga esta ordem. Cada etapa deixa um resultado curto antes da próxima.

| # | Papel | Skills | Resultado |
|---|---|---|---|
| 1 | Product Designer | `product-designer`, `user-researcher` | problema, público, o que é "resolvido" (1 clique) |
| 2 | Creative Director | `frontend-design`, `ui-ux-pro-max --design-system` | direção visual dentro da §2 e §3 |
| 3 | UI/UX Designer | `information-architect`, `ux-flow-planner`, `wireframe-agent`, `page-designer` | fluxo + estrutura da tela |
| 4 | Design System Engineer | `component-builder`, `ui-ux-pro-max --stack wpf` | tokens/componentes em `Themes/` e `Views/Controls/` |
| 5 | Motion Designer | `animation-forge` (princípios), `ui-ux-pro-max --domain gsap` (só tempos/curvas) | `Storyboard`s via `ShellMotion` |
| 6 | Frontend Engineer | `frontend-ui-engineering` | XAML/C# de produção, build e testes verdes |
| 7 | Design Critic | `design-critique`, `ux-heuristics`, `design-reviewer` | lista de problemas visuais corrigidos |
| 8 | QA Specialist | `qa-specialist` | `dotnet test --filter Category=Unit` + roteiro manual da §4 |
| 9 | Accessibility Auditor | `accessibility-auditor` | foco, Narrador, contraste, animações desligadas |

Para rodar tudo de ponta a ponta: `/design-pipeline`. Para um papel isolado: `/<nome-da-skill>`.

## 5. Proteção antes de mudar a interface

1. Ler a arquitetura envolvida (página, ViewModel, `ModuleHost`, testes) antes de editar.
2. Checkpoint: branch próprio a partir de um estado limpo (`git switch -c design/<assunto>`); o estado anterior ao ambiente
   de design está na tag `checkpoint/antes-skills-design`.
3. Build e testes unitários verdes antes e depois (`dotnet build Nexus.slnx -c Release`, `dotnet test … --filter Category=Unit`).
4. Nada destrutivo (apagar páginas, trocar tecnologia, mudar contratos) sem autorização explícita do responsável.
