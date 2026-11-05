# Laboratório A/B — "Comparar com e sem"

Página **Laboratório A/B** (Ferramentas avançadas › grupo Medir) e o atalho **"Comparar com e sem"** ao lado de "Medir FPS"
em cada jogo da tela **Jogos**. Prova, **neste PC**, se um ajuste ajuda num jogo: o usuário joga uma partida sem o ajuste
(A), o NEXUS aplica o ajuste pelo aplicador, o usuário joga de novo (B) e, se pediu mais certeza, mais uma sem (A de novo).
Só fica o que melhorar além da variação normal; o resto volta ao estado inicial, com o consentimento dado no começo.

Não há ação de catálogo própria: o laboratório **reaproveita** as ações que a tela Jogos e os Ajustes rápidos já pedem
(driver NVIDIA por jogo, jogos em janela, Fortnite, ajustes do `catalog/tuning.v1.json`…), sempre pelo aplicador
(`IModuleHost.ApplyConfirmedAsync` / `RevertWithoutConfirmationAsync`), com diário e desfazer. As únicas gravações
próprias são `%LocalAppData%\NEXUS\ab-lab.json` (teste em andamento, fila, histórico; escrita atômica, arquivo ilegível vira
`.bad`) e as partidas do Medir (`%LocalAppData%\NEXUS\sessoes\`).

## Onde está o quê

| Peça | Arquivo |
|---|---|
| Modelo, regras das partidas, máquina de estados, fila e histórico (sem WPF, testados) | `src/Nexus.Measurement/AbLab/AbLabModel.cs`, `AbLabMachine.cs` (`AbLabMachine`, `AbLabFlow`) |
| Veredito (A/B e A/B/A) | `src/Nexus.Measurement/AbLab/AbLabJudge.cs` |
| Persistência | `src/Nexus.Measurement/AbLab/AbLabStore.cs` |
| Página: candidatos a partir da tela Jogos e do catálogo, frases em linguagem simples (sem WPF, testado) | `src/Nexus.Desktop/ViewModels/Modules/AbLabText.cs` |
| Página: executa os comandos da máquina (aplicar/desfazer/comparar/perguntar), recebe as partidas do Medir | `src/Nexus.Desktop/ViewModels/Modules/AbLabPage.cs` |
| XAML | `src/Nexus.Desktop/Views/Pages/AbLab.xaml` (um por arquivo; estilos de `Themes/Controls.xaml`) |
| Testes | `tests/Nexus.Tests/Modules/AbLabTests.cs` (lógica), `AbLabPageTests.cs` (tela), `WiringTests.Ab_lab_candidates_only_come_from_game_option_plans_and_catalog_tweaks` |

## Como funciona

1. **Escolher.** O jogo vem dos cartões da tela Jogos (biblioteca Steam/Epic/… lida pela página Placa de vídeo) e dos jogos
   já medidos no Medir. Os ajustes a testar são as opções daquele jogo na tela Jogos (`GameOptionPlans`: mesmas ações,
   valores, argumentos e nível) mais os **Ajustes rápidos** do catálogo que ainda não estão ligados neste PC
   (`QuickTweaksPanel.All`, montados pela janela a partir do próprio catálogo). O ViewModel nunca escreve uma chave de ação à
   mão (teste de fiação). Marcar vários: são testados **um de cada vez**, em fila, no mesmo jogo e com as mesmas opções.
2. **Desenho.** A/B (duas partidas: sem, com) ou **A/B/A** (três: sem, com, sem). Por padrão o que não ajudou volta
   sozinho; "Perguntar no fim" troca isso por "Manter mesmo assim? / Deixar como estava".
3. **Confirmação única** ao começar: "Testar X em Y? São N partidas de pelo menos 3 minutos. Entre elas o NEXUS aplica o
   ajuste pelo aplicador (com diário e desfazer). Se não ajudar, volta sozinho ao estado inicial." Esse é o consentimento
   para o aplicar e o desfazer **sem outra caixa** no meio do fluxo (mesmo contrato do "um clique" da interface simples e do
   modo automático do Assistente). Mudanças de sistema continuam pedindo o aviso do Windows na hora.
4. **Partidas.** O laboratório liga a medição automática do Medir e espera. Cada partida que o Medir guarda é oferecida
   à máquina (`AbLabMachine.Offer`). Ela entra no teste se: é do mesmo jogo; começou **depois** do passo atual; não está
   marcada como atípica; durou **≥ 150 s** e tem **≥ 3000 quadros**; e resolução, tomada/bateria e driver de vídeo são os
   mesmos da primeira partida aceita (condição desconhecida de um lado não reprova). Senão a página diz por quê em uma frase
   e pede outra partida. Partida de outro jogo é ignorada em silêncio.
5. **Entre as partidas.** Aceita a A → aplica (`Applying`). "Já estava assim" (nenhum `Verified`) termina o teste como
   **já estava assim** (não há o que comparar). Aceita a B → A/B compara; A/B/A desfaz (`Reverting`) e espera a terceira.
6. **Veredito** (`AbLabJudge`): a mesma estatística do Medir (`AutoCompare.CompareSegments`): trechos de 30 s pareados
   pela posição (o primeiro, de carregamento, fica de fora), intervalo de 95% por bootstrap, diferença mínima relevante por
   métrica (FPS médio 3% ou 0,5 FPS; 1% low 5% ou 0,5 FPS; P99 5% ou 0,1 ms; engasgos 1/min). Em A/B/A a partida com o ajuste
   é comparada com **as duas** sem; só conta melhora (ou piora) que aparece contra as duas, e se A1 × A2 já dá diferença
   "real" numa métrica, o jogo variou sozinho: a métrica fica **inconclusiva (ruído)**.
   **Decisão: manter só se pelo menos uma métrica decisiva (FPS médio, 1% low, P99, engasgos) melhorou e nenhuma
   piorou.** Misto, igual, inconclusivo, ruído e dados insuficientes = **não ajudou / inconclusivo → não fica**. A variação do
   frametime (desvio padrão, "Regularidade") é só indicativa ("parece melhor/pior, sem prova") e nunca decide.
7. **Fim.** Ajudou: A/B fica como está; A/B/A aplica de novo (`Reapplying`). Não ajudou: A/B desfaz (`Undoing`); A/B/A já
   está desfeito. Resultado em palavras simples ("Ajudou", "Não ajudou: igual na prática", "Inconclusivo") com o número do
   destaque ("+18% no 1% low"), antes → depois por métrica com selo honesto (só "Melhorou"/"Piorou" com prova), avisos
   visíveis e o método recolhido. O teste vai para o histórico (`AbLabFlow.Advance`, até 200) e o próximo da fila começa.
8. **Cancelar** só nos passos de espera. Se o ajuste está aplicado pelo teste, ele é desfeito antes (combinado no começo).
   **Fechar o NEXUS** no meio: tudo está gravado; ao reabrir, um passo de aplicar que ficou no meio vira "interrompido:
   confira no Histórico" (o aplicador pode ou não ter gravado); desfazer é repetido com segurança.

## Limites (o que o veredito significa e o que não significa)

- **Precisa de partidas iguais.** O NEXUS não vê a cena: só compara números. Jogar a fase 1 sem o ajuste e a fase 7 com ele
  dá um "resultado" que não é do ajuste. O aviso "o resultado só vale se as partidas foram parecidas" aparece sempre.
  Mesma fase, mesma resolução, mesmo tempo, notebook na tomada.
- **Variação natural.** Duas partidas iguais sem mudar nada já diferem alguns por cento (aquecimento, cache de sombreadores,
  horário, outros programas). Por isso só conta o que passa do intervalo de confiança **e** da diferença mínima relevante; e
  por isso A/B/A existe: a terceira partida mede essa variação. "Inconclusivo" quer dizer exatamente isso: não dá para
  afirmar que ajudou — não que não ajudou.
- **Uma partida de cada lado é indicativo.** Trechos da mesma partida não são independentes; o Medir, com 3+ partidas de
  cada lado, dá um veredito mais firme. O laboratório prefere uma resposta honesta e rápida a uma falsa certeza: repita o
  teste se o resultado importa.
- **"Já estava assim"** não é veredito: o ajuste já estava ligado antes e não houve o que comparar.
- **Não mede** latência de entrada, qualidade de imagem, temperatura nem consumo: só quadros apresentados (PresentMon).
- **Nunca promete ganho.** O catálogo continua com mecanismo, evidência, riscos e desfazer de cada ação; o laboratório só
  responde "neste PC, neste jogo, nesta cena, ajudou ou não".

## O que falta testar em máquina real

Toda a lógica (regras, veredito, máquina, fila, persistência, frases) está coberta por testes unitários com partidas
sintéticas. **Falta um ciclo completo num PC com jogo e aplicador reais**: partida medida pelo PresentMon → aplicar pelo
aplicador (usuário e administrador) → segunda partida → veredito → desfazer. O autoteste da interface abre a página (sem teste
em andamento) nos três idiomas; a verificação "Laboratório A/B" do autoteste lê o `ab-lab.json` como a página lê.
