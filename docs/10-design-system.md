# 10 — Design system v2 ("bancada de telemetria", didático)

Pedido do responsável: *"melhorar o design de todas as páginas, está muito simples; quero animações, fotos, demonstrativos,
imagens; menos textos e mais didático, fácil de entender"*.

Este documento é o manual para redesenhar qualquer página do NEXUS com o mesmo sistema. As duas páginas-modelo são
**Meu PC** (`Views/Pages/Overview.xaml`) e **Limpeza** (`Views/Pages/Cleaning.xaml`): copie a estrutura delas.

---

## 1. Princípios

1. **Uma ideia por tela.** O topo da página responde "o que é e quanto": um `HeroPanel` com a ilustração do módulo, um
   título curto e **um número grande** (o que o usuário ganha/tem). Todo o resto apoia esse número.
2. **Número grande, rótulo pequeno.** Números reais, lidos ou medidos (nunca inventados), em Bahnschrift, contando até o valor
   (`AnimatedNumber`). Sem número disponível ainda: esqueleto pulsando (`SkeletonLoader`), nunca "Lendo…".
3. **Verbo claro.** O botão diz o que acontece: "Limpar selecionados", "Desfazer última limpeza", "Medir uma partida".
   Um único botão primário (âmbar) por tela; o resto é secundário, fantasma (ciano) ou perigo (vermelho, irreversível).
4. **Texto curto: no máximo 2 linhas visíveis.** Legenda do destaque em 1 linha; cartões com título + 1 frase.
5. **Detalhes recolhidos.** Mecanismo, risco, fonte e notas longas vão para `ExplainToggle` ("Por que isso?"). Avisos de
   segurança (dados pessoais, irreversível, programa aberto) **continuam visíveis** — nunca esconda um risco.
6. **Ilustração por módulo.** Cada página abre com a sua ilustração (`Illo.*`) e usa ícones de linha (`Icon.*`) em listas,
   abas e cartões. Mesmo desenho, mesma paleta.
7. **Demonstrativo antes de pedir confiança.** Se a página faz algo com o PC, mostre **como funciona** em 3–4 passos
   (`HowItWorks`) e **o efeito** com antes → depois (`BeforeAfter`).
8. **Movimento só para mostrar mudança ou carregamento.** Contagem do número, preenchimento do anel/barras, entrada da página
   (uma vez por navegação), "Por que isso?" abrindo, esqueleto enquanto carrega. Nada decorativo em loop. Com
   "Mostrar animações no Windows" desligado (`SystemParameters.ClientAreaAnimation`), **nada se move**.
9. **Honestidade.** Cor verde só para o que é bom de verdade; "itens em ordem" não é "nota de desempenho"; nenhuma promessa de
   FPS. As regras do brief (mitos, risco, desfazer) valem também para o visual.

Cores (Tokens.xaml, inalteradas): fundo `#0E1320`; **âmbar `#FFB547`** = ação/medida/"depois"; **ciano `#4FD1E8`** =
sinal/referência/"antes"/link; **verde `#5EE6A0`** = bom; **vermelho `#FF6B7A`** = problema/irreversível.

---

## 2. Onde fica cada coisa

| O quê | Arquivo |
|---|---|
| Tokens (cores, tipos, tamanhos) | `src/Nexus.Desktop/Themes/Tokens.xaml` |
| Estilos base (Text.*, Panel.*, Button.*, Chip) — **contrato** | `Themes/Controls.xaml` (cabeçalho lista todos os nomes) |
| Ícones de linha `Icon.*` | `Themes/Icons.xaml` |
| Ilustrações `Illo.*` | `Assets/Illustrations/Hardware.xaml`, `Assets/Illustrations/Modules.xaml` |
| Modelos dos componentes, abas, conversores | `Themes/Components.xaml` |
| Código dos componentes | `Views/Controls/*.cs` |
| Lógica pura (formatação, faixas, %, anel) — testada | `Views/Controls/DesignMath.cs` |
| Mapas página → ícone/ilustração, achado → página | `Views/Shell/ShellMap.cs` |
| Navegar para uma página de qualquer lugar | `Views/Shell/ShellCommands.cs` |
| Movimento (respeita animações desligadas) | `Views/Shell/ShellMotion.cs` |
| Testes | `tests/Nexus.Tests/DesignSystemTests.cs` |

Todos os dicionários estão registrados em `App.xaml` (antes das páginas). Nas páginas, declare:

```xml
xmlns:c="clr-namespace:Nexus.Desktop.Views.Controls"
xmlns:shell="clr-namespace:Nexus.Desktop.Views.Shell"
```

---

## 3. Catálogo de componentes

### HeroPanel — abertura da página
Ilustração + título curto + 1 número grande que conta + legenda de 1 linha + ação principal (e secundária). O conteúdo
(filho) vai à direita: anel, resumo, botões de perigo.

