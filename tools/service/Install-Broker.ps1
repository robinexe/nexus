<#
  NEXUS — instala o broker como serviço do Windows (NexusBroker, conta LocalSystem, início manual).

  EXIGE PowerShell elevado. NÃO rode no PC de jogos enquanto a execução real estiver bloqueada no hospedeiro: o portão do
  broker continua exigindo VM de teste liberada para qualquer escrita, mas instalar um serviço já é uma alteração do sistema.
  Hoje o uso previsto é a VM descartável do CI (.github/workflows/vm-integracao.yml).

  O que faz:
    1. Copia o broker já compilado (compile ANTES, sem elevação: dotnet build src\Nexus.Broker.Host -c Release) para
       %ProgramFiles%\NEXUS\broker (só Administradores/SYSTEM escrevem ali).
    2. Cria %ProgramData%\NEXUS sem herança: SYSTEM e Administradores com controle total, Usuários só leitura (R1/B1).
       Preserva o que já existir lá (diário, arquivo de liberação da VM).
    3. Cria %ProgramData%\NEXUS\nexus.lock SEM acesso para usuários comuns (B7): só SYSTEM/Administradores conseguem
       segurar a trava. Consequência: nexus-recover sem elevação mostra a coordenação como "indeterminada" e recusa
       recover/revert (exit 6); consulte o broker (Status) ou rode elevado.
    4. Registra/atualiza o serviço com --allow-sid <SID>: só esse usuário (e Administradores) abre o pipe.
  Não inicia o serviço, a menos que -Start seja passado.

  Uso: powershell -ExecutionPolicy Bypass -File .\tools\service\Install-Broker.ps1 [-AllowSid S-1-5-21-...] [-Start]
#>
param(
    [string]$Source = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\Nexus.Broker.Host\bin\Release\net10.0-windows'),
    [string]$AllowSid = ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value),
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'NexusBroker'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Recusado: este script precisa de PowerShell elevado (administrador).' -ForegroundColor Red
    exit 10
}
if ($AllowSid -notmatch '^S-1-5-21(-[0-9]{1,10}){4}$') {
    Write-Host "Recusado: -AllowSid deve ser o SID de uma conta de usuário (S-1-5-21-...). Recebido: $AllowSid" -ForegroundColor Red
    exit 11
}
$exe = Join-Path $Source 'nexus-broker.exe'
if (-not (Test-Path $exe)) {
    Write-Host "Broker não compilado em $Source. Rode antes, SEM elevação: dotnet build src\Nexus.Broker.Host -c Release" -ForegroundColor Red
    exit 12
}

# 1. Binários
$dest = Join-Path $env:ProgramFiles 'NEXUS\broker'
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $existing.WaitForStatus('Stopped', '00:00:30') }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $dest -Recurse -Force
Write-Host "Binários: $dest"

# 2. Dados: SYSTEM/Administradores total, Usuários leitura; sem herança da ACL padrão de %ProgramData% (que deixa usuários criarem arquivos)
$data = Join-Path $env:ProgramData 'NEXUS'
New-Item -ItemType Directory -Force -Path $data | Out-Null
& icacls $data /setowner '*S-1-5-32-544' | Out-Null
& icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls falhou em $data ($LASTEXITCODE)" }
Write-Host "Dados: $data (SYSTEM/Administradores: total; Usuários: leitura)"

# 3. Trava sem acesso para usuários comuns (B7)
$lock = Join-Path $data 'nexus.lock'
if (-not (Test-Path $lock)) { [System.IO.File]::WriteAllBytes($lock, [byte[]]@()) }
& icacls $lock /setowner '*S-1-5-32-544' | Out-Null
& icacls $lock /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls falhou em $lock ($LASTEXITCODE)" }
Write-Host "Trava: $lock (só SYSTEM/Administradores)"

# 4. Serviço
$binPath = "`"$(Join-Path $dest 'nexus-broker.exe')`" --service --allow-sid $AllowSid"
if ($existing) {
    & sc.exe config $ServiceName binPath= $binPath start= demand obj= LocalSystem | Out-Null
} else {
    & sc.exe create $ServiceName binPath= $binPath start= demand obj= LocalSystem DisplayName= 'NEXUS Broker' | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw "sc.exe falhou ($LASTEXITCODE)" }
& sc.exe description $ServiceName 'NEXUS Performance Lab: executa ações do catálogo com diário e reversão. Escrita real só em VM de teste liberada.' | Out-Null
Write-Host "Serviço $ServiceName registrado (LocalSystem, início manual). Cliente autorizado: $AllowSid"

if ($Start) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
    Write-Host "Serviço iniciado. Log: $(Join-Path $data 'broker.log')"
}
