<#
  NEXUS — validação local no Windows (build, testes Unit, coordenação entre processos, descoberta e smoke test somente leitura).

  O que este script FAZ:
    - dotnet --info / --list-sdks / restore / build / test --filter Category=Unit
      (os testes de coordenação iniciam processos auxiliares que só escrevem em %TEMP%)
    - compila tools/Nexus.SmokeJournal e cria um diário de TESTE em %TEMP%\nexus-smoke-<data>\smoke.db
      (o gerador usa um registro EM MEMÓRIA; não lê nem escreve o registro real) e mostra a versão nativa do SQLite
    - roda nexus-recover runs / scan / show apontando para esse diário de teste (somente leitura)
    - roda tools/Nexus.Discover (descoberta de hardware somente leitura) e grava o relatório no log
    - grava tudo, com o $LASTEXITCODE de cada comando, em %TEMP%\nexus-validacao-<data>.log (UTF-8)

  O que este script NÃO faz:
    - não roda como administrador (recusa se estiver elevado)
    - não chama recover/revert, não toca registro, serviços, tarefas, energia, drivers ou arquivos de jogos
    - não usa o diário padrão (%ProgramData%\NEXUS\journal.db)

  Uso (PowerShell normal, NÃO elevado), na pasta do projeto:
    powershell -ExecutionPolicy Bypass -File .\tools\etapa1-windows.ps1
#>
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Codificação: o PowerShell 5.1 decodifica a saída de programas nativos com [Console]::OutputEncoding (OEM, ex. 850),
# enquanto o .NET escreve conforme a página de código do console. Força UTF-8 nos dois lados durante o script e
# restaura no final para não alterar a janela do usuário.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$previousConsoleEncoding = [Console]::OutputEncoding
$previousPipeEncoding = $OutputEncoding
[Console]::OutputEncoding = $utf8NoBom
$global:OutputEncoding = $utf8NoBom

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Recusado: este PowerShell está elevado (administrador). Abra um PowerShell normal.' -ForegroundColor Red
    [Console]::OutputEncoding = $previousConsoleEncoding
    exit 10
}

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $env:TEMP "nexus-validacao-$stamp.log"

function Write-Log([string[]]$Lines) {
    [System.IO.File]::AppendAllText($log, (($Lines -join "`r`n") + "`r`n"), $utf8Bom)
}

Write-Log @("NEXUS validação — $(Get-Date -Format o) — $env:COMPUTERNAME — $root",
            "Controle de codificação: ação, código, não, recuperação (deve aparecer sem caracteres estranhos)")

$summary = New-Object System.Collections.Generic.List[string]