```xml
<c:HeroPanel Illustration="{StaticResource Illo.Cleaning}" Title="Dá para liberar"
             Number="{Binding RecoverableBytes}" Kind="Bytes" IsLoading="{Binding Scanning}"
             Caption="Medido agora, sem mexer em nada. O que sai vai para a quarentena e pode voltar."
             ActionText="Limpar selecionados" Command="{Binding CleanCommand}"
             SecondaryText="Desfazer última limpeza" SecondaryCommand="{Binding UndoLastCommand}">
  <c:RingMeter Value="{Binding Score}" WarnAt="80" BadAt="50" Label="em ordem"/>
</c:HeroPanel>
```

Propriedades: `Illustration`, `Title`, `Number` (double; NaN = sem número) + `Kind` (`Number`/`Bytes`/`Percent`),
`Decimals`, `Suffix`; `ValueText` (texto no lugar do número, ex.: nome do PC; vence `Number`); `Caption`; `Accent`
(cor do número, padrão âmbar); `ActionText`/`Command`/`CommandParameter`; `SecondaryText`/`SecondaryCommand`/
`SecondaryCommandParameter`; `IsLoading` (troca o número por esqueleto). Ação irreversível **não** vai no `ActionText`
(que é âmbar): ponha um `Button.Danger` no conteúdo, como a aba Quarentena.

### AnimatedNumber — número que conta
É um `TextBlock` (aceita `Text.Metric` etc.). Conta de 0 ao valor quando aparece e do valor antigo ao novo quando muda
(0,7 s, desacelerando). NaN mostra `Placeholder` ("—").

```xml
<c:AnimatedNumber Value="{Binding SelectedBytes}" Kind="Bytes" Style="{StaticResource Text.Metric}"/>
<c:AnimatedNumber Value="{Binding Fps}" Decimals="0" Suffix=" FPS" FontFamily="{StaticResource Font.Numbers}" FontSize="26"/>
```

### RingMeter — anel de %/placar
Começa às 12 h, enche no sentido horário. Cor pela faixa: `WarnAt`/`BadAt` + `HigherIsBetter` (padrão verdadeiro). Sem
faixas, usa `Accent` (âmbar). Centro: número + `Label`.

```xml
<c:RingMeter Value="{Binding HealthScore}" WarnAt="80" BadAt="50" Label="em ordem" Width="136" Height="136" Thickness="11"/>
<!-- uso de disco: menor é melhor -->
<c:RingMeter Value="{Binding UsedPercent}" WarnAt="85" BadAt="95" HigherIsBetter="False" Label="usado"/>
```

### BeforeAfter — antes → depois
Duas barras (ciano = antes, âmbar = depois), valores e um selo com a diferença em % (verde se melhorou, vermelho se piorou,
"igual" abaixo de 0,5%). Barras enchem ao aparecer/mudar.

```xml
<c:BeforeAfter Before="{Binding FreeBytes}" After="{Binding FreeAfterBytes}" Kind="Bytes"
               BeforeLabel="Livre agora" AfterLabel="Depois de esvaziar"/>
<!-- tempo de boot: menor é melhor -->
<c:BeforeAfter Before="{Binding BootSecondsBefore}" After="{Binding BootSecondsAfter}" HigherIsBetter="False"
               BeforeLabel="Antes" AfterLabel="Depois"/>
```

### HowItWorks + StepItem (modelo "StepCard") — demonstrativo didático
3–4 passos lado a lado, numerados e ligados por linha. `Tone`: `Info` (ciano, padrão), `Warn` (âmbar, o passo principal),
`Good` (verde, desfazer), `Bad` (vermelho, irreversível).

```xml
<c:HowItWorks>
  <c:StepItem Icon="{StaticResource Icon.Search}" Title="Varre" Text="Mede caches e temporários. Medir não mexe em nada."/>
  <c:StepItem Icon="{StaticResource Icon.Quarantine}" Title="Guarda" Text="O que você limpa vai para a quarentena." Tone="Warn"/>
  <c:StepItem Icon="{StaticResource Icon.Undo}" Title="Desfaz" Text="Mudou de ideia? Restaure o lote inteiro." Tone="Good"/>
  <c:StepItem Icon="{StaticResource Icon.Trash}" Title="Esvazia" Text="Só aqui o espaço volta. Apaga de vez." Tone="Bad"/>
</c:HowItWorks>
```

Título = verbo curto; texto ≤ ~70 caracteres (2 linhas no máximo; o resto é cortado com "…").

### StatTile — cartão de um número
Ícone **ou** ilustração, número (animado) ou texto, rótulo, detalhe de 1–2 linhas e tendência (selo). Com `Command` vira
botão (foco, Enter/Espaço, hover âmbar, leitor de tela anuncia e aciona); sem `Command`, é só leitura e não recebe foco.

