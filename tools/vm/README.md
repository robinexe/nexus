# Testes de integração em VM (aplicação, rollback e recuperação REAIS)

Estes testes escrevem de verdade no registro, no plano de energia e em serviços. Por isso:

- ficam em `tests/Nexus.IntegrationTests`, **fora da solução** (`dotnet test Nexus.slnx` não os roda);
- cada teste avalia o `VmOnlyExecutionGate` **antes de qualquer escrita** e falha sem tocar no sistema se não estiver numa
  VM liberada. O broker confere o portão de novo;
- o portão exige (1) SMBIOS de máquina virtual, (2) `%ProgramData%\NEXUS\vm-test-host.allow` com o nome **desta**
  máquina e (3) esse arquivo pertencer a Administradores sem permitir escrita a usuários comuns. No PC físico, a
  condição 1 já falha.

## Opção recomendada: Windows Sandbox (descartável)

1. No PC (PowerShell normal, não elevado), na pasta do projeto:
   `powershell -ExecutionPolicy Bypass -File .\tools\vm\Prepare-Sandbox.ps1`
   O script compila e gera `vm-config\nexus-sandbox.wsb` (fora da pasta que o Sandbox pode gravar). Não executa nada no hospedeiro.
2. Se o Windows Sandbox não estiver ativado, o script avisa. Ativar é decisão sua: exige administrador e reinício.
3. Duplo clique em `vm-config\nexus-sandbox.wsb`. Dentro do Sandbox, `Inside-Sandbox.ps1` roda sozinho:
   - recusa se não estiver na conta do Sandbox;
   - registra a descoberta (deve dizer "Máquina virtual: Sim");
   - cria o arquivo de liberação e roda os testes.
4. Os resultados ficam em `vm-saida\vm-integracao-<data>.log` e `.trx`. Feche o Sandbox: tudo o que mudou lá dentro é
   descartado.

Isolamento: o repositório e o .NET são mapeados somente leitura, a rede fica desligada e a única pasta gravável é
`vm-saida`.

## Opção alternativa: VM Hyper-V ou VirtualBox

Instale o .NET 10 SDK na VM, copie o repositório, crie, como administrador, `C:\ProgramData\NEXUS\vm-test-host.allow` contendo o nome da VM
(`$env:COMPUTERNAME`), com dono Administradores e sem escrita para usuários comuns
(`icacls <arquivo> /setowner *S-1-5-32-544` e `icacls <arquivo> /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F *S-1-5-32-545:R`), e rode `dotnet test tests\Nexus.IntegrationTests -c Release`. Para cobrir os testes de
administrador (serviço SysMain, energia AC), rode o terminal como administrador **dentro da VM**.

## O que é testado (`VmIntegrationTests`)

| Teste | Alvo real | Verifica |
|---|---|---|
| Modo de Jogo | `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` | aplica pelo broker, relê, reverte ao valor exato (inclusive "ausente") |
| Crash + `nexus-recover` | `HKCU\Software\NexusVmIntegration` | escrita real seguida de crash simulado; o `nexus-recover.exe` real restaura e remove a chave criada |
| Sessão de energia | esquema ativo | Alto desempenho na sessão; `EndSession` volta ao esquema anterior (ou "não aplicável" sem mudar nada) |
| SysMain | tipo de inicialização do serviço | bloqueado sem elevação; com elevação, ida e volta |
| Core parking AC | valor no esquema ativo | bloqueado sem elevação; com elevação, ida e volta |
| Alvo protegido | `HKLM\...\Lsa` | recusado por `Assess` e pela validação de restauração |
