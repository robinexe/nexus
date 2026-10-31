<#
  NEXUS — remove o serviço NexusBroker e os binários de %ProgramFiles%\NEXUS\broker. EXIGE PowerShell elevado.
  %ProgramData%\NEXUS (diário, trava, log) é PRESERVADO: o diário é o que permite recuperar alterações. Use -RemoveData
  só se não houver nada a recuperar (o script recusa se nexus-recover scan apontar operações pendentes).
#>
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$ServiceName = 'NexusBroker'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Recusado: este script precisa de PowerShell elevado (administrador).' -ForegroundColor Red
    exit 10
}
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Serviço $ServiceName removido."
}
$dest = Join-Path $env:ProgramFiles 'NEXUS\broker'
$cli = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\Nexus.Recovery.Cli\bin\Release\net10.0\nexus-recover.exe'
$data = Join-Path $env:ProgramData 'NEXUS'
if ($RemoveData -and (Test-Path (Join-Path $data 'journal.db'))) {
    if (Test-Path $cli) {
        & $cli scan --journal (Join-Path $data 'journal.db') | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Host 'Recusado: há operações a recuperar (ou não foi possível verificar). Dados preservados.' -ForegroundColor Red; exit 11 }
    } else {
        Write-Host "Recusado: compile o nexus-recover ($cli) para conferir o diário. Dados preservados." -ForegroundColor Red; exit 11
    }
}
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest; Write-Host "Binários removidos: $dest" }
if ($RemoveData -and (Test-Path $data)) { Remove-Item -Recurse -Force $data; Write-Host "Dados removidos: $data" }
elseif (Test-Path $data) { Write-Host "Dados preservados: $data" }
