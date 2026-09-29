#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfirmResourceGroup, [switch]$AcknowledgePermanentDemoDeletion)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgePermanentDemoDeletion) { throw 'Explicit acknowledgement is required to delete the demo resources, operation history and Entra registrations.' }
$state = Get-DemoState
Assert-DemoContext $state
if ($ConfirmResourceGroup -cne $state.resourceGroup) { throw 'Type the exact recorded resource group name. No wildcard or inferred deletion target is accepted.' }
$groupExists = Invoke-AzJson @('group', 'exists', '--name', $ConfirmResourceGroup)
if ($groupExists) {
    $group = Invoke-AzJson @('group', 'show', '--name', $ConfirmResourceGroup)
    if ($group.tags.demoId -ne $state.demoId) { throw 'Resource-group ownership tag differs; refusing deletion.' }
}
if ($state.ContainsKey('bootstrap') -and $state.bootstrap.active) { throw 'Close bootstrap access before teardown.' }
if ($state.github.ContainsKey('installationId')) {
    $owner = $state.repository.Split('/')[0]
    $installed = Invoke-GitHub "orgs/$owner/installations?per_page=100"
    if ($installed.total_count -gt 100) { throw 'Installation enumeration needs pagination; verify removal explicitly before proceeding.' }
    if ($state.github.installationId -in @($installed.installations.id)) {
        throw 'Uninstall the demo GitHub App from the organisation before deleting its signing/deployment resources.'
    }
}
if ($groupExists) {
    if ($state.azure.ContainsKey('workerAppName') -and $state.azure.ContainsKey('admissionAppName')) {
        & "$PSScriptRoot\Set-DemoRuntime.ps1" -Enabled $false
    }
    Invoke-AzJson @('group', 'delete', '--name', $ConfirmResourceGroup, '--yes') | Out-Null
    if (Invoke-AzJson @('group', 'exists', '--name', $ConfirmResourceGroup)) {
        throw 'Resource group still exists. No successful teardown is recorded.'
    }
}
foreach ($kind in @('delegated', 'api')) {
    $app = $state.entra[$kind]
    $found = @(Get-GraphCollection "/applications?`$filter=appId%20eq%20'$($app.clientId)'")
    if ($found.Count -eq 0) { continue }
    if ($found.Count -ne 1) { throw 'Unexpected application lookup cardinality.' }
    $current = $found[0]
    if ($current.appId -ne $app.clientId -or "agent-identity-demo:$($state.demoId)" -notin $current.tags) {
        throw 'Application ownership mismatch. Refusing directory deletion.'
    }
    Invoke-Graph -Path "/applications/$($app.objectId)" -Method DELETE | Out-Null
}
$state.removedAt = [DateTimeOffset]::UtcNow.ToString('O')
Save-DemoState $state
Write-Host 'Recorded Azure group and tagged Entra demo applications removed. Vault recovery retention still applies; GitHub repositories/organisation and App registration were not deleted.'
