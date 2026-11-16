<#
.SYNOPSIS
    Basic information about the server SpecOps runs on. Cross-platform.
#>
[CmdletBinding()]
param()

[pscustomobject]@{
    Machine    = [System.Environment]::MachineName
    OS         = $PSVersionTable.OS
    PowerShell = $PSVersionTable.PSVersion.ToString()
    '.NET'     = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    Processors = [System.Environment]::ProcessorCount
    Uptime     = [timespan]::FromMilliseconds([System.Environment]::TickCount64).ToString('d\.hh\:mm\:ss')
    User       = [System.Environment]::UserName
} | Format-List
