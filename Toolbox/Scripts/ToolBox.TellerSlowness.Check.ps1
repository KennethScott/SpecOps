# Teller Slowness Check

#$machine = Read-Host "Enter Workstation IP/MachineName: "
Param($machine)

$INIPath = "\\$machine\c$\ARGO\Client\INI\BPIBSCLI.INI"
$hostsPath = "\\$machine\c$\Windows\System32\drivers\etc\hosts"
#Get-WmiObject win32_computersystem -ComputerName $machine | Select-Object Model

$IssueCount = 0

#Test and output if LogLevel is wrong
If(Test-Path $INIPath)
{
    #$INI = (Get-Content $INIPath) -Match "BPLOGLEVEL=3"

    If(!((Get-Content $INIPath) -Match "BPLOGLEVEL=3"))
    {
        Write-Host "INI LogLevel is not correct"
        $IssueCount += 1
    }
}


#Test and output if hosts file is corrupt
If(Test-Path $hostsPath)
{
    $hosts = Get-Content $hostsPath -Encoding byte

    $aC = 0
    $lC = 0
    Do
    {
        If($hosts[$aC] -eq 0x00)
        {
            Write-Host "Hosts File is corrupt"
            $lC++
            $IssueCount += 1
            break
        }
        $aC++
    }While ($aC -lt $hosts.length)
}

try 
{
    #Test if machine matches known model with issues (BIOS and Network Driver)
    $Model = Invoke-Command -ComputerName $machine -ScriptBlock {systeminfo}

    If($Model -match "OptiPlex 5050" -or $Model -Match "OptiPlex 5060" -or $Model -Match "OptiPlex 5070")
    {
        Write-Host 'Known problem model'
        $IssueCount += 1
    }
}
catch {}

If($IssueCount -eq 0)
{
    Write-Host 'No known slowness issues were identified on ' $machine
}

Get-Service -ComputerName $Machine -Name "BPIWSF"
