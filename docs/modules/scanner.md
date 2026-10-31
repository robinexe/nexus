# Módulo Verificar arquivos (`src/Nexus.Security.Scanner`, página "Verificar arquivos", grupo "Proteger")

> **O Microsoft Defender continua sendo a proteção principal.** Este verificador procura *sinais suspeitos* com
> explicação, e cada achado tem o botão "Verificar com o Defender". "Suspeito" não é "malware": só a correspondência com
> uma lista de hashes que **você** importou afirma identidade, e mesmo assim a palavra final é do Defender.

## O que faz

Motor local, **somente leitura**, paralelo e cancelável. Lista arquivos sem seguir links/junções (`FileAttributes.ReparsePoint`),
pula arquivos que só existem na nuvem (OneDrive: `Offline`, `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, para não forçar
download), não repete pastas, respeita profundidade máxima (48), lê cada arquivo uma vez com `FileShare.ReadWrite | Delete`
(não atrapalha quem está usando; arquivo bloqueado conta como "pulado") e calcula SHA-256. Três camadas, cada sinal com
título, "por que é suspeito" e pontos:

### a) Lista de hashes (opcional, formato aberto)

- O NEXUS traz **só um conjunto de teste** (`Hashes/test.hashes.txt`, embutido): o SHA-256 do EICAR
  (`275a021b…51fd0f`) e de uma amostra própria (`"NEXUS-SCANNER-TEST-SAMPLE\n"`). Nenhum hash de malware real foi inventado
  nem é baixado. Achado de teste aparece como "Arquivo de teste (não é ameaça)".
- **Importar uma lista** (botão "Importar lista de hashes…"): arquivo de texto, uma entrada por linha. Vale o primeiro
  token de 64 hexadecimais da linha (MD5 e SHA-1 têm outro tamanho e são ignorados), o resto vira rótulo. Comentários com
  `#`; `#! test` marca as linhas seguintes como amostras de teste, `#! real` volta ao normal. CSV também serve, então
  exportações de fontes públicas (por exemplo, o CSV de amostras recentes do MalwareBazaar, abuse.ch, ou listas de IoC
  publicadas por fornecedores) podem ser importadas como estão. O arquivo é validado (precisa ter ao menos um hash) e
  copiado para `%LocalAppData%\NEXUS\scanner\hashes\`; o NEXUS nunca baixa nada sozinho. Limite: 2 milhões de entradas,
  200 MB por arquivo.
- Confira a origem da lista: um hash na lista faz o arquivo aparecer em "Na sua lista de hashes".
- A camada de hashes cobre os arquivos cujo conteúdo é lido (executáveis, scripts, atalhos, documentos OLE, e qualquer
  arquivo até 4 KB nas verificações completa/personalizada); arquivos acima de 512 MB recebem só a heurística de nome.

### b) Regras de padrão (subconjunto próprio inspirado no YARA, JSON `nexus.scanrules/1`)

Arquivo `Rules/default.rules.json` (embutido) mais os que o usuário importar (`%LocalAppData%\NEXUS\scanner\rules\*.json`).

```json
{
  "schema": "nexus.scanrules/1",
  "rules": [{
    "id": "script.download-execute", "title": "…", "why": "…(aparece para o usuário)", "points": 45,
    "test": false, "extensions": [".ps1", ".bat"], "minSize": 0, "maxSize": 8388608, "requirePe": false,
    "strings": [
      { "id": "iex", "text": "invoke-expression", "nocase": true, "wide": true },
      { "id": "mz",  "hex": "4D 5A ?? ?? [2-4] 50 45", "at": 0 },
      { "id": "s",   "text": "x", "section": ".text" }
    ],
    "condition": "$mz and (2 of ($iex, $s) or not $s)"
  }]
}
```

- Padrões: `text` (ASCII/UTF-8; `nocase`; `wide` = também UTF-16LE; `ascii: false` = só UTF-16) ou `hex` com `??`
  (qualquer byte) e saltos `[n]`/`[n-m]` (até 1024, no máximo 4 por padrão; precisa começar e terminar em byte fixo).
  Restrições: `at` (deslocamento exato), `section` (nome da seção do PE, `entry` = seção do ponto de entrada, `overlay`).
- Condição: `$id`, `and`, `or`, `not`, parênteses, `N of (...)`, `any of`, `all of`, `them`, curinga `$prefixo*`.
- Limites: 500 regras, 64 padrões por regra, 2 MB por arquivo; erro de formato recusa o arquivo inteiro com o id da regra.

Regras embutidas (genéricas; procuram **técnicas**, não malware específico):

| Regra | O que procura | Pontos |
|---|---|---|
| `test.eicar` | Arquivo de teste EICAR (no início, até 128 bytes). Guardado em hex na regra para o próprio JSON não ser "um EICAR". | 100 (teste) |
| `script.download-execute` | IEX/Invoke-Expression + DownloadString/WebClient/Invoke-WebRequest/BITS (T1059.001 + T1105) | 45 |
| `script.powershell-encoded` | `powershell` + `-enc`/`-EncodedCommand` + janela oculta/FromBase64String/IEX (T1027) | 40 |
| `lnk.script-host` | Atalho `.lnk` que chama mshta, wscript/cscript com URL/.vbs/.js, ou PowerShell/cmd com hidden/-e/http/iex (T1204.002) | 45 |
| `office.ole-autoexec-macro` | Documento OLE (Office 97–2003) com `_VBA_PROJECT` e AutoOpen/Document_Open/Workbook_Open… (T1137) | 35 |
| `office.ole-macro-launcher` | Mesmo documento com WScript.Shell/URLDownloadToFile/XMLHTTP/PowerShell | 25 |
| `script.vbs-js-dropper` | VBS/JS/HTA com XMLHTTP + ADODB.Stream + WScript.Shell | 45 |
| `script.defender-tamper` | Set-MpPreference -Disable…/Add-MpPreference -Exclusion… (T1562.001) | 45 |
| `pe.embedded-script-launcher` | Executável com comando de PowerShell oculto/codificado embutido | 30 |

Limitação honesta: macros em `.docm/.xlsm` (zip) e em VBA comprimido podem escapar; o Office já bloqueia macros de
arquivos da internet por padrão.

### c) Heurística de nome e de PE (leitor de PE próprio, testado com binários montados nos testes)

Nome (qualquer arquivo): caractere de controle de direção/RTLO (60), extensão dupla `fatura.pdf.exe` (45), muitos
espaços antes de uma extensão executável (40).

PE (`PeFile`: cabeçalhos, seções, importações, diretório de depuração, tabela de certificados, overlay; toda leitura
confere limites, tetos de 96 seções/256 DLLs/8192 funções, funciona com trecho parcial):

| Sinal | Pontos |
|---|---|
| Seção executável e gravável (W+X) | 20 |
| Entropia ≥ 7,2 em seção executável ≥ 4 KB (empacotado/criptografado) | 20 |
| Seções de empacotador conhecido (UPX, MPRESS, Themida, VMProtect…) | 10 |
| Imports de injeção: VirtualAllocEx + WriteProcessMemory + CreateRemoteThread (ou Nt*/QueueUserAPC/SetThreadContext) | 30 |
| Imports de captura de teclado: SetWindowsHookEx + GetAsyncKeyState/… + GetForegroundWindow/… | 25 |
| Só LoadLibrary + GetProcAddress (≤ 6 imports) ou nenhum import (exceto .NET) | 15 |
| Data de compilação no futuro ou antes de 1992 (ignorado em build reproduzível: entrada REPRO) | 5 |
| Nome de processo do Windows (svchost, lsass…) fora da pasta do Windows | 35 |
| Nome imitando processo do Windows: 1 letra trocada, 2 vizinhas trocadas, homóglifos 0/o 1/l i/l rn/m (svch0st, scvhost, Isass) | 40 |
| Executável criado há menos de 7 dias em Temp/Downloads/AppData/Pública | 10 |
| Overlay > 1 MB e ≥ 30% do arquivo (descontada a assinatura digital) | 10 |
| Sem assinatura Authenticode válida: em pasta de risco 15, fora dela 5 | 15/5 |

Assinatura: reaproveita `Nexus.Security.Authenticode.IsSigned` (WinVerifyTrust, sem rede, revogação só em cache). Só é
consultada para executáveis em pastas de risco ou que já somam ≥ 15 pontos (é a parte cara). **Assinatura válida limita os
pontos da heurística de PE a 15** (abaixo de "para revisar"), para evitar falso positivo em programas legítimos que usam
compressão/overlay/ganchos; regras de padrão e hashes continuam valendo.

### Pontuação

Soma dos pontos: ≥ 20 "Para revisar", ≥ 45 "Suspeito", ≥ 75 "Muito suspeito"; hash na sua lista = "Está na sua lista de
hashes"; só sinais de teste = "Arquivo de teste". A interface agrupa por isso e sempre diz que suspeito ≠ malware.

## Modos

- **Rápida:** Downloads (pasta conhecida real, `SHGetKnownFolderPath`), Área de Trabalho (sua e pública), `%TEMP%`,
  `%AppData%`, `%LocalAppData%`, Pública, pastas Inicializar (sua e de todos) e os executáveis apontados por Run/RunOnce
  (HKCU, HKLM 64 e 32 bits). Só tipos executáveis/scripts/atalhos/documentos OLE.
- **Completa:** as unidades fixas marcadas (todas as pastas, exceto links, `System Volume Information`, `WinSxS` e a
  quarentena do NEXUS); também arquivos pequenos de qualquer tipo.
- **Personalizada:** pasta ou arquivos escolhidos em diálogo; também lê o começo de arquivos de tipo desconhecido para achar
  executáveis renomeados (`MZ`).
- **Ao vivo (opcional):** `FileSystemWatcher` em Downloads enquanto a página está aberta (desliga ao trocar de página);
  espera o arquivo ficar parado alguns segundos e ignora `.crdownload/.part/.tmp`.

Progresso: arquivos, bytes, pulados, arquivos/s, tempo e o arquivo atual; botão Cancelar.

## Ações por achado

| Ação | Como | Escreve? |
|---|---|---|
| Verificar com o Defender | `MpCmdRun -Scan -ScanType 3 -File <arquivo>` via `DefenderScanner.ScanAsync` do módulo Segurança, depois de confirmação (exceção controlada do brief). Remoção/quarentena do Defender ficam com ele. | Não (o Defender decide) |
| Mostrar no Explorer | `explorer.exe /select,"<arquivo>"` | Não |
| Propriedades e assinatura | `SHObjectProperties` (janela Propriedades, aba Assinaturas Digitais) | Não |
| Ignorar | Exceção por caminho + SHA-256 em `%LocalAppData%\NEXUS\scanner\ignore.json` (dados do próprio NEXUS). Se o arquivo mudar, volta. | Só a lista do NEXUS |
| Isolar | Quarentena reversível pelo aplicador (abaixo) | Sim, pelo aplicador |

### Quarentena reversível (`catalog/scanner.v1.json`, handler `ScannerQuarantine`)

| Ação | Nível | Alvo |
|---|---|---|
| `scanner.quarantine.user.01`…`10` | User | `scope=HKCU` (o broker como serviço grava o SID de quem pediu), `path`, `sha256` |
| `scanner.quarantine.machine.01`…`04` | Admin (UAC + ponto de restauração) | `scope=HKLM`, `path`, `sha256` |

- `path` validado pelo catálogo com a mesma regex de caminho das sobras de programas (absoluto com letra de unidade, sem
  `..`, sem caracteres proibidos, até 8 níveis de 120 caracteres; `\z`), `sha256` = `[0-9a-f]{64}`.
- O handler revalida tudo (`QuarantinePolicy`): pasta do Windows e pasta de dados do NEXUS **nunca**; Arquivos de Programas,
  ProgramData e perfis de outras contas só pela vaga de administrador com o aplicador elevado; sem SID com o aplicador em
  outra conta = recusa; caminho precisa estar normalizado; nem o alvo nem nenhuma pasta acima pode ser link/junção; só
  arquivos (até 256 MB).
- **Hash revalidado:** o SHA-256 atual precisa ser o da verificação (senão "mudou desde a verificação", bloqueado) e é
  conferido de novo depois de mover; se diferir, o arquivo volta ao lugar e a operação falha.
- Manifesto (`nexus.scanner.quarantine/1`: escopo, SID, caminho original, SHA-256, tamanho, data, estado pending/done)
  gravado **antes** de mover, em `DataDirectory\quarantine\scanner\<id>\manifest.json`; o conteúdo vai para `payload.bin`.
  Mesmo volume: renomeação (preserva a marca "baixado da internet"); outro volume: cópia com a origem travada para escrita,
  conferência de SHA-256 e de que a origem não mudou, só então remoção da origem.
- Estados: `present` (no lugar, mesmo hash), `changed`, `quarantined`, ausente. **Restore** confere o SHA-256 da
  quarentena contra o manifesto, revalida a política e devolve; se algo novo ocupar o caminho, restaura ao lado como
  `nome (restaurado pelo NEXUS).ext`, sem sobrescrever. Vaga de administrador: manifesto precisa ser de Administradores
  (`AdminOwnedFile.Check`).
- **Nunca apaga.** Esvaziar a quarentena não existe nesta versão (de propósito).

## Riscos

- Falso positivo: instaladores, ferramentas de sistema, jogos com anti-cheat, scripts de administração podem somar pontos.
  Por isso os limiares são conservadores, a assinatura válida limita a heurística, e isolar é desfazível.
- Isolar um arquivo de um programa legítimo faz o programa parar até o Desfazer.
- A verificação completa usa disco e CPU (paralelismo = metade dos núcleos, máx. 6); pode ser cancelada.

## O que NÃO faz, e por quê

- **Não substitui o Defender** nem promete detectar malware específico; não há assinaturas de famílias reais.
- **Não baixa listas nem regras** (sem rede, sem telemetria). Importar é sempre manual.
- **Não apaga arquivos** e não "desinfeta": remoção definitiva é com o Defender (que sabe restaurar falso positivo).
- **Não isola nada da pasta do Windows** (peça ao Defender) nem arquivos em uso.
- **Não segue links, não lê arquivos só na nuvem** (pastas do OneDrive com arquivos sob demanda aparecem como "pulados").
- **Não desempacota zip/OOXML** (macros em .docm/.xlsm): fora do escopo de um verificador leve.

## Comparação com o Kudu

| Kudu (Malware Scanner) | NEXUS (Verificar arquivos) |
|---|---|
| Assinaturas + heurística + Defender | Lista de hashes aberta e importável (sem hashes inventados), regras de padrão documentadas em JSON, heurística de PE com explicação por sinal e pontuação transparente, botão do Defender por arquivo |
| Quarentena/remoção | Quarentena reversível pelo aplicador com diário, manifesto, SHA-256 revalidado antes/depois, recusa de alvos do sistema; nunca apaga |
| — | Verificação ao vivo de Downloads, exceções por caminho + hash, aviso honesto de que suspeito ≠ malware |

## Como validar no Windows

1. `dotnet build Nexus.slnx -c Release` e `dotnet test tests/Nexus.Tests -c Release --filter FullyQualifiedName~Scanner`.
2. Crie `eicar.com` em Downloads com o texto do EICAR (eicar.org). O Defender provavelmente o remove na hora (ótimo sinal);
   para testar o NEXUS, crie numa pasta de exclusão temporária ou use a amostra `NEXUS-SCANNER-TEST-SAMPLE` (texto + quebra
   de linha, num `.txt`) com "Verificar arquivos…": deve aparecer em "Arquivos de teste".
3. Copie `C:\Windows\System32\notepad.exe` para Downloads como `svch0st.exe`: aparece com "nome parecido com processo do
   Windows" (a assinatura do notepad é por catálogo, então "sem assinatura embutida" também aparece; é o esperado).
4. "Verificar com o Defender" num achado: a mensagem do MpCmdRun aparece no cartão.
5. "Isolar" (vaga de usuário): o arquivo some de Downloads, aparece em `%LocalAppData%\NEXUS\quarantine\scanner\…`;
   "Desfazer" devolve. Mude o arquivo antes de isolar: o aplicador recusa (hash diferente).
6. Marque "Ao vivo" e baixe um arquivo: alguns segundos depois do fim do download, o resultado aparece embaixo da caixa.
