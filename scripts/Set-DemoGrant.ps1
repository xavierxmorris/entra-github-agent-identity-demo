#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Workload', 'Delegated', 'Global')][string]$Mode,
    [Parameter(Mandatory)][bool]$Enabled,
    [switch]$AcknowledgePolicyChange,
    [string]$OperatorIpv4 = '',
    [switch]$AcknowledgeTemporaryPublicAccess
)
. "$PSScriptRoot\Bootstrap-Access.ps1"
. "$PSScriptRoot\Registry.ps1"
if (-not $AcknowledgePolicyChange) { throw 'Explicit policy-change acknowledgement is required.' }
$state = Get-DemoState
Invoke-WithBootstrapAccess -State $state -OperatorIpv4 $OperatorIpv4 `
    -AcknowledgeTemporaryPublicAccess:$AcknowledgeTemporaryPublicAccess -Action {
    param($current)
    if ($Mode -eq 'Global') { Set-PolicyEnabled $current 'control' 'global' $Enabled }
    else {
        $objectId = if ($Mode -eq 'Workload') { $current.azure.ciPrincipalId } else { $current.delegatedUserObjectId }
        $clientId = if ($Mode -eq 'Workload') { $current.azure.ciClientId } else { $current.entra.delegated.clientId }
        $partition = Get-PrincipalPartition $Mode $current.tenantId $objectId $clientId
        Set-PolicyEnabled $current $partition 'sandbox-maintenance' $Enabled
    }
}
Write-Host 'Policy updated conditionally; temporary bootstrap access closed. Previously admitted decisions cannot bypass a changed grant version.'
