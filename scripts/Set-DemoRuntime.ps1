#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][bool]$Enabled, [switch]$AcknowledgeGitHubWrites)
. "$PSScriptRoot\Common.ps1"
$state = Get-DemoState
Assert-DemoContext $state
if ($Enabled) {
    if (-not $AcknowledgeGitHubWrites) { throw 'Enabling requires explicit approval for the synthetic App-authored PR operation.' }
    if (-not $state.github.ContainsKey('installationId') -or
        ($state.ContainsKey('bootstrap') -and $state.bootstrap.active)) { throw 'Bootstrap is incomplete.' }
    $vault = Invoke-AzJson @('keyvault', 'show', '--name', $state.azure.vaultName, '--resource-group', $state.resourceGroup)
    $storage = Invoke-AzJson @('storage', 'account', 'show', '--name', $state.azure.storageAccountName, '--resource-group', $state.resourceGroup)
    if ($vault.properties.publicNetworkAccess -ne 'Disabled' -or $storage.publicNetworkAccess -ne 'Disabled' -or
        $storage.allowSharedKeyAccess -ne $false -or $vault.properties.enablePurgeProtection -ne $true) {
        throw 'Final data-service protection checks failed; runtime will not be enabled.'
    }
}
$kill = (-not $Enabled).ToString().ToLowerInvariant()
$apps = if ($Enabled) { @($state.azure.workerAppName, $state.azure.admissionAppName) } else { @($state.azure.admissionAppName, $state.azure.workerAppName) }
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($app in $apps) {
    try {
        Invoke-AzJson @('webapp', 'config', 'appsettings', 'set', '--name', $app,
            '--resource-group', $state.resourceGroup, '--settings', "Broker__KillSwitch=$kill") | Out-Null
    }
    catch { $failures.Add("$app update failed: $($_.Exception.Message)") }
}
if ($failures.Count) { throw ($failures -join "`n") }
Write-Host "Runtime enabled=$Enabled. App-setting changes restart processes; this is not instantaneous token revocation or rollback of an in-flight GitHub request."
