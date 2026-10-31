# Laboratório da memória RAM (`src/Nexus.Lab.Memory`, página "Memória RAM" no grupo Laboratório)

"Engenharia reversa" da memória sem driver de kernel: o que o firmware informa (SMBIOS), o que o número de peça revela
e o que a medição comprova. Quase tudo é leitura. A única escrita é uma ação reversível do catálogo (arquivo de paginação
automático), que vai pelo aplicador.

No PC do responsável (Acer Nitro AN515-45, 1 × 8 GB DDR4-3200 SODIMM) o cabeçalho deve mostrar
**"8 GB DDR4-3200 · 1 módulo · SINGLE CHANNEL"** (em vermelho), o diagrama "Canal A: DIMM 0 8 GB · Canal B: vazio",
e o achado "Memória em single channel" recomendando um segundo módulo igual no slot livre.

## O que faz

| Área | O que mostra | Fonte / método |
|---|---|---|
| Módulos | slot (locator/bank), fabricante (nome ou código JEDEC → nome), número de peça, série, tamanho, tipo (DDR3/DDR4/DDR5/LPDDR4/LPDDR5…), forma (DIMM/SODIMM/soldada/CAMM), velocidade nominal e configurada (MT/s), ranks, largura de dados (e ECC), tensões mín/máx/configurada, registrada ou não | SMBIOS tipo 17 lido com `GetSystemFirmwareTable('RSMB')` (sem administrador). Decodificação própria em `SmbiosMemory` (DSP0134 3.x: tamanho estendido 0x7FFF, tamanho em KB, velocidade estendida 0xFFFF, ID JEDEC do módulo 3.2+). A separação em estruturas reutiliza `SmbiosParser.Parse` de `Nexus.HardwareDiscovery` (só leitura, sem editar). |
| Slots vazios e capacidade | posições sem módulo, capacidade máxima da placa, número de posições, memória soldada vs slot livre | tipo 16 (Physical Memory Array, só "memória do sistema"; capacidade estendida) + tipo 17 com tamanho 0. Soldada: forma "chip/linha de chips/die" ou nome "Onboard/Soldered/Memory Down". Notebook: tipo 3 (gabinete). |
| Número de peça | marca, linha, geração, velocidade e latência anunciadas, tamanho do módulo e do kit, SODIMM, organização (ranks × largura), perfil (JEDEC/XMP/EXPO) e grau de confiança | `PartNumberDecoder`: regras por fabricante a partir da nomenclatura pública (Kingston Fury/HyperX/KHX/ValueRAM/KCP/OEM Acer-HP-Dell-Lenovo, Corsair, G.Skill DDR4/DDR5, Crucial DDR4/DDR5/Ballistix, Samsung, SK hynix, Micron DDR4/DDR5, ADATA/XPG, TeamGroup, Ramaxel). CL que não está no número vem da tabela JEDEC e é marcado "(padrão JEDEC)". Sem regra = "desconhecido" (nada é deduzido). |
| Canais | canal de cada slot, single/dual/quad, canal vazio, modo assimétrico (flex) | `ChannelInference`: nomes de Acer/AMD ("DIMM 0" + "P0 CHANNEL A"), Intel/Lenovo ("ChannelA-DIMM0"), ASUS/MSI ("DIMM_A1", "DIMMA1"), Gigabyte ("DDR4_A1"), ASRock ("A1"), Dell ("DIMM A"), LPDDR ("Controller0-ChannelA"). Sem nome de canal (HP), vira estimativa marcada e a medição decide. LPDDR: largura total do barramento (ex.: 8 × 16 bits = 128 bits = equivalente a dual). |
| Banda | leitura, escrita e cópia em GB/s vs teórico, eficiência %, canais confirmados | `MemoryBenchmark.MeasureBandwidth`: dois blocos alinhados (64–256 MB cada, 1/10 da RAM livre), uma thread por processador lógico com largada simultânea (Barrier), AVX/SSE2 com escrita não temporal (Vector128 / escalar se não houver), 1 passada de aquecimento + 7 medidas, mediana. Teórico = MT/s × largura do barramento em bytes (`BandwidthTheory`). |
| Latência | ns por acesso | `MeasureLatency`: bloco de 128 MB–1 GB (1/5 da RAM livre, ≫ L3) ligado numa única volta aleatória (Sattolo) de linhas de 64 B; cada leitura depende da anterior. Duas voltas: "aleatória" (inclui falta de TLB) e "por página" (as 64 linhas de cada página antes da próxima: quase sem custo de TLB). Páginas grandes de 2 MB se a conta tiver "Bloquear páginas na memória" (SeLockMemoryPrivilege — padrão do Windows é não ter); senão páginas de 4 KB, com explicação. |
| Estado do Windows | em uso, memória comprometida e limite, pico de commit desde o boot, lista de espera, faltas de página (total e lidas do disco), compressão de memória, arquivo de paginação (modo, onde, tamanho atual), SysMain (referência à página Serviços), Prefetch | `GlobalMemoryStatusEx`, `K32GetPerformanceInfo`, PDH (`\Memory\Standby Cache *`, `Modified Page List Bytes`, `Free & Zero Page List Bytes`, `Page Faults/sec`, `Pages Input/sec`), `Get-MMAgent` em PowerShell oculto (sem perfil, 20 s de limite; sem ele, presença do processo "Memory Compression"), registro `Memory Management\PagingFiles` e `PrefetchParameters\EnablePrefetcher`. |
| Comparação | medições anteriores (data, configuração, leitura/escrita/cópia, latência) e variação da leitura vs a anterior | `%LocalAppData%\NEXUS\lab\memory\yyyyMMdd-HHmmss.json` (`MemoryLabStore`). |