function Invoke-Step {
    param([string]$Exe, [string[]]$Arguments, [int[]]$OkCodes = @(0))
    $display = "$Exe $($Arguments -join ' ')"
    Write-Host "`n### $display" -ForegroundColor Cyan
    Write-Log @('', "### $display")
    $out = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    Write-Log (@($out) + "exit=$code")
    $out | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
    $ok = $OkCodes -contains $code
    Write-Host ("  -> exit={0} {1}" -f $code, $(if ($ok) { 'OK' } else { 'FALHOU' })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    $summary.Add(("{0,-4} exit={1,-3} {2}" -f $(if ($ok) { 'OK' } else { 'FAIL' }), $code, $display))
    return [pscustomobject]@{ Code = $code; Ok = $ok; Output = $out }
}

function Stop-WithSummary([int]$code) {
    Write-Log (@('', '=== RESUMO ===') + $summary)
    Write-Host "`n=== RESUMO ===" -ForegroundColor Yellow
    $summary | ForEach-Object { Write-Host $_ }
    Write-Host "`nLog completo (UTF-8): $log" -ForegroundColor Yellow
    [Console]::OutputEncoding = $previousConsoleEncoding
    $global:OutputEncoding = $previousPipeEncoding
    exit $code
}

# 1. SDK
Invoke-Step 'dotnet' @('--info') | Out-Null
$sdks = Invoke-Step 'dotnet' @('--list-sdks')
if (-not ($sdks.Output -match '^10\.0\.')) {
    Write-Host 'Nenhum SDK 10.0 encontrado. Instale o .NET SDK 10.0 (x64) e rode de novo.' -ForegroundColor Red
    Stop-WithSummary 20
}

# 2-4. restore, build, testes (inclui coordenação entre processos)
if (-not (Invoke-Step 'dotnet' @('restore', 'Nexus.slnx')).Ok) { Stop-WithSummary 21 }
if (-not (Invoke-Step 'dotnet' @('build', 'Nexus.slnx', '-c', 'Release', '--no-restore')).Ok) { Stop-WithSummary 22 }
$test = Invoke-Step 'dotnet' @('test', 'Nexus.slnx', '-c', 'Release', '--no-build', '--filter', 'Category=Unit', '--logger', 'console;verbosity=normal')
$coord = Invoke-Step 'dotnet' @('test', 'tests\Nexus.Tests', '-c', 'Release', '--no-build', '--filter', 'FullyQualifiedName~CoordinationTests', '--logger', 'console;verbosity=normal')

# 5. descoberta de hardware somente leitura (texto completo vai para o log, para conferência dos valores reais)
if ((Invoke-Step 'dotnet' @('build', 'tools\Nexus.Discover', '-c', 'Release')).Ok) {
    Invoke-Step 'dotnet' @((Join-Path $root 'tools\Nexus.Discover\bin\Release\net10.0\Nexus.Discover.dll'), '--rows', '400') | Out-Null
}

# 6. smoke test somente leitura do nexus-recover
$smokeDir = Join-Path $env:TEMP "nexus-smoke-$stamp"
New-Item -ItemType Directory -Path $smokeDir | Out-Null
$journal = Join-Path $smokeDir 'smoke.db'
if (-not (Invoke-Step 'dotnet' @('build', 'tools\Nexus.SmokeJournal', '-c', 'Release')).Ok) { Stop-WithSummary 23 }
if (-not (Invoke-Step 'dotnet' @((Join-Path $root 'tools\Nexus.SmokeJournal\bin\Release\net10.0\Nexus.SmokeJournal.dll'), $journal)).Ok) { Stop-WithSummary 24 }

$cli = Join-Path $root 'src\Nexus.Recovery.Cli\bin\Release\net10.0\nexus-recover.exe'
$hashBefore = (Get-FileHash $journal -Algorithm SHA256).Hash
$runs = Invoke-Step $cli @('runs', '--journal', $journal)
Invoke-Step $cli @('scan', '--journal', $journal) -OkCodes @(1) | Out-Null   # 1 = há operações interrompidas (esperado: 2)
$runId = ($runs.Output | Where-Object { $_ -match 'smoke-interrompida' } | Select-Object -First 1) -replace '\s.*$', ''
if ($runId) { Invoke-Step $cli @('show', $runId, '--journal', $journal) | Out-Null }
Invoke-Step $cli @('scan', '--journal', (Join-Path $smokeDir 'nao-existe.db')) -OkCodes @(3) | Out-Null
$hashAfter = (Get-FileHash $journal -Algorithm SHA256).Hash
$same = $hashBefore -eq $hashAfter
$summary.Add(("{0,-4} diário de teste inalterado pelo CLI (SHA-256 antes={1} depois={2})" -f $(if ($same) { 'OK' } else { 'FAIL' }), $hashBefore.Substring(0, 16), $hashAfter.Substring(0, 16)))

$desktop = Join-Path $root 'src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe'
Write-Host "`nInterface (somente leitura): $desktop" -ForegroundColor Yellow
Write-Log @('', "Interface: $desktop")
Stop-WithSummary $(if ($test.Ok -and $coord.Ok -and $same) { 0 } else { 1 })