```xml
<c:StatTile Illustration="{StaticResource Illo.Ram}" Label="Memória RAM" Kind="Bytes"
            Number="{Binding Ram.Number}" Value="{Binding Ram.Value}" Detail="{Binding Ram.Note}"
            Trend="canal único" TrendTone="Warn"
            Command="{x:Static shell:ShellCommands.Navigate}" CommandParameter="MemoryLabPage"/>
<c:StatTile Icon="{StaticResource Icon.TrendUp}" Number="{Binding RecoveredMonthBytes}" Kind="Bytes" Tone="Good"
            Label="Recuperado neste mês" Detail="Quarentena e Lixeira esvaziadas."/>
```

Grade de cartões: `UniformGrid Columns="4" Margin="-7,0"` com cada cartão `Margin="7,0"` (mesma largura e vão de 14 px).

### ExplainToggle — "Por que isso?"
A explicação longa fica recolhida; abre com clique, Enter ou Espaço e entra com um fade curto. Texto puro vira `Text.Small`.

```xml
<c:ExplainToggle Content="{Binding DiskCleanupNote}"/>
<c:ExplainToggle Header="De onde vêm estes números?">
  <StackPanel>
    <TextBlock Text="{Binding Mechanism}" Style="{StaticResource Text.Small}"/>
    <TextBlock Text="{Binding Evidence}" Style="{StaticResource Text.Small}" Margin="0,6,0,0"/>
  </StackPanel>
</c:ExplainToggle>
```

Dê `Style` explícito aos `TextBlock` de dentro (não dependa do estilo implícito atravessar modelos).

### StatusChip — selo de estado com ícone
`Tone`: `Good` (✓ verde), `Warn` (! âmbar), `Bad` (× vermelho), `Info` (i ciano). `Icon` opcional troca o ícone.

```xml
<c:StatusChip Tone="Good" Text="Tudo em ordem"/>
<c:StatusChip Tone="Bad" Text="Esvaziar não tem desfazer"/>
```

O selo antigo (`ContentTemplate="{StaticResource Chip}"` para FactStatus/DiagnosticSeverity) continua valendo para listas
técnicas.

### EmptyState — nada aqui ainda
Ilustração + frase + detalhe + botão que diz o que fazer.

```xml
<c:EmptyState Illustration="{StaticResource Illo.Cleaning}" Text="A quarentena está vazia."
              Detail="O que você limpar aparece aqui e pode voltar até ser esvaziado."
              ActionText="Ir para Limpar" Command="{Binding ShowCleanCommand}"
              Visibility="{Binding HasBatches, Converter={StaticResource FalseToVisible}}"/>
```

### SkeletonLoader — carregando
`Shape="Lines"` (com `Lines`), `"Tiles"` (com `Columns`, dê `Height`), `"Hero"`. Pulsa só enquanto visível e com animações
ligadas. Anunciado como "Carregando".

```xml
<c:SkeletonLoader Shape="Tiles" Columns="4" Height="196" Visibility="{Binding IsLoading, Converter={StaticResource BoolToVis}}"/>
<c:SkeletonLoader Shape="Lines" Lines="6" Visibility="{Binding Scanning, Converter={StaticResource BoolToVis}}"/>
```

### Sparkline — tendência pequena (genérica)
Qualquer lista de números (observável ou não); `Fill` opcional, `Baseline` tracejada em ciano.

```xml
<c:Sparkline Values="{Binding History}" Height="36" Fill="{StaticResource Brush.TraceSoft}" Baseline="{Binding Target}"/>
```

(Medir e Monitor têm gráficos próprios mais completos; `Views/Modules/Measure/Sparkline.cs` continua lá.)

### Icon — ícone de linha
Grade 24, escala para `Size`, herda a cor do texto (`Foreground`) de onde está. Decorativo (o texto ao lado descreve).

```xml
<c:Icon Data="{StaticResource Icon.Clean}" Size="18"/>
<c:Icon Data="{Binding IconKey, Converter={StaticResource ResourceByKey}}" Size="19" Foreground="{StaticResource Brush.Signal}"/>
```

Distintivo redondo padrão (cartões, categorias):

```xml
<Grid Width="38" Height="38">
  <Ellipse Fill="{StaticResource Brush.SignalSoft}"/>
  <c:Icon Data="{StaticResource Icon.Browser}" Size="19" Foreground="{StaticResource Brush.Signal}" HorizontalAlignment="Center"/>
</Grid>
```

