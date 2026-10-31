<#
  NEXUS — confere se a pasta do programa está protegida contra troca de arquivos por contas sem privilégio.

  Por que importa: ações de administrador iniciam nexus-broker.exe ELEVADO (aviso do Windows) a partir da pasta do NEXUS.
  Se uma conta comum (ou um programa rodando com ela) puder trocar nexus-broker.exe, uma dll ou o catálogo nessa pasta, o
  que roda como administrador passa a ser escolhido por ela. Em %ProgramFiles%\NEXUS (instalador) só administradores
  escrevem; numa pasta extraída do ZIP em Downloads/Área de Trabalho, o seu usuário escreve.

  Regra (a mesma de Nexus.WindowsActions.AdminOnlyDirectory.Evaluate):
    - na pasta e em cada arquivo dentro dela: dono = Administradores/SYSTEM/TrustedInstaller e nenhuma permissão de escrita,
      exclusão ou troca de permissões para outras contas ("CREATOR OWNER" é ignorado);
    - em cada pasta acima: nenhuma conta sem privilégio com "excluir subpastas e arquivos" (renomear a pasta do NEXUS e pôr
      outra no lugar).
  Somente leitura: não altera nada.

  Uso: powershell -ExecutionPolicy Bypass -File Test-NexusFolderSafety.ps1 [-Path <pasta>] [-Quiet]
  Saída: 0 = protegida; 1 = gravável por contas comuns (lista o motivo); 2 = não foi possível verificar.
#>
param(
    [string]$Path = (Split-Path -Parent $PSScriptRoot),
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

$Privileged = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
$CreatorOwner = 'S-1-3-0'
# FILE_WRITE_DATA/ADD_FILE, FILE_APPEND_DATA/ADD_SUBDIRECTORY, FILE_WRITE_EA, FILE_DELETE_CHILD, FILE_WRITE_ATTRIBUTES,
# DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE (igual a AdminOnlyDirectory.WriteLike)
$WriteLike = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
$DeleteChild = 0x40 -bor 0x10000000

function Get-Problem([string]$Item, [int]$Mask, [bool]$CheckOwner) {
    $acl = Get-Acl -LiteralPath $Item
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($CheckOwner -and $Privileged -notcontains $owner) { return "dono $owner não é Administradores/SYSTEM: $Item" }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        $sid = $rule.IdentityReference.Value
        if ($Privileged -contains $sid -or $sid -eq $CreatorOwner) { continue }
        if (([int]$rule.FileSystemRights -band $Mask) -ne 0) {
            $name = try { $rule.IdentityReference.Translate([Security.Principal.NTAccount]).Value } catch { $sid }
            return "$name pode alterar: $Item"
        }
    }
    return $null
}

try {
    $root = (Resolve-Path -LiteralPath $Path).ProviderPath
    $problems = New-Object System.Collections.Generic.List[string]
    $dirInfo = Get-Item -LiteralPath $root -Force
    if ($dirInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) { $problems.Add("a pasta é um link ou junção: $root") }

    $p = Get-Problem $root $WriteLike $true
    if ($p) { $problems.Add($p) }
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction Stop) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $problems.Add("link ou junção dentro da pasta: $($item.FullName)"); continue }
        $p = Get-Problem $item.FullName $WriteLike $true
        if ($p) { $problems.Add($p) }
        if ($problems.Count -ge 20) { break }
    }
    $parent = Split-Path -Parent $root
    while ($parent) {
        $p = Get-Problem $parent $DeleteChild $false
        if ($p) { $problems.Add("pasta acima permite trocar a do NEXUS ($p)") }
        $next = Split-Path -Parent $parent
        if ($next -eq $parent) { break }
        $parent = $next
    }
} catch {
    if (-not $Quiet) { Write-Host "Não foi possível verificar ${Path}: $($_.Exception.Message)" -ForegroundColor Yellow }
    exit 2
}

if ($problems.Count -eq 0) {
    if (-not $Quiet) { Write-Host "Protegida: só administradores alteram $root." -ForegroundColor Green }
    exit 0
}
if (-not $Quiet) {
    Write-Host "ATENÇÃO: contas sem privilégio podem trocar arquivos em $root." -ForegroundColor Yellow
    $problems | Select-Object -First 20 | ForEach-Object { Write-Host "  - $_" }
    Write-Host "As ações de administrador do NEXUS rodariam arquivos que essas contas escolhem. Use o instalador"
    Write-Host "(%ProgramFiles%\NEXUS) para ações de administrador, ou mova a pasta para um lugar que só administradores alteram."
}
exit 1
