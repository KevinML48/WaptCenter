param(
    [string]$TaskName = 'WaptCenter.LocalServiceMachineCatalog',
    [string]$PythonExecutablePath = '',
    [string]$HelperScriptPath = '',
    [string]$RequestPath = '',
    [string]$OutputPath = ''
)

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Administrator privileges are required to register the SYSTEM scheduled task.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($PythonExecutablePath)) {
    $PythonExecutablePath = Join-Path ${env:ProgramFiles(x86)} 'wapt\waptpython.exe'
}

if ([string]::IsNullOrWhiteSpace($HelperScriptPath)) {
    $HelperScriptPath = Join-Path $PSScriptRoot 'wapt_local_service_machine_helper.py'
}

if ([string]::IsNullOrWhiteSpace($RequestPath) -or [string]::IsNullOrWhiteSpace($OutputPath)) {
    $bridgeDirectory = Join-Path $env:ProgramData 'WaptCenter\MachineBridge'
    if ([string]::IsNullOrWhiteSpace($RequestPath)) {
        $RequestPath = Join-Path $bridgeDirectory 'machine_request.json'
    }
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path $bridgeDirectory 'machine_output.json'
    }
}

$bridgeDirectory = Split-Path -Path $RequestPath -Parent
if (-not (Test-Path -LiteralPath $bridgeDirectory)) {
    New-Item -ItemType Directory -Path $bridgeDirectory -Force | Out-Null
}

$arguments = '"{0}" --request-path "{1}" --output-path "{2}"' -f $HelperScriptPath, $RequestPath, $OutputPath
$action = New-ScheduledTaskAction -Execute $PythonExecutablePath -Argument $arguments
$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(5))
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew -StartWhenAvailable
$taskDefinition = New-ScheduledTask -Action $action -Principal $taskPrincipal -Trigger $trigger -Settings $settings

Register-ScheduledTask -TaskName $TaskName -InputObject $taskDefinition -Force | Out-Null

[pscustomobject]@{
    task_name = $TaskName
    python_executable = $PythonExecutablePath
    helper_script = $HelperScriptPath
    request_path = $RequestPath
    output_path = $OutputPath
    run_as = 'SYSTEM'
} | ConvertTo-Json -Depth 4