# Skills de design do NEXUS

Instaladas em 05/11/2026 a partir das fontes abaixo (cópia fiel, sem alteração de conteúdo; só foram removidos `__pycache__`).
Uso e adaptação ao WPF: `CLAUDE.md` §4. Ferramenta de desenvolvimento: nada disto vai para o produto.

| Skill(s) | Fonte | Commit (data) | Licença |
|---|---|---|---|
| `ui-ux-pro-max` | github.com/nextlevelbuilder/ui-ux-pro-max-skill (`.claude/skills/ui-ux-pro-max`) | `ccce51c` (2026-11-04) | MIT |
| `frontend-design` | github.com/anthropics/skills (`skills/frontend-design`) | `ca45fc4` (2026-10-28) | Apache-2.0 (`LICENSE.txt`) |
| `frontend-ui-engineering` | github.com/addyosmani/agent-skills (`skills/frontend-ui-engineering`) | `301b02b` (2026-11-01) | MIT |
| 18 Skills: `accessibility-auditor`, `component-builder`, `content-copy-designer`, `design-critique`, `design-pipeline`, `design-reviewer`, `information-architect`, `interactive-flow-diagram`, `journey-map`, `page-designer`, `product-designer`, `qa-specialist`, `screen-flow-diagram`, `user-researcher`, `ux-flow-planner`, `ux-heuristics`, `ux-map-maker`, `wireframe-agent` | github.com/richhemsley3/claude-design-skills (`skills/`) | `1185d0d` (2026-04-27) | MIT |
| `animation-forge` | github.com/Eskapeum/animation-forge | `ee08fc1` (2026-04-03) | **sem licença no repositório** → só local, fora do git (`.git/info/exclude`) |
| 8 Skills: `gsap-core`, `gsap-timeline`, `gsap-performance`, `gsap-utils`, `gsap-plugins`, `gsap-scrolltrigger`, `gsap-react`, `gsap-frameworks` | github.com/greensock/gsap-skills (`skills/`) | `aed9cfd` (2026-04-21) | MIT |

Revisão de segurança feita antes de instalar: nenhum script acessa rede, executa comandos do sistema ou contém instruções
que contradigam o usuário. O único código executável é `ui-ux-pro-max/scripts/*.py` (busca local em CSV, só biblioteca padrão).

Atualizar: clonar a fonte de novo, comparar com a pasta e atualizar o commit nesta tabela.
