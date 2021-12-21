    #Normally you would explicitly define your parameters, but for this demo I'll just get them dynamically.  This lets me use this one script as input to multiple scriptsettings for demo purposes.
    #Param($StrParam, $IntParam, $SpecOpsCurrentUser, $SpecOpsCurrentUserIP)
    
    Get-Host | Select-Object Version

    Write-Host "Message from inside the running script"
    
    Write-Host "Here are the loaded modules in the script:" 
    Get-Module | Write-Host
    
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
    Write-Information "Example of an error message:"
    Get-ChildItem -Directory "folder-doesnt-exist"

    # There's probably a better way of dynamically getting all the input parms, but I found them in UnboundArguments
    Write-Information "Dumping $($MyInvocation.UnboundArguments.Count/2) input parameter names and values..."

    for ( $i = 0; $i -lt $MyInvocation.UnboundArguments.Count; $i+=2 ) {
        Write-Information "$($args[$i]) : $($args[$i+1])"                   
    } 

