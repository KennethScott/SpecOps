<#
.SYNOPSIS
    Lists Windows services by status. Windows only (shows how platform-specific scripts are handled).
#>
[CmdletBinding()]
param(
    [ValidateSet('Running', 'Stopped')]
    [string]$Status = 'Running',

    [string]$NameFilter = '*'
)

Get-Service -Name $NameFilter |
    Where-Object Status -EQ $Status |
    Sort-Object DisplayName |
    Select-Object Name, DisplayName, StartType |
    Format-Table -AutoSize
