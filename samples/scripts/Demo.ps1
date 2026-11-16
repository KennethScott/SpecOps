<#
.SYNOPSIS
    Shows off SpecOps: typed parameters, every output stream, progress, object formatting and cancellation.

.DESCRIPTION
    Runs on Windows, Linux and macOS. SpecOps passes the run context in $SpecOps
    ($SpecOps.User, $SpecOps.ClientIp, $SpecOps.RunId, $SpecOps.ScriptId, $SpecOps.ScriptName).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Message,

    [int]$Count = 3,

    [datetime]$When,

    [timespan]$At,

    [ValidateSet('dev', 'test', 'prod')]
    [string]$Environment,

    [string]$Email,

    [switch]$IncludeErrors,

    [int]$DelaySeconds = 5
)

Write-Information "Hello $($SpecOps.User)! This is run $($SpecOps.RunId) from $($SpecOps.ClientIp)."
Write-Information "PowerShell $($PSVersionTable.PSVersion) on $($PSVersionTable.OS), running as $([System.Environment]::UserName)."

foreach ($i in 1..$Count) {
    Write-Progress -Activity 'Demo' -Status "Step $i of $Count" -PercentComplete ([int]($i / $Count * 100))
    "[$i/$Count] $Message"
    Start-Sleep -Milliseconds 400
}
Write-Progress -Activity 'Demo' -Completed

if ($When) { "When: $($When.ToString('D'))" }
if ($At) { "At: $At" }
if ($Environment) { "Environment: $Environment" }
if ($Email) { "Email: $Email" }

Write-Host 'Write-Host goes to the information stream.'
Write-Warning 'This is a warning.'
Write-Verbose 'This is verbose output.' -Verbose
Write-Debug 'This is debug output.' -Debug

Write-Information 'Objects are formatted just like in a console:'
Get-Process |
    Sort-Object WorkingSet64 -Descending |
    Select-Object -First 5 Name, Id, @{ n = 'Memory (MB)'; e = { [math]::Round($_.WorkingSet64 / 1MB) } }

if ($IncludeErrors) {
    Write-Error 'A non-terminating error: the script keeps going.'
    Get-Item -Path (Join-Path $PSScriptRoot 'does-not-exist.txt')
}

Write-Information "Sleeping $DelaySeconds seconds so you can try Cancel..."
Start-Sleep -Seconds $DelaySeconds
'Done.'
