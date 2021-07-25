    Param($StrParam, $IntParam, $SpecOpsCurrentUser, $SpecOpsCurrentUserIP)

    Get-Host | Select-Object Version

    Write-Output "Message from inside the running script"
    Write-Output "This is the value from the first param: $StrParam"
    Write-Output "This is the value from the second param: $IntParam"
    
    Write-Output "Here are the loaded modules in the script:" 
    Get-Module
    
    Write-Information "Wait 5 seconds to allow for cancel..." 
    Wait-Event -SourceIdentifier "ProcessStarted" -Timeout 5

    # write some data to the info/warning streams
    
    Write-Host "A message from write-host"
    Write-Information "A message from write-information"
    
    Write-Warning "A message from write-warning"
    
    Write-Debug -Message "A message from write-debug" -Debug
    
    Write-Verbose -Message "A message from write-verbose" -Verbose
    
    Write-Progress -Activity "Searching Events" -Status "Progress:" -PercentComplete 50
    
    # write a message to the error stream by throwing a non-terminating error
    # note: terminating errors will stop the pipeline.
    Get-ChildItem -Directory "folder-doesnt-exist"

    Write-Information "The current user is: $SpecOpsCurrentUser"
    Write-Information "The current user's IP is: $SpecOpsCurrentUserIP"
    