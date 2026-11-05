# 14 — Redesign premium: brief das etapas 1–3 (05/11/2026)

Base: diretriz `docs/13-diretriz-de-produto.md`, fluxo do `CLAUDE.md` §4, estado atual em `docs/10-design-system.md` §9.
Maquete visual para aprovação: canvas "NEXUS — Direção visual" (Início, Otimizar com a varredura, Fundamentos).
**Nada foi implementado ainda.** Este documento propõe; as decisões marcadas ⚑ esperam o responsável.

---

## Etapa 1 — Product Designer

### Para quem

| Persona | Quem é | O que quer | O que o trava hoje |
|---|---|---|---|
| **Jogador comum** (principal) | Notebook ou PC de entrada/média, joga Fortnite/Valorant/CS, não sabe o que é DPC ou core parking | "Meu jogo travando? Clica e resolve." | 27 ferramentas avançadas; o Início não mostra o PC "vivo" |
| **Curioso que mexe** | Já usou outros otimizadores, desconfia de promessa | Ver o que muda, risco, fonte, desfazer | Detalhe técnico mistura com o essencial em algumas páginas |
| **Quem ajuda os outros** | O "amigo que entende" que instala no PC do irmão | Diagnóstico claro para explicar, relatório | Achados espalhados entre Problemas, Checagens e módulos |

PC de referência usado na maquete (lido em `_claude/windows-dados.txt`): Acer Nitro AN515-45, AMD Ryzen 5 5600H,
NVIDIA GeForce GTX 1650 (4 GB), 8 GB de RAM em **um pente só (canal único)**, SSD NVMe 512 GB, Windows 11 Home.

### O que é "resolvido" (critério de sucesso)

1. **Abrir → entender em 5 s:** o Início responde "meu PC está bem?" com uma palavra, uma frase e o PC ao vivo.
2. **Um clique para cada intenção principal:** Analisar · Otimizar · Preparar para jogar · Desfazer — todas visíveis no Início.
3. **Nenhuma decisão técnica obrigatória.** O avançado existe, mas só aparece para quem abre.
4. **Confiança:** cada mudança diz o que faz, risco e como volta; nada de prometer FPS (só medido).

### Requisitos novos em relação ao que existe

| Requisito da diretriz | Hoje | Proposta |
|---|---|---|
| Dashboard com CPU, GPU, RAM, temperaturas, armazenamento, drivers | Anel de saúde + até 3 cartões | **Painel de instrumentos ao vivo** (4 mostradores) + linha de drivers e problemas |
| "NEXUS Optimize" com diagnóstico → classificação → aplicar seguro → resultado | Otimizar automático (Assistente) + Ajustes rápidos | Mesma lógica (`AssistantPage.RunAutomaticAsync`, `AutoPilot`), **nova apresentação em 4 fases** e a varredura exclusiva |
| Navegação 5 itens | 6 itens + "Ferramentas avançadas" | 5 áreas (Etapa 3) ⚑ |
| Animação exclusiva do diagnóstico | `ProcessRing` + `ScanlineOverlay` genéricos | **"Varredura"** (Etapa 2) |
| Imagens reais de hardware | Ilustrações vetoriais `Illo.*` | Ilustrações técnicas novas, com o modelo detectado escrito ⚑ |
| Linguagem simples | Já existe (`PlainFindings`, `SimpleHealth`) | Estender aos mostradores ("Seu processador está tranquilo") |

### Limites honestos (o design não pode esconder)

- **Temperatura do processador:** o Windows não tem leitura pública confiável (`docs/modules/monitor.md`). O mostrador mostra
  uso e frequência; a temperatura aparece como "o Windows não informa" — nunca um número inventado.
- **Temperatura da placa de vídeo:** só NVIDIA (via `nvidia-smi`). No GTX 1650 aparece; em AMD/Intel, "não informada".
- **Nota de saúde ≠ nota de desempenho.** Continua valendo `docs/10` §1.9.

### Decisões de produto

| Decisão | Escolha | Alternativas | Por quê |
|---|---|---|---|
| Ação principal do Início | **Otimizar meu PC** (âmbar, única primária) | Analisar como primária | Analisar já acontece sozinho ao abrir; o usuário quer o resultado |
| Preparar para jogar | Botão secundário no Início, aplica a Sessão de jogo + Modo de jogo | Página própria | Intenção frequente, um clique |
| Desfazer | Sempre no Início ("Desfazer tudo", só aparece se houve mudança) e no Histórico | Só no Histórico | A diretriz pede restauração a um clique |
| Mostradores ao vivo | 1 leitura/s só com a janela visível; pausa minimizado/na bandeja | Sempre ligado | Meta de custo < 1 % de CPU do Monitor |