### Tela de progresso — `ProcessRing`, `ScanlineOverlay`, `Pulse.IsOn`
Para uma etapa que trabalha (ler o PC, aplicar). Modelo: a tela de progresso do Assistente (`Views/Pages/Assistant.xaml`,
`ViewModels/Modules/AssistantProcess.cs`). O andamento vem de `Nexus.Assistant.ProcessTracker` (marcos reais + tempo típico
dentro de cada parte, mostrado como "estimativa"); o tempo restante, de `EtaEstimator` (suavizado, nunca negativo,
"calculando…" até ter dados). Os três laços (giro do anel, varredura e pulso) **só rodam com as animações do Windows ligadas**
e enquanto a etapa trabalha — é um indicador de carregamento, não decoração.

```xml
<Grid Width="236" Height="236">
  <c:ProcessRing Value="{Binding Percent}" IsActive="{Binding IsRunning}" Tone="{Binding RingTone}" Thickness="13"/>
  <c:AnimatedNumber Value="{Binding Percent}" Kind="Percent" Decimals="0" FontFamily="{StaticResource Font.Numbers}" FontSize="58"
                    HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Grid>
<c:ScanlineOverlay IsActive="{Binding IsRunning}"/>   <!-- por cima de um painel, não recebe clique -->
<Ellipse Width="8" Height="8" Fill="{StaticResource Brush.Trace}" c:Pulse.IsOn="{Binding IsRunning}"/>   <!-- "ao vivo" -->
```

**Qualquer aplicar/desfazer de uma página** (`IModuleHost.ApplyAsync/RevertAsync`, página Aplicar) já ganha a tela de progresso
da janela (`ViewModels/ApplyOverlay.cs` + `MainWindow.xaml`, lógica em `Nexus.Assistant.HostApplyProgress`): não precisa fazer
nada na página. Uma página com tela própria (o Assistente) passa `IProgress<ApplyStage>` ao `ApplyAsync` e a da janela não aparece.

`ProcessRing`: 72 marcas que acendem com o %, arco com brilho, ponto na ponta e um arco ciano girando por dentro; `Tone`
Warn (aplicando), Info (lendo), Good (concluído), Bad (falhou). Leitor de tela: "Andamento: 45%".

### Abas — `Tab.Underline`
`RadioButton` com sublinhado âmbar; ícone + nome com um `ContentTemplate` local (veja `C.TabHeader` em Cleaning.xaml):

```xml
<RadioButton Style="{StaticResource Tab.Underline}" GroupName="MinhasAbas" Content="Quarentena" Tag="{StaticResource Icon.Quarantine}"
             ContentTemplate="{StaticResource C.TabHeader}" IsChecked="{Binding IsQuarantineTab, Mode=TwoWay}"/>
```

### Conversores do design system
`ResourceByKey` (texto → recurso: o VM escolhe o ícone por nome, ex. `IconKey = "Icon.Games"`), `ToneBrush` (`Tone` → pincel;
`ConverterParameter=soft` = fundo suave; `text` = cor do texto para o neutro), `PageIcon` (página → ícone;
`illustration` → ilustração), `InvertBool`. Os antigos continuam: `BoolToVis`, `FalseToVisible`, `NullToCollapsed`,
`ZeroToVisible`, `StatusTone`, `StatusText`…

### Navegar para outra página
Sem depender do MainViewModel: `ShellCommands.Navigate` com o **nome do tipo** do ViewModel.

```xml
<Button Content="Ver a memória" Command="{x:Static shell:ShellCommands.Navigate}" CommandParameter="MemoryLabPage"/>
```

### Movimento herdado
`ShellMotion.EnterPage` (a casca já aplica a cada troca de página), `ShellMotion.RevealIn` (revelar algo que apareceu por
ação do usuário), `ShellMotion.Count` (0,7 s), `Normal` (0,22 s), `Fast` (0,12 s) — todos viram zero com animações
desligadas. `Views/Optimize/Reveal.cs` (`Reveal.OnLoad`/`Reveal.Trigger`) continua disponível para listas.

---

## 4. Ilustrações e ícones por módulo

As "fotos" pedidas são **ilustrações vetoriais desenhadas para o NEXUS** (XAML `DrawingImage`): não há fotos de hardware
licenciadas para uso offline e o NEXUS não baixa imagens de terceiros. Vetores escalam sem perder nitidez, pesam poucos KB
e seguem a paleta. Todas têm 160×120, placa redonda escura com marcas de mostrador e sombra; âmbar marca a parte "ativa".

