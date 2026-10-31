<#
  NEXUS — prepara o teste de integração no Windows Sandbox (roda no PC hospedeiro, NÃO elevado).

  O que FAZ no hospedeiro:
    - compila tests\Nexus.IntegrationTests e tools\Nexus.Discover (nada é executado aqui)
    - cria a pasta de saída <repo>\vm-saida
    - gera <repo>\vm-config\nexus-sandbox.wsb (fora da pasta gravável pelo Sandbox) com:
        * o repositório mapeado SOMENTE LEITURA em C:\nexus
        * o .NET do hospedeiro mapeado SOMENTE LEITURA em C:\dotnet
        * a pasta de saída (única gravável) em C:\saida
        * rede desligada

  O que NÃO faz:
    - não ativa o recurso Windows Sandbox (isso exige administrador e é decisão sua)
    - não roda testes, não escreve registro, serviços, energia nem nada fora do repositório
#>
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Recusado: rode em um PowerShell normal (não elevado).' -ForegroundColor Red; exit 10
}

Write-Host 'Compilando os testes de integração e a ferramenta de descoberta...' -ForegroundColor Cyan
dotnet build tests\Nexus.IntegrationTests -c Release
if ($LASTEXITCODE -ne 0) { Write-Host 'Falha na compilação.' -ForegroundColor Red; exit 1 }
dotnet build tools\Nexus.Discover -c Release
if ($LASTEXITCODE -ne 0) { Write-Host 'Falha na compilação.' -ForegroundColor Red; exit 1 }

$dotnetDir = Split-Path -Parent (Get-Command dotnet).Source
$out = Join-Path $root 'vm-saida'     # única pasta que o Sandbox pode gravar
$cfg = Join-Path $root 'vm-config'    # o .wsb fica FORA da pasta gravável: o Sandbox não consegue alterá-lo
New-Item -ItemType Directory -Force -Path $out, $cfg | Out-Null
function X([string]$s) { [System.Security.SecurityElement]::Escape($s) }

$wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$(X $root)</HostFolder><SandboxFolder>C:\nexus</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(X $dotnetDir)</HostFolder><SandboxFolder>C:\dotnet</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(X $out)</HostFolder><SandboxFolder>C:\saida</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -ExecutionPolicy Bypass -NoExit -File C:\nexus\tools\vm\Inside-Sandbox.ps1</Command>
  </LogonCommand>
</Configuration>
"@
$wsbPath = Join-Path $cfg 'nexus-sandbox.wsb'
[System.IO.File]::WriteAllText($wsbPath, $wsb, (New-Object System.Text.UTF8Encoding($false)))

$sandboxExe = Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe'
Write-Host ''
Write-Host "Configuração gerada: $wsbPath" -ForegroundColor Green
if (Test-Path $sandboxExe) {
    Write-Host 'Windows Sandbox encontrado. Dê duplo clique no arquivo .wsb acima para rodar os testes isolados.' -ForegroundColor Green
} else {
    Write-Host 'Windows Sandbox NÃO está ativado neste PC.' -ForegroundColor Yellow
    Write-Host 'Para ativar (decisão sua; exige administrador e reinício): Configurações > Sistema > Recursos opcionais >' -ForegroundColor Yellow
    Write-Host '"Mais recursos do Windows" > marcar "Área Restrita do Windows" (Windows Sandbox).' -ForegroundColor Yellow
}
Write-Host "Resultados aparecerão em: $out"
