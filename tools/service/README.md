# Broker como serviço do Windows

`nexus-broker --service --allow-sid <SID>` roda sob o Gerenciador de Serviços como **LocalSystem** (serviço `NexusBroker`,
início manual). Mesmo protocolo e mesmo portão do modo console: **qualquer escrita real exige VM de teste liberada**.

## Instalação (PowerShell elevado; hoje só na VM de teste)

```powershell
dotnet build src\Nexus.Broker.Host -c Release            # SEM elevação
powershell -ExecutionPolicy Bypass -File .\tools\service\Install-Broker.ps1 -Start   # elevado
powershell -ExecutionPolicy Bypass -File .\tools\service\Uninstall-Broker.ps1        # elevado; preserva o diário
```

| Item | Onde | Permissões |
|---|---|---|
| Binários | `%ProgramFiles%\NEXUS\broker` | padrão de Program Files (só administradores escrevem) |
| Dados (diário, log) | `%ProgramData%\NEXUS` | sem herança: SYSTEM e Administradores total, Usuários leitura (R1/B1) |
| Trava | `%ProgramData%\NEXUS\nexus.lock` | só SYSTEM e Administradores (B7): um usuário comum não consegue segurar a trava |
| Pipe | `\\.\pipe\NexusBroker.v1` | SYSTEM e Administradores total; o SID de `-AllowSid` (padrão: quem instalou) leitura/escrita; rede negada |

## Como o serviço sabe de quem é o HKCU (B10)

Como LocalSystem, o HKCU do processo é o perfil do SYSTEM. Por isso:

1. O pipe identifica o cliente pelo **token** (impersonação no nível *Identification*, depois do primeiro pedido). O SID
   nunca vem do conteúdo do pedido.
2. Toda ação de HKCU recebe no alvo `userSid=<SID do cliente>` e é gravada assim no diário. Os backends escrevem em
   `HKU\<SID>` e nunca criam essa raiz: com o perfil descarregado (usuário sem sessão), a operação falha sem escrever.
3. A restauração usa o SID do diário: volta ao perfil de quem aplicou, mesmo que outro usuário peça.
4. Sem SID de conta de usuário (`S-1-5-21-…`), ações de HKCU são recusadas (`caller-unknown`). Entradas antigas de HKCU sem
   SID não são restauradas pelo serviço (iriam para o SYSTEM): use o `nexus-recover` na sessão do usuário.
5. Segunda barreira: o backend de registro do serviço recusa HKCU sem SID.

## Como o cliente sabe que é o broker verdadeiro (B3)

`BrokerClient.Connect(..., BrokerServerKind.Service)` confere o dono do pipe antes de enviar qualquer coisa: só aceita
SYSTEM ou Administradores. Um pipe com o mesmo nome criado por um usuário comum é recusado ("possível impostor").

## Consequências conhecidas

- Com a trava restrita, `nexus-recover` sem elevação mostra a coordenação como indeterminada e recusa `recover/revert`
  (exit 6). Consulte o broker (`Status`) ou rode elevado.
- Assinatura Authenticode do cliente (verificação de imagem do processo cliente) continua pendente: exige certificado de
  assinatura de código, que o projeto não tem.

## Validação

VM do GitHub Actions (`.github/workflows/vm-integracao.yml`): instala, inicia, roda `ServiceIntegrationTests` como o
usuário da VM e desinstala. Ver `docs/06-build-log.md`.