| Página (tipo do VM) | Ícone (navegação, cartões) | Ilustração (destaque, cartões, vazio) |
|---|---|---|
| Meu PC (`OverviewPage`) | `Icon.Pc` | `Illo.Pc` (monitor com frametime + gabinete) |
| Hardware (`HardwarePage`) | `Icon.Hardware` | `Illo.Pc` |
| Diagnóstico (`DiagnosticsPage`) | `Icon.Diagnostics` | `Illo.Diagnostics` (pulso) |
| Assistente (`AssistantPage`) | `Icon.Assistant` | `Illo.Assistant` (trilha de etapas com bandeira) |
| Otimização automática (`AutomationPage`) | `Icon.Auto` | `Illo.Automation` (varinha) |
| Processador (`CpuLabPage`) | `Icon.Cpu` | `Illo.Cpu` (chip com núcleo âmbar) |
| Placa de vídeo (`GpuLabPage`) | `Icon.Gpu` | `Illo.Gpu` (placa com duas ventoinhas) |
| Memória RAM (`MemoryLabPage`) | `Icon.Ram` | `Illo.Ram` (**1 de 2 slots ocupados**: pente + slot vazio tracejado) |
| Memória: diagnóstico (`MemoryDoctorPage`) | `Icon.MemoryDoctor` | `Illo.MemoryDoctor` (gráfico subindo = vazamento) |
| O que dá pra ajustar (`CatalogPage`) | `Icon.Catalog` | `Illo.Settings` |
| Testar sem aplicar (`PlanPage`) | `Icon.Plan` | `Illo.Settings` |
| Aplicar (`BrokerPage`) | `Icon.Apply` | `Illo.Security` |
| Limpeza (`CleaningPage`) | `Icon.Clean` | `Illo.Cleaning` (vassoura + partículas) |
| Espaço em disco (`DiskPage`) | `Icon.Disk` | `Illo.Disk` (SSD NVMe + barra de uso) |
| Programas (`ProgramsPage`) | `Icon.Programs` | `Illo.Programs` (caixas + seta de instalar) |
| Privacidade (`PrivacyPage`) | `Icon.Privacy` | `Illo.Privacy` (olho + escudo) |
| Segurança (`SecurityPage`) | `Icon.Security` | `Illo.Security` (escudo com ✓) |
| Verificar arquivos (`ScannerPage`) | `Icon.Scan` | `Illo.Scanner` (lupa sobre documento) |
| Inicialização (`StartupPage`) | `Icon.Startup` | `Illo.Startup` (foguete) |
| Serviços (`ServicesPage`) | `Icon.Services` | `Illo.Services` (engrenagens) |
| Ferramentas do sistema (`SystemToolsPage`) | `Icon.Tools` | `Illo.Network` (Wi‑Fi/rede) |
| Atualizar drivers (`DriverUpdatesPage`) | `Icon.Drivers` | `Illo.Drivers` (chip + seta) |
| Medir (`MeasurePage`) | `Icon.Measure` | `Illo.Measure` (velocímetro + frametime) |
| Monitor (`MonitorPage`) | `Icon.Monitor` | `Illo.Monitor` (tela com gráfico ao vivo) |
| Histórico (`HistoryPage`) | `Icon.History` | `Illo.History` (relógio + seta de desfazer) |
| Ajustes (`SettingsPage`) | `Icon.Settings` | `Illo.Settings` (controles deslizantes) |
| O que já funciona (`CapabilitiesPage`) | `Icon.Capabilities` | `Illo.Diagnostics` |

O mapa vive em `ShellMap` (a navegação usa `PageIcon`); em XAML use a chave direto (`{StaticResource Illo.Ram}`).
Ícones de apoio: `Icon.Check`, `Icon.Warning`, `Icon.Error`, `Icon.Info`, `Icon.Undo`, `Icon.Refresh`, `Icon.Export`,
`Icon.Lock`, `Icon.Trash`, `Icon.Quarantine`, `Icon.Search`, `Icon.Sparkle`, `Icon.Bolt`, `Icon.TrendUp`, `Icon.TrendDown`,
`Icon.ChevronDown/Up/Right`, `Icon.ArrowRight`, `Icon.Close`, `Icon.Plus`, `Icon.Windows`, `Icon.Browser`, `Icon.Apps`,
`Icon.Games`, `Icon.Code`, `Icon.User`, `Icon.Page`.

**Nova ilustração:** os dois XAML são gerados por `Assets/Illustrations/generate.py` (lista de traços por ilustração; a
moldura, a sombra, a placa e as marcas entram sozinhas). Acrescente uma entrada `I["Illo.Nova"] = [...]` desenhando dentro do
círculo (centro 80,58, raio 46) com corpo `#1F2840`, traço ciano 2 px e um único detalhe âmbar, inclua a chave em `GROUPS`,
rode `python3 generate.py . previa.svg` na pasta (a prévia abre com `rsvg-convert previa.svg -o previa.png`), e acrescente
a chave em `ShellMap` e na tabela acima. **Novo ícone:** grade 24×24, só traço, cabe entre 2 e 22.
O teste `Illustration_and_icon_keys_are_unique_and_well_formed` confere chave única e caminho válido.

