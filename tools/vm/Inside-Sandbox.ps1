<#
  NEXUS — roda DENTRO do Windows Sandbox (chamado pelo nexus-sandbox.wsb). Não use no PC hospedeiro.

  1. Recusa se não estiver na conta descartável do Sandbox (WDAGUtilityAccount).
  2. Registra a descoberta (deve indicar máquina virtual).
  3. Cria o arquivo de liberação do portão com o nome DESTA máquina (só vale aqui; o Sandbox é descartado ao fechar).
  4. Roda os testes de integração com handlers reais e grava log e .trx em C:\saida (pasta do hospedeiro).
#>
$ErrorActionPreference = 'Continue'
$utf8 = New-Object System.Text.UTF8Encoding($true)
# Identidade real do processo (não a variável de ambiente, que qualquer um pode alterar).
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($identity -notmatch '\\WDAGUtilityAccount$') {
    Write-Host "Recusado: este script só roda dentro do Windows Sandbox (identidade atual: $identity)." -ForegroundColor Red
    exit 10
}
$env:DOTNET_ROOT = 'C:\dotnet'
$env:PATH = "C:\dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = "C:\saida\vm-integracao-$stamp.log"
function Log([string[]]$lines) { [System.IO.File]::AppendAllText($log, (($lines -join "`r`n") + "`r`n"), $utf8) }
Log @("NEXUS integração em VM — $(Get-Date -Format o) — $env:COMPUTERNAME — usuário $env:USERNAME",
      "Controle de codificação: ação, código, não, recuperação")

New-Item -ItemType Directory -Force -Path C:\work | Out-Null
Copy-Item -Recurse -Force C:\nexus\tests\Nexus.IntegrationTests\bin\Release\net10.0-windows C:\work\it
Copy-Item -Recurse -Force C:\nexus\tools\Nexus.Discover\bin\Release\net10.0 C:\work\discover

Log @('', '### descoberta')
$disc = & C:\dotnet\dotnet.exe C:\work\discover\Nexus.Discover.dll --rows 5 2>&1 | ForEach-Object { "$_" }
Log ($disc + "exit=$LASTEXITCODE")
$disc | Select-String 'Máquina virtual|Fabricante do sistema|Modelo do sistema|Processo elevado' | ForEach-Object { Write-Host $_ }

# Segunda checagem: o SMBIOS precisa indicar máquina virtual antes de criar a liberação.
if (-not ($disc -match 'Máquina virtual: Sim')) {
    Log @('', 'Recusado: o SMBIOS não indica máquina virtual; arquivo de liberação NÃO criado.')
    Write-Host 'Recusado: o SMBIOS não indica máquina virtual.' -ForegroundColor Red
    exit 11
}
New-Item -ItemType Directory -Force -Path C:\ProgramData\NEXUS | Out-Null
$marker = 'C:\ProgramData\NEXUS\vm-test-host.allow'
[System.IO.File]::WriteAllText($marker, $env:COMPUTERNAME, (New-Object System.Text.UTF8Encoding($false)))
# O portão exige dono Administradores e nenhuma escrita para usuários comuns (exige este PowerShell elevado no Sandbox).
& icacls $marker /setowner '*S-1-5-32-544' | Out-Null
& icacls $marker /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' '*S-1-5-32-545:R' | Out-Null
Log @('', "Arquivo de liberação criado para $env:COMPUTERNAME (existe só dentro deste Sandbox); icacls exit=$LASTEXITCODE.")

Log @('', '### dotnet test (integração, handlers reais)')
$test = & C:\dotnet\dotnet.exe test C:\work\it\Nexus.IntegrationTests.dll --logger 'console;verbosity=normal' --logger "trx;LogFileName=C:\saida\vm-integracao-$stamp.trx" 2>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE
Log ($test + "exit=$code")
$test | Select-Object -Last 25 | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Resultado: exit=$code. Log: $log (no hospedeiro: pasta vm-saida do repositório)." -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Red' })
Write-Host 'Feche o Sandbox quando terminar; tudo o que foi alterado aqui dentro é descartado.'