---

## Etapa 2 — Creative Director

### Conceito: **"Instrumento de precisão"**

O NEXUS é um instrumento de bancada — osciloscópio, painel de carro esportivo bem desenhado — não uma "nave gamer".
Escuro profundo, poucos elementos, números grandes e calmos, uma agulha âmbar que indica ação. A imagem de referência enviada
(painel de telemetria com mostradores laterais e leitura central) entra como **composição**: dois mostradores ladeando uma
leitura central. Fica de fora o que a diretriz proíbe: neon saturado, gradientes fortes, brilho em tudo.

| Princípio | Na prática |
|---|---|
| **Calmo até precisar** | Repouso em grafite e azul-noite; cor só em dado e ação. Âmbar = ação/agulha; ciano = medição; verde = bom; vermelho = problema |
| **O número é o herói** | Leituras em Bahnschrift SemiCondensed grandes; rótulo pequeno; frase simples embaixo |
| **Profundidade sem borda** | Superfícies separadas por luminosidade e um realce de 1 px no topo (luz de cima), não por contorno em tudo |
| **Uma assinatura por tela** | Início: o painel de instrumentos. Otimizar: a varredura. Resto: sóbrio |
| **Movimento que informa** | Agulha que assenta, número que conta, varredura que percorre. Nada em loop decorativo |

### Identidade (continuidade com o que existe)

Mantém a paleta de `Tokens.xaml` (já é identidade própria, testada e coerente) com três ajustes:

| Token | Hoje | Proposta | Motivo |
|---|---|---|---|
| `C.TextDim` | #5C6785 (3,3:1) | **#7C88A8** (5,3:1 sobre Bg; 4,8:1 sobre Surface) | Reprovava WCAG AA |
| novo `Brush.Highlight` | — | branco 5 % (realce de 1 px no topo das superfícies) | Profundidade sem borda |
| novo `C.Ground` | — | #0A0E18 (atrás do painel de instrumentos) | Um degrau a mais de profundidade só no herói |

Tipografia: **Bahnschrift** (display e números, já vem no Windows, desenho DIN de instrumento) + **Segoe UI Variable**
(texto). Nenhuma fonte nova a distribuir. Escala proposta (base 14, razão ≈ 1,25): 12 · 14 · 16 · 20 · 28 · 40 · 56.
Espaço: grade de 4 px — `Space.1…8` = 4 · 8 · 12 · 16 · 24 · 32 · 48 · 64. Raios: 6 (controle) · 10 (cartão) · 14 (painel).

### Assinatura de movimento: **a Varredura**

Animação exclusiva do diagnóstico. Um anel com seis pontos — Processador, Placa de vídeo, Memória, Armazenamento, Drivers,
Windows. Uma linha âmbar fina gira como o ponteiro de um radar; quando passa por um ponto que já foi lido, ele acende (ciano
→ verde, ou âmbar se achou algo) e o nome aparece com o achado em uma linha. No centro, o número de verificações conta.

- **É dado de verdade:** cada ponto só acende quando aquela leitura termina (`PcChecksCoordinator`, diagnóstico). Se a leitura
  demora, a linha continua girando; nunca "termina" antes do PC.
- **Barato:** uma `RotateTransform` numa linha + `Opacity` dos pontos (composição na GPU, sem layout). Para quando a janela
  não está visível.
- **Movimento reduzido:** sem rotação; os pontos acendem em sequência, com o mesmo texto.

Curvas (equivalência no `CLAUDE.md` §4): entrada `CubicEase EaseOut` 220 ms; agulha `BackEase EaseOut` (amplitude 0,3)
600 ms; número `QuarticEase EaseOut` 600 ms; painéis abrindo 220 ms; nada acima de 600 ms, exceto a varredura (contínua).

### Imagens ⚑

Fotos de fabricantes (AMD, NVIDIA, Intel, Kingston…) são material protegido; usar exige autorização de cada um. Por isso
há duas opções:

- **A (recomendada):** ilustrações técnicas originais do NEXUS em vetor (processador, placa de vídeo, pente de memória, SSD,
  notebook/gabinete), com luz e volume, e o **modelo detectado escrito ao lado**. São marcadas como ilustração e servem
  para qualquer marca. Não pesam (são XAML) e não exigem licença.