Tudo em `Task.Run`; a medição é cancelável (botão Cancelar) e nunca derruba a janela.

### Exemplo de leitura da medição

DDR4-3200 em um canal: 3200 × 10⁶ × 8 B = **25,6 GB/s** teóricos. Um canal nunca entrega mais que o próprio teórico, então:

- leitura medida > 108% do teórico de UM canal → **prova** pelo menos dois canais;
- entre 45% e 108% → compatível com um canal (com o SMBIOS mostrando um canal só, "single channel confirmado");
- abaixo de 45% → medição atrapalhada (outro programa, economia de energia): pede para repetir.

"Seu PC mede 22 GB/s; um canal desta memória tem 25,6 GB/s teóricos → compatível com um só canal."

## Diagnóstico (`MemoryDiagnosis`, cada achado com evidência e fonte)

| Achado | Quando | Recomendação |
|---|---|---|
| Single channel | um canal ocupado e não desmentido pela medição | slot livre: "instale um módulo igual (mesmo tamanho/geração/MT/s; de preferência o mesmo número de peça) → ~2× a banda", passos para notebook/desktop; soldada + slot livre: módulo do tamanho da soldada; sem slot livre: trocar por 2 iguais, conferir manual |
| Perfil XMP/EXPO desligado (PC de mesa) | configurada < 97% da nominal do SMBIOS ou da anunciada pelo número de peça | passo a passo ASUS / MSI / Gigabyte / ASRock / genérico; o NEXUS não mexe no BIOS |
| Velocidade limitada (notebook) | idem, em notebook | explica que é limite da plataforma; notebooks quase nunca têm XMP/EXPO |
| Módulos misturados | tamanhos, velocidades ou modelos diferentes | todos rodam na velocidade do mais lento; tamanhos diferentes = parte em single |
| Kit incompleto | número de peça de kit × N com menos módulos instalados | o par do kit é o ideal |
| Pouca RAM para jogos | ≤ 8 GB (grave se pico de commit > 90% da RAM ou > 50 páginas/s lidas do disco) | 16 GB (2 × 8 GB em dual) |
| Memória perto do limite | > 8 GB com pressão medida | ver no Monitor quem usa |
| Paginação desligada / fixa e pequena | sem arquivo, ou máximo < metade da RAM com ≤ 16 GB | ação reversível "voltar para automático" |
| Compressão desligada | `Get-MMAgent` ou processo ausente | informação: como religar (Enable-MMAgent), sem ação automática |
| Banda muito baixa | medição inconclusiva | medir com carregador e PC parado |
| Dual channel funcionando | dois canais e medição acima de um canal | nada a fazer |

### Impacto em jogos (faixas, nunca um número prometido)

- TechSpot (Ryzen 7 9700X + RTX 5090, DDR5-6000): um pente vs dois → ~12% a menos na média em 1080p, ~16% nos 1% mais
  lentos, casos de até 33% nos 1% lows; quase nada em alguns jogos. <https://www.techspot.com/article/3066-single-stick-vs-dual-channel-ram/>
