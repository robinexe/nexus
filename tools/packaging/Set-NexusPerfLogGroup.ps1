<#
.SYNOPSIS
  Grupo "Usuários do log de desempenho" (Performance Log Users, S-1-5-32-559) para o Medir do NEXUS: o PresentMon mede sem
  pedir permissão ao Windows a cada partida quando a conta é membro. Usado pelo instalador (opção marcada por padrão) e pelo
  desinstalador. Precisa de administrador para -Join e -Leave.

.DESCRIPTION
  -Join -Sid S-1-5-21-...  adiciona a conta ao grupo SE ela ainda não for membro e grava a marca do NEXUS em
                           HKLM\SOFTWARE\NEXUS\PerfLogGroup (valor = SID, dado = -By). Já membro: nada muda e nenhuma marca é
                           gravada (não foi o NEXUS que adicionou). Códigos: 0 adicionada, 2 já era membro, 1 erro.
  -Leave                   para cada conta com a marca do NEXUS (adicionada pelo instalador ou pela ação
                           measure.perflog-group.join do aplicativo), remove do grupo e apaga a marca. Quem já estava no
                           grupo antes do NEXUS não tem marca e nunca é removido. Códigos: 0 ok, 1 erro.
  -Status -Sid S-1-5-21-... só lê: 0 membro (imprime se há marca), 3 não membro, 1 erro.

  O grupo é encontrado pelo SID (funciona em qualquer idioma do Windows). A mudança vale depois que a pessoa sai e entra de
  novo na conta. Mesma marca e mesma regra do handler PerfLogGroupHandler (src/Nexus.Measurement/PerfLogGroup.cs).
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Join', Mandatory = $true)][switch]$Join,
    [Parameter(ParameterSetName = 'Leave', Mandatory = $true)][switch]$Leave,
    [Parameter(ParameterSetName = 'Status')][switch]$Status,
    [Parameter(ParameterSetName = 'Join', Mandatory = $true)]
    [Parameter(ParameterSetName = 'Status', Mandatory = $true)]
    [ValidatePattern('^S-1-5-21(-[0-9]{1,10}){4}$')][string]$Sid,
    [Parameter(ParameterSetName = 'Join')][ValidateSet('installer', 'app')][string]$By = 'installer'
)

$ErrorActionPreference = 'Stop'
$GroupSid = 'S-1-5-32-559'
$MarkerKey = 'HKLM:\SOFTWARE\NEXUS\PerfLogGroup'

function Get-GroupName {
    $account = (New-Object System.Security.Principal.SecurityIdentifier $GroupSid).Translate([System.Security.Principal.NTAccount]).Value
    return $account.Split('\')[-1]
}

function Get-Group {
    return [ADSI]("WinNT://$env:COMPUTERNAME/" + (Get-GroupName) + ',group')
}

# Membro DIRETO, lido pelo ADSI (Get-LocalGroupMember falha quando o grupo tem SIDs órfãos).
function Test-Member([string]$UserSid) {
    foreach ($m in @((Get-Group).Invoke('Members'))) {
        $bytes = $m.GetType().InvokeMember('objectSid', 'GetProperty', $null, $m, $null)
        if ((New-Object System.Security.Principal.SecurityIdentifier($bytes, 0)).Value -eq $UserSid) { return $true }
    }
    return $false
}

# Tipos das exceções comparados pelo nome: se o módulo LocalAccounts faltar, um "catch [Tipo]" desconhecido daria outro erro.
function Add-Member([string]$UserSid) {
    try { Add-LocalGroupMember -SID $GroupSid -Member $UserSid }
    catch {
        if ($_.Exception.GetType().Name -eq 'MemberExistsException') { return }
        (Get-Group).Add("WinNT://$UserSid") # módulo LocalAccounts indisponível: o ADSI aceita o SID no caminho
    }
}

function Remove-Member([string]$UserSid) {
    try { Remove-LocalGroupMember -SID $GroupSid -Member $UserSid }
    catch {
        if ($_.Exception.GetType().Name -in 'MemberNotFoundException', 'PrincipalNotFoundException') { return }
        (Get-Group).Remove("WinNT://$UserSid")
    }
}

function Get-Markers {
    if (-not (Test-Path $MarkerKey)) { return @() }
    $item = Get-Item $MarkerKey
    return @($item.GetValueNames() | Where-Object { $_ -match '^S-1-5-21(-[0-9]{1,10}){4}$' })
}

try {
    switch ($PSCmdlet.ParameterSetName) {
        'Join' {
            if (Test-Member $Sid) {
                Write-Output "Já é membro de $(Get-GroupName): $Sid (nada alterado, sem marca do NEXUS)."
                exit 2
            }
            Add-Member $Sid
            if (-not (Test-Member $Sid)) { throw "A conta $Sid não aparece no grupo depois de adicionar." }
            if (-not (Test-Path $MarkerKey)) { New-Item -Path $MarkerKey -Force | Out-Null }
            New-ItemProperty -Path $MarkerKey -Name $Sid -Value $By -PropertyType String -Force | Out-Null
            Write-Output "Adicionada a $(Get-GroupName): $Sid (marca do NEXUS: $By). Vale depois de sair e entrar de novo na conta."
            exit 0
        }
        'Leave' {
            foreach ($s in Get-Markers) {
                if (Test-Member $s) { Remove-Member $s; Write-Output "Removida de $(Get-GroupName): $s" }
                else { Write-Output "Já não era membro: $s" }
                Remove-ItemProperty -Path $MarkerKey -Name $s -Force
            }
            if ((Test-Path $MarkerKey) -and @((Get-Item $MarkerKey).GetValueNames()).Count -eq 0 -and @(Get-ChildItem $MarkerKey).Count -eq 0) {
                Remove-Item $MarkerKey -Force
            }
            exit 0
        }
        default {
            $marked = (Get-Markers) -contains $Sid
            if (Test-Member $Sid) { Write-Output "Membro: $Sid (marca do NEXUS: $marked)"; exit 0 }
            Write-Output "Não é membro: $Sid (marca do NEXUS: $marked)"
            exit 3
        }
    }
}
catch {
    Write-Output "ERRO: $($_.Exception.Message)"
    exit 1
}