- **B:** fotos reais licenciadas (banco de imagens com licença comercial, ou kits de imprensa com autorização por escrito do
  fabricante). Isso adiciona arquivos ao instalador e um registro em `docs/05-dependencies.md`.

---

## Etapa 3 — UI/UX Designer (arquitetura de informação)

### Navegação proposta ⚑

```
NEXUS
├── Início          painel de instrumentos · saúde · Otimizar / Preparar para jogar / Desfazer · até 3 problemas
├── Otimizar        NEXUS Optimize (varredura → plano → aplicar → resultado)
│   ├─ Limpeza            (era item próprio "Limpar")
│   ├─ Ajustes rápidos    (Velocidade · Jogos · Privacidade · Energia)
│   └─ Avançado ▸ Inicialização · Serviços · Programas · Privacidade · Catálogo completo
├── Desempenho      Jogos (por jogo) · Medir FPS · Ao vivo (Monitor)
│   └─ Avançado ▸ Processador · Placa de vídeo · Memória RAM · Rede para jogos · Mouse
├── Diagnóstico     Problemas (em palavras simples) · Drivers · Segurança · Saúde dos discos
│   └─ Avançado ▸ Por que travou? · O jogo fechou sozinho? · Verificar arquivos · Ferramentas do sistema · Capacidades
└── Configurações   Ajustes · Automático · Idioma · Atualizações
                    Histórico (todas as mudanças + Desfazer tudo)
```

Mudanças em relação a hoje:

| Hoje | Proposta | Efeito |
|---|---|---|
| Limpar (item próprio) | Dentro de Otimizar, como primeira aba; a limpeza já faz parte do "Otimizar meu PC" | 1 clique a mais só para quem quer **só** limpar |
| Jogos | Desempenho › Jogos (aba inicial) | Mesmo número de cliques |
| Problemas | Diagnóstico › Problemas (aba inicial) | Mesmo número de cliques |
| Histórico | Configurações › Histórico + "Desfazer tudo" no Início | Desfazer fica mais perto |
| "Ferramentas avançadas" (27 ladrilhos numa página) | Cada ferramenta dentro da sua área, recolhida em "Avançado ▸" | Fica onde a pessoa procuraria |

### Fluxo principal: Otimizar meu PC (um clique)

```
Início ─[Otimizar meu PC]─▶ 1 Varredura (lê o PC; 6 pontos acendem)
                            2 Plano: "Encontramos N melhorias seguras"
                               cada linha: nome · frase · benefício · risco · desfaz? · estado atual
                               seguras e autorizadas já marcadas; o resto fica visível, desmarcado
                            [Aplicar N melhorias]  (1 confirmação; aviso do Windows só se for de sistema)
                            3 Aplicando: cada linha confirma ✓ ou explica o erro em palavras simples
                            4 Resultado: "Pronto. Liberamos X GB e ajustamos Y coisas."  [Desfazer tudo] [Ver detalhes]
                               FPS: "Quer saber se melhorou? Medir uma partida" (nunca um número prometido)
```

Erros e estados:
- **Sem permissão de administrador:** a linha diz "Precisa da sua permissão no Windows" e oferece tentar de novo.
- **Falhou:** ✕ com uma frase do porquê e "Nada foi alterado nesta etapa" (o aplicador já garante).
- **Carregando:** esqueleto nos mostradores, nunca "Lendo…".
- **Vazio:** "Seu PC já está em ordem. Nada a fazer agora." com a data da última verificação.

### Impacto técnico previsto (para a etapa 6, depois da aprovação)

- Visual: `Themes/*`, `MainWindow.xaml`, páginas da interface simples, componentes novos (`Gauge`, `ScanSweep`,
  `OptimizePlanRow`). Lógica de aplicar, catálogo, broker e diário **não mudam**.
- `ShellMap`/navegação e `SimpleModeTests` mudam **porque o requisito muda** (5 áreas em vez de 6+1). Isso será feito
  explicitamente e registrado; não é mudar teste para passar.
- Mostradores ao vivo reutilizam `Nexus.Monitoring` (mesma agregação da página Monitor).

### Pendências do responsável ⚑

1. Aprovar ou ajustar a direção visual ("Instrumento de precisão") e a maquete.
2. Aprovar a navegação em 5 áreas (Limpar e Histórico deixam de ser itens próprios).
3. Imagens: opção A (ilustrações próprias) ou B (fotos licenciadas).
4. Reenviar o fim da diretriz (seção 11, "Teste de simplicidade").
