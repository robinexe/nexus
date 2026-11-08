# Novidades do NEXUS

Cada versão tem um resumo de uma linha (a linha que começa com `>`): é o texto do aviso de atualização dentro do NEXUS.
O resto da seção vai para a página de download (GitHub Releases).

## 1.1.3

> Cartões de 11 jogos com design novo, e "Atraso de drivers" apontando qual driver é o culpado.

- **Cartões de jogo redesenhados** (Desempenho › Jogos): selo com monograma e cor discreta (sem logotipos), estado
  "instalado · aberto agora · última partida medida", opções de um clique com "✓ Ligado — Desfazer", "Medir FPS" e "Comparar
  com e sem", aviso para fechar o jogo antes e destaque do que é recomendado para o seu PC.
- **Jogos novos**: Fortnite (migrado), Overwatch 2, Apex Legends, Minecraft (Java e Bedrock), Roblox, GTA V (Legacy e
  Enhanced) com opções no arquivo que o próprio jogo grava; VALORANT, League of Legends, Call of Duty, Rocket League e EA FC
  só com Medir/Comparar e a explicação de por que o NEXUS não mexe nos arquivos deles (servidor, antitrapaça ou arquivo
  regravado). Mitos de cada jogo em "rejeitados" com fonte.
- **Atraso de drivers com culpado**: "Investigar como administrador (10 s)" rastreia DPC/interrupções (ETW) e mostra o
  provável driver culpado, o dispositivo e os cinco que mais seguraram o processador, com um conselho por categoria. Sem
  administrador, continua só o total, dito com honestidade. Aparece também no Diagnóstico quando o nível é ruim.

## 1.1.2

> Modo jogo automático: liga o conjunto escolhido quando um jogo abre e desfaz tudo quando ele fecha.

- **Modo jogo automático** (Jogos; desligado por padrão): você escolhe o que entra (plano de energia de sessão, notificações
  silenciadas, captura em segundo plano do Game DVR; teto de downloads só com o NEXUS como administrador). Ao abrir um jogo, o
  NEXUS aplica pelo aplicador; ao fechar (com folga configurável de 5 s a 2 min), desfaz pelas execuções do histórico. Se o
  NEXUS for fechado com um jogo aberto, ao abrir de novo ele desfaz o que ficou pendente. Avisos discretos na bandeja; se um
  desfazer falhar, aparece em Problemas com atalho para o Histórico. Substitui a "Sessão de jogo".
- Dois avisos do compilador corrigidos (build volta a 0 avisos).

## 1.1.1

> "Otimizar meu PC" mostra a conta completa: o que aplicou agora, o que já estava no máximo e o que não serve para o seu PC.

- **Resumo completo do "Otimizar meu PC"**: além do que mudou na rodada, mostra quantos ajustes já estavam no máximo, quantos
  não servem para este PC (placa de outra marca, jogo não instalado) e se algum deu problema.
- **Navegadores fechados não seguram o PC**: o "Otimizar meu PC" também impede o Edge e o Chrome de continuarem rodando depois
  de fechados e desliga a Inicialização rápida do Edge.

## 1.1.0

> Desempenho máximo de verdade: o "Otimizar meu PC" agora aplica tudo o que dá FPS (Windows, processador, placa, memória, disco, rede e jogos), o NEXUS nunca limita o FPS e há visual novo.

- **"Otimizar meu PC" = Desempenho máximo**: aplica todos os ajustes de desempenho que podem ser desfeitos, em qualquer PC
  (notebook, desktop, gamer), sem economia de energia. Nunca desliga proteções do Windows nem limita o FPS.
- **Mais de 60 ajustes novos**: modo de energia "Melhor desempenho", processador e núcleos de eficiência no máximo, SSD/NVMe/SATA
  sem economia, gráficos Intel/AMD de notebook no máximo, placa NVIDIA no máximo em todos os jogos, AMD Anti-Lag e Chill/limite
  desligados, Intel baixa latência e Endurance desligado, placa de rede sem economia, prioridade de rede para jogos online.
- **Jogos**: VALORANT, CS2 e Fortnite com FPS máximo (driver, arquivos do jogo e guia do que mudar no menu); Minecraft, Rainbow
  Six Siege, Overwatch 2 e Apex com os arquivos de configuração no máximo de FPS; placa de vídeo dedicada em cada jogo.
- **Reiniciar na BIOS**: para ligar XMP/EXPO, Resizable BAR e MUX, o NEXUS reinicia direto na tela da placa-mãe e explica o que
  mudar.
- **Túnel de rotas** (linha de comando `nexus rota`): rota alternativa para os servidores de jogo por um relay próprio.