---

## 5. Expor dados do ViewModel para a nova apresentação

- Números como `double` (NaN = ainda não sei) ao lado do texto existente: `RecoverableBytes` junto de `RecoverableText`.
  Não troque o texto antigo (outras partes podem usá-lo).
- Ícone escolhido pelo VM: `string IconKey => "Icon.Games"` + `ResourceByKey`.
- Tom: propriedade `Tone` (de `Nexus.Desktop.Views.Controls`) ou um `bool` + gatilho no XAML.
- Leitura extra (ex.: espaço livre) só leitura, em `Task.Run`, nunca escrita.
- **Propriedade só de leitura + controle que grava por padrão** (`CheckBox.IsChecked`, `ComboBox.SelectedItem`,
  `ToggleButton.IsChecked`, `ProgressBar.Value`…): use `Mode=OneWay` (`XamlBindingTests`). As propriedades dos componentes
  novos (`Number`, `Value`, `IsLoading`…) são de mão única; `ExplainToggle.IsExpanded` é de mão dupla (ligue só a algo gravável).
- Toda `{StaticResource X}` em `Views/**` precisa existir: `Every_static_resource_used_by_the_design_system_exists` falha no
  Linux antes de quebrar no Windows.

---

## 6. Checklist para redesenhar uma página

1. [ ] Qual é **o número** da página? (espaço, nota, FPS, itens, minutos) Exponha como `double` no VM se ainda não houver.
2. [ ] `HeroPanel` com a ilustração do módulo, título de 2–4 palavras, número, legenda de 1 linha e **um** verbo primário.
3. [ ] Carregando? `IsLoading` no hero e `SkeletonLoader` no lugar das listas. Apague "Lendo…"/"Medindo…" soltos.
4. [ ] A página altera o PC? `HowItWorks` com 3–4 passos (o último mostra o desfazer ou o irreversível).
5. [ ] Há efeito mensurável? `BeforeAfter` (ou `RingMeter` para %).
6. [ ] Listas viram cartões com distintivo de ícone; cada item: título + 1 número + selo. Explicações em `ExplainToggle`.
7. [ ] Avisos de risco continuam visíveis (`Panel.Notice` + `Icon.Warning`, `StatusChip Tone="Bad"`).
8. [ ] Vazio? `EmptyState` com o próximo passo.
9. [ ] Texto: nenhuma frase visível com mais de 2 linhas; sem caixa-alta; sem jargão; português do Brasil.
10. [ ] Botões: um primário; irreversível = `Button.Danger`; "Desfazer" visível depois de aplicar.
11. [ ] Acessibilidade: tudo alcançável por Tab, `AutomationProperties.Name` em caixas/combos sem rótulo, nada só por cor
     (selo tem ícone e texto).
12. [ ] `dotnet build` 0 avisos; `dotnet test` verde (inclui `DesignSystemTests` e `XamlBindingTests`).
13. [ ] No Windows: conferir com animações ligadas e desligadas, janela em 980 px e maximizada (seção 8).

---

## 7. Antes / depois — Limpeza

**Antes** (v1): título + subtítulo de 1 linha; bloco com "12,3 GB" estático, legenda longa ("recuperáveis com segurança (mais
3 item(ns) que só o administrador consegue medir). Medido agora, sem mexer em nada."), texto de seleção, texto de quarentena e
aviso; quatro botões empilhados; cada regra mostrava título, etiquetas, **explicação, ganho esperado e aviso** sempre
abertos (3–5 linhas por item, ~40 itens); Lixeira com parágrafo; "Ferramentas do Windows" com um parágrafo de 5 linhas sobre
WinSxS; histórico com 4 números em texto puro; aba Regras com parágrafo de 4 linhas.

**Depois** (v2):

| Parte | Antes | Depois |
|---|---|---|
| Topo | número estático + 4 linhas | `HeroPanel` com vassoura, **"12,3 GB" contando**, legenda de 1 linha, "Limpar selecionados" / "Desfazer última limpeza"; à direita "Marcado agora" contando e "Medir de novo" |
| Carregando | "Medindo o que pode ser liberado…" | esqueleto no lugar do número e das categorias |
| Como funciona | parágrafo da quarentena | `HowItWorks`: Varre → Guarda → Desfaz → Esvazia (último em vermelho = irreversível); texto longo em "Por que isso?" |
| Efeito | — | `BeforeAfter` do disco do Windows: livre agora → depois de esvaziar, com % de diferença |
| Categorias | cartão com título e total | cartão com distintivo de ícone (Windows, navegador, apps, jogos, código, usuário), nº de itens, total grande em âmbar, "Todos" |
| Itens | 3–5 linhas sempre abertas | título + etiquetas + tamanho; explicação e ganho em "Por que isso?"; **aviso de dados pessoais continua visível** |
| Lixeira | parágrafo | tamanho + selo "Esvaziar não tem desfazer" + botão perigo |
| Ferramentas do Windows | parágrafo de 5 linhas | 1 linha + "Por que isso?" com a explicação completa |
| Quarentena | número + 2 parágrafos | `HeroPanel` (relógio com desfazer) com botões perigo; nota recolhida; lote com ícone e "Restaurar" com ícone; vazio = `EmptyState` |
| Histórico | 4 números em texto | 4 `StatTile` (verde = recuperado de verdade, âmbar = na quarentena); fonte dos números recolhida; vazio = `EmptyState` |
| Abas | só texto | ícone + texto (`Tab.Underline`) |