- Tom's Hardware (Ryzen 7 4800H + RTX 2060): 2 × 8 GB ~11,7% mais rápido que 1 × 8 GB. <https://www.tomshardware.com/features/ryzen-4000-ram-guide-picking-the-best-ram-for-your-laptop>
- UltrabookReview (Zephyrus G14, Ryzen 7 4800HS + RTX 2060): 3% a 19% em 1080p, menos em 1440p. <https://www.ultrabookreview.com/36308-single-dual-channel-ram-amd>
- LaptopMedia (i5-11300H/i7-11370H + RTX 3050): 18% a 72% em cenas leves para a GPU (preset mínimo). <https://laptopmedia.com/highlights/the-bottleneck-effect-gaming-performance-with-single-vs-dual-channel-memory/>
- GPU integrada: usa a RAM como memória de vídeo, então metade da banda pesa direto (mecanismo; sem número publicado nessas fontes).

## Ação de catálogo (`catalog/memorylab.v1.json`)

| Id | Handler | O que escreve | Privilégio | Reinício |
|---|---|---|---|---|
| `memorylab.pagefile.automatic` | `MemoryLabPageFile` (`PageFileHandler`) | `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles` = `?:\pagefile.sys` (REG_MULTI_SZ) — o mesmo que "Gerenciar automaticamente o tamanho do arquivo de paginação de todas as unidades" | Admin (UAC + ponto de restauração automático) | Necessário |

- Alvo **fixo no código**: o catálogo só declara `{"setting": "PagingFiles"}`; qualquer outro alvo ou valor é bloqueado no
  Assess e no CheckRestore.
- Detect lê o valor (tipo + dados); o executor grava no diário antes de escrever e relê depois.
- **Desfazer**: regrava o valor anterior exatamente (MultiString/String/ExpandString) ou remove o valor se não existia. Vale
  depois de reiniciar.
- Oferecida só nos achados "paginação desligada" e "fixa e pequena". Fontes: Microsoft, *How to determine the appropriate
  page file size* e *Introduction to the page file*.

## O que NÃO faz, e por quê

- **Timings (tCL/tRCD/tRP/tRAS) e SPD direto**: exigem ler o chip SPD pelo SMBus da placa, que só um driver de kernel
  acessa. O NEXUS não instala driver. A página explica e indica CPU-Z / utilitário da placa. A CL mostrada vem do número de
  peça (ou da tabela JEDEC, marcada como tal).
- **Não liga XMP/EXPO**: é ajuste de BIOS; damos o passo a passo por fabricante.
- **"Limpador de RAM"**: mito. A lista de espera é cache que o Windows devolve na hora; esvaziar à força faz os programas
  lerem do disco de novo. A página explica.
- **Não mexe em SysMain/Prefetch**: só mostra e remete à página Serviços (o módulo dono).
- **Não religa a compressão automaticamente**: é raro alguém desligar; mostramos o comando oficial.
- **LPDDR4X/LPDDR5X**: o SMBIOS não distingue de LPDDR4/LPDDR5; mostramos o que ele informa.

## Riscos

- Medição: aloca até ~0,5 GB (banda) e ~1 GB (latência) **limitados pela memória livre** (1/10 e 1/5); usa todas as threads
  por ~15 s. Nada é escrito no sistema além do JSON da medição.
- Páginas grandes: o NEXUS só ATIVA no próprio processo um direito que a conta já tenha (`AdjustTokenPrivileges`); não
  concede direitos nem altera políticas.
- Ação de paginação: baixo risco; ocupa espaço no disco do sistema; reversível.

## Testes

- `tests/Nexus.Tests/Modules/MemoryLabTests.cs` (Linux): tabelas SMBIOS binárias montadas no teste (Acer Nitro real,
  tamanho/velocidade estendidos, matriz de vídeo, soldada, truncada), códigos JEDEC, 34 números de peça de 9 fabricantes, mais
  Ramaxel, OEM e casos parciais, e desconhecidos (nunca inventa), canais de Acer/Lenovo/Dell/ASUS/MSI/Gigabyte/ASRock/HP/LPDDR5, teórico vs medido, diagnóstico
  com fatos falsos, handler com registro em memória (aplicar, restaurar ausente, restaurar valor anterior, alvo adulterado),
  catálogo, medições reduzidas (8 MB / 4 MB), cancelamento e armazenamento.
- `tests/Nexus.IntegrationTests/Modules/MemoryLabIntegrationTests.cs` (VM Windows): leitura real do SMBIOS/estado e a ação de
  paginação aplicada, conferida e desfeita pelo BrokerService.