- **Início ao vivo**: processador, placa de vídeo, memória e disco em tempo real, explicados em frases simples ("Seu processador
  está trabalhando intensamente"). Temperatura só quando o Windows informa de verdade.
- **Navegação em 5 áreas**: Início, Otimizar, Desempenho, Diagnóstico e Configurações. As ferramentas completas ficam em
  "Avançado" dentro de cada área.
- **NEXUS Optimize**: um clique, com a Varredura mostrando cada etapa terminando de verdade. Cada ajuste mostra o risco, o
  estado atual e como desfazer.
- Contraste melhor nos textos de apoio e um visual mais limpo, sem contornos em tudo.
- **Avisos do NEXUS**: as confirmações deixaram de usar a caixa antiga do Windows. Agora são avisos no visual do NEXUS, com a
  lista organizada, texto que rola quando é longo e os botões sempre à vista.
- **FPS sem limite**: o NEXUS nunca mais limita o FPS. Em Jogos, cada jogo com placa NVIDIA ganha "FPS sem limite" (tira o
  limite de FPS e o V-Sync forçado do driver), o Fortnite ganha "FPS máximo e menos atraso" (sem limite + Reflex), e um
  cartão novo mostra se o driver NVIDIA está limitando todos os jogos, com "Liberar FPS" em um clique. Quem usou o antigo
  "Limitar FPS" pode tirar o limite ali mesmo (ou desfazer no Histórico).

## 1.0.0

> Primeira versão estável: Assistente, limpeza, drivers, ajustes de desempenho explicados, medição de FPS e desfazer de tudo.

O NEXUS deixa o Windows mais rápido e limpo sem quebrar nada: cada mudança é explicada, confirmada por você, registrada
num histórico e pode ser desfeita.

### Assistente

- Lê o seu PC e monta um plano em etapas (limpeza, inicialização, privacidade, energia, drivers, ajustes), com o que cada
  item faz, quanto ajuda e o risco.
- Aplica tudo de uma vez com tela de progresso real, gera um relatório (HTML) e tem "Desfazer tudo".
- Ajustes avançados aparecem separados e desmarcados.

### Manutenção

- **Limpeza**: temporários, caches de navegadores, apps e jogos, Lixeira. Nada é apagado de verdade: os arquivos vão para a
  quarentena do NEXUS e podem voltar ao lugar. Regras próprias em JSON e histórico.
- **Disco**: analisador de espaço (o que ocupa cada pasta, explicado); exclusão segura com confirmação dupla (essa não tem volta,
  e o NEXUS avisa).
- **Programas**: desinstalador com remoção de sobras e desinstalação em massa (fila, ponto de restauração).
- **Inicialização e serviços**: impacto de cada item, desligar em massa, sempre reversível.
- **Privacidade**: mais de 30 opções do Windows (telemetria, anúncios, Recall e outras) com explicação e desfazer.
- **Segurança**: placar de proteção (Defender, firewall, SmartScreen, Secure Boot, BitLocker) e verificação de arquivos
  (hash, regras, análise de executáveis).
- **Ferramentas do sistema**: rede (DNS, ARP, redes Wi-Fi salvas), pacotes de driver, pontos de restauração, apps do Windows.
- **Automático**: verificação e limpeza segura agendadas (diária, semanal ou mensal), sem janela preta, e iniciar com o
  Windows.

### Desempenho, com honestidade

- **Ajustes de desempenho**: catálogo com fontes (documentação da Microsoft e dos fabricantes), mecanismo, risco e como
  desfazer de cada ajuste. Os "mitos" conhecidos (limpar RAM, "limpar registro acelera" e outros) aparecem como mitos, com
  a fonte, e o NEXUS não os aplica.
- **Placa de vídeo**: padrões do DirectX (janela otimizada, VRR, Auto HDR), ajustes por jogo e, em placas NVIDIA, cache de
  sombreador e opções por jogo direto no driver (baixa latência, desempenho máximo, limite de FPS).
- **Processador e memória**: laboratório que mostra o que o seu hardware faz de verdade; diagnóstico de vazamento de memória.
- **O que só você pode fazer**: Resizable BAR, XMP/EXPO, MUX de notebook, link PCIe: o NEXUS mostra o estado e explica,
  sem mexer na BIOS.

### Medição

- **Medir**: FPS e tempo de quadro com o PresentMon oficial da Intel (incluído e conferido), detecção do jogo, captura
  automática e comparação antes x depois. Assim você vê se um ajuste ajudou no seu PC.
- **Monitor** ao vivo (processador, memória, disco, rede, placa de vídeo).

### Drivers

- Busca de drivers no Windows Update oficial, com cópia de segurança e desfazer pelo próprio Windows (pnputil).
- Placa de vídeo NVIDIA/AMD: baixa só dos servidores oficiais do fabricante quando você pede, confere a assinatura digital
  e instala dentro do NEXUS.

### Segurança das mudanças

- Toda mudança passa pelo aplicador do NEXUS: grava no histórico **antes** de escrever, confere depois e sabe desfazer.
- Mudanças de sistema pedem permissão ao Windows e criam um ponto de restauração.
- Se algo for interrompido (queda de energia, travamento), o NEXUS recupera na próxima vez que abrir.
- Sem conta, sem telemetria. Atualização automática só do próprio instalador, com assinatura conferida.

### Requisitos

Windows 10 (1809 ou mais novo) ou Windows 11, 64 bits (x64). Não precisa instalar o .NET.