Lógica inalterada: o ViewModel só ganhou números para a apresentação (`RecoverableBytes`, `SelectedBytes`, `FreeBytes`,
`FreeAfterBytes`, `SystemDrive`, `HasBatches`, `Recovered*Bytes`, `IconKey`/`CountText` das categorias).

---

## 8. Conferir no Windows (WPF não roda no Linux)

O build e os testes conferem tipos, propriedades, bindings de mão dupla e chaves de recurso, mas **não desenham**. No PC:

1. Abrir o NEXUS: a navegação mostra um ícone por página; o item ativo tem fundo, marca âmbar que cresce e ícone âmbar.
2. Cabeçalho: selo verde "Nada muda sem você confirmar · Por que isso?" — clicar abre o balão; clicar fora fecha.
3. Meu PC: ilustração do PC, nome do modelo em âmbar, anel "em ordem" enche e conta; quatro cartões (processador, placa de
   vídeo, memória com selo "canal único" âmbar no Acer Nitro de 1 pente, disco com % usado) — clicar abre a página; três
   recomendações com botão; "Como o NEXUS trabalha"; "Ver tudo o que foi lido" abre a lista.
4. Limpeza: número conta; esqueleto durante a medição; Varre → Guarda → Desfaz → Esvazia; barras do antes/depois enchem e o
   selo mostra a diferença; cartões das categorias com ícone; "Por que isso?" em cada item abre com fade.
5. Configurações do Windows › Acessibilidade › Efeitos visuais › **Efeitos de animação desligado**: reabrir — números aparecem
   prontos, anel e barras cheios, sem fade nem pulso.
6. Janela em 980 px de largura (mínimo) e maximizada: nada cortado no hero; textos longos com "…" e dica.
7. Narrador (Win+Ctrl+Enter): cartões anunciam "Processador: 6 núcleos. AMD Ryzen…", anel anuncia "em ordem: 83%",
   esqueleto anuncia "Carregando"; Tab percorre cartões clicáveis e "Por que isso?".

---

## 9. Interface simples para todos (02/11/2026)

Pedidos do responsável: *"nosso aplicativo ficou com muita informação e ficou confuso, vamos facilitar a vida do usuário
leigo"* e, em seguida, *"quero que o modo simples seja para todos usuários, quero que a interface seja fácil de entender e
aplicar as modificações"*. Não há escolha de modo: a interface é uma só.

| Parte | Como ficou |
|---|---|
| Navegação | 6 itens, ícone grande, uma palavra, sem grupos: Início, Otimizar, Limpar, Jogos, Problemas, Histórico. No pé, um link discreto "Ferramentas avançadas" |
| Ferramentas avançadas | uma página (`AdvancedToolsPage`) com as 27 páginas de antes em ladrilhos (ícone, nome, uma linha); Ajustes primeiro; "Exportar leitura" e "Ver tudo (administrador)" ficam aqui. Uma página aberta fora da lista lateral mostra a barra "Voltar ao início · Ferramentas avançadas" |
| Cabeçalho | o selo de segurança e o botão de reler (ícone) |
| Primeira abertura | boas-vindas em 2 telas (o que faz; seguro e tudo se desfaz), só para quem nunca usou. Quem já usava não vê aviso. O antigo `UiMode = "full"` é migrado para `"simple"` (`UiModeStartup.Decide`) |

**Um clique onde a recomendação aparece.** Fluxo único (`ViewModels/Simple/OneClick.cs`): clique → uma frase de
confirmação → aplica pelo aplicador (`IModuleHost.ApplyConfirmedAsync`, por nível: sua conta, depois sistema com um aviso do
Windows) → "✓ Feito — Desfazer" no próprio lugar (`OneClickAction`, componente XAML `S.OneClick`). Desfazer volta as execuções
pelo aplicador. O cartão/linha em que o usuário clicou fica à vista mesmo depois da releitura.

