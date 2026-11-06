# Novidades do NEXUS

Cada versão tem um resumo de uma linha (a linha que começa com `>`): é o texto do aviso de atualização dentro do NEXUS.
O resto da seção vai para a página de download (GitHub Releases).

## Próxima versão (em desenvolvimento)

> Visual novo: Início com o seu PC ao vivo, navegação em 5 áreas e a Varredura do NEXUS Optimize.

- **Início ao vivo**: processador, placa de vídeo, memória e disco em tempo real, explicados em frases simples ("Seu processador
  está trabalhando intensamente"). Temperatura só quando o Windows informa de verdade.
- **Navegação em 5 áreas**: Início, Otimizar, Desempenho, Diagnóstico e Configurações. As ferramentas completas ficam em
  "Avançado" dentro de cada área.
- **NEXUS Optimize**: um clique, com a Varredura mostrando cada etapa terminando de verdade. Cada ajuste mostra o risco, o
  estado atual e como desfazer.
- Contraste melhor nos textos de apoio e um visual mais limpo, sem contornos em tudo.
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
