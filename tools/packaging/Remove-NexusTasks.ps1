<#
  NEXUS — remove as tarefas agendadas criadas pelo NEXUS (usado pelo desinstalador; pode ser rodado à mão).

  Só remove tarefas cuja descrição tem a marca do NEXUS ("[nexus.automation.scheduled-scan/1]" e futuras "[nexus.*]"),
  gravada por Nexus.Automation.ScheduledScanTask. Uma tarefa com o mesmo nome criada por outra pessoa/programa NÃO é tocada.
  A tarefa semanal é da conta do usuário; para remover tarefas de OUTRAS contas, rode elevado (o desinstalador roda).

  Uso: powershell -ExecutionPolicy Bypass -File Remove-NexusTasks.ps1 [-WhatIf]
  Saída: 0 = nada a remover ou removidas; 1 = alguma falhou; 2 = não foi possível listar.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()
$ErrorActionPreference = 'Stop'
$markerPattern = '\[nexus\.[a-z0-9.-]+/[0-9]+\]'

try {
    $tasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.Description -and $_.Description -match $markerPattern })
} catch {
    Write-Host "Não foi possível listar as tarefas agendadas: $($_.Exception.Message)"
    exit 2
}
if ($tasks.Count -eq 0) { Write-Host 'Nenhuma tarefa agendada do NEXUS.'; exit 0 }

$failed = 0
foreach ($t in $tasks) {
    $full = "$($t.TaskPath)$($t.TaskName)"
    if ($PSCmdlet.ShouldProcess($full, 'Remover tarefa agendada do NEXUS')) {
        try {
            Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false -ErrorAction Stop
            Write-Host "Removida: $full"
        } catch {
            Write-Host "Falhou ao remover ${full}: $($_.Exception.Message)"
            $failed++
        }
    }
}
if ($failed -gt 0) { exit 1 }
exit 0