- **Início**: anel (`ProcessRing`, o único elemento marcante) com a palavra da saúde (`SimpleHealth.Compute`) e um botão
  "Otimizar meu PC com segurança". No máximo 3 cartões (`ImproveCards.Top`). O botão do cartão faz o seguinte:
  - lixo recuperável ou pouco espaço: limpa as regras seguras (`CleaningPage.SafeRequests`);
  - "N ajustes seguros ainda não foram feitos": aplica os recomendados dos Ajustes rápidos;
  - achado com ação de risco baixo no catálogo (`ISimpleActions.LowRiskRequest`): "Resolver";
  - driver de vídeo: abre Atualizar drivers já procurando a versão nova (baixar e instalar tem tela própria);
  - memória num canal só ou BIOS: "Ver como" (é com o usuário).
- **Otimizar**: o Assistente completo no modo automático (`AssistantPage.RunAutomaticAsync`): uma confirmação, só o que
  `AutoPilot.IsAutomatic` aceita, resumo "Pronto! Liberamos X e ajustamos Y coisas.", "Ver detalhes" e "Desfazer tudo".
  Abaixo, **Ajustes rápidos** (`QuickTweaksPanel`):
  - as recomendações do Automático para este PC, em Velocidade, Jogos, Privacidade e Energia (`QuickTweakText`);
  - cada uma com caixa de marcar, uma frase e "ligado" lido do estado atual (Detect);
  - marcar aplica; desmarcar desfaz a execução que ligou só aquele ajuste (desta sessão ou do diário);
  - por padrão só as recomendadas de risco baixo; "Mostrar mais opções" mostra o resto com a palavra de risco ("pode gastar
    mais bateria", "experimental: pode não ajudar").
- **Limpar**: "X GB podem ser liberados", "Limpar agora" (só regras seguras, para a quarentena), lista em "Ver o que será limpo".
- **Jogos**: Modo de jogo do Windows com "Ligar" de um clique. Por jogo: "Medir FPS" e cada opção que vale com "Ligar" de um
  clique (driver NVIDIA por jogo, jogos em janela).
  - Cada opção mostra o estado REAL, lido de onde a ação escreve (`GameOptionStates.cs`, lógica pura testada em
    `GameOptionStateTests`): Modo de jogo pelo registro (releitura do catálogo depois de aplicar), driver NVIDIA pelo leitor do
    DRS em cada executável (o da biblioteca e, se ele só abre o jogo, o que desenha: `GameAliases`), jogos em janela pelo token
    `SwapEffectUpgradeEnable` de UserGpuPreferences, Fortnite pelo GameUserSettings.ini, Sessão de jogo por `game-session.json`.
  - Ligada: "✓ Ligado — Desfazer" quando o diário tem a execução que só mexeu nela (chave da ação + alvo do jogo); feita junto
    com outras coisas: "Desfazer no Histórico"; ligada por fora do NEXUS: só "✓ Ligado". Valor diferente: "Está com outro valor".
  - Os botões são os mesmos entre aberturas da tela; a cada abertura e depois de aplicar/desfazer, relê só aquele jogo.
  - "Comparar com e sem" (jogos com opções): abre o **Laboratório A/B** já com o jogo escolhido — uma partida sem, o NEXUS aplica,
    uma partida com; só fica o que melhorar de verdade (`docs/modules/ablab.md`).
- **Início/Problemas depois de aplicar**: o diagnóstico só é refeito na próxima leitura do PC; até lá, um achado cuja ação do
  catálogo já está como ela deixa (releitura "same") conta como resolvido (`ResolvedFindings`) e não volta como pendente.
- **Problemas**: achados em palavras simples (`PlainFindings`), "O que fazer" em 1–2 frases e "Resolver" de um clique quando há
  ação de risco baixo. O que está bem fica recolhido.
- **Checagens do PC** (`docs/modules/pc-checks.md`): rede para jogos, saúde dos discos, monitor abaixo da taxa, limitação do
  processador pelo firmware. Entram em Problemas com os números da leitura (`PcCheckText`); disco falhando e monitor abaixo da
  taxa também viram cartão no Início ("Usar 144 Hz" leva à Placa de vídeo, que volta sozinha em 15 s). Notebook na bateria ao
  abrir um jogo: aviso na bandeja (desligável em Ajustes).
- **Histórico**: os dois diários, "Hoje 14:30 — Limpeza: 12 itens limpos — Desfazer" (`SimpleHistoryText`), "Desfazer tudo".
- Nunca aparecem chave de ação, estado cru do aplicador nem valor técnico.
- Testes: `SimpleModeTests` (navegação, migração, nota, cartões, achados, histórico, ajustes rápidos, seleção do automático).
- O autoteste da interface abre as 6 telas, as 2 de boas-vindas, Otimizar de demonstração, Ferramentas avançadas e cada
  ferramenta dela.
