#requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$AcknowledgeAppRegistrationAndKeyImport,
    [string]$OperatorIpv4 = '',
    [switch]$AcknowledgeTemporaryPublicAccess
)
. "$PSScriptRoot\Bootstrap-Access.ps1"
. "$PSScriptRoot\GitHub-App.ps1"
. "$PSScriptRoot\Registry.ps1"
if (-not $AcknowledgeAppRegistrationAndKeyImport) {
    throw 'Explicit GitHub App/key-import acknowledgement is required. Never run bootstrap inside an ordinary CI job.'
}
$state = Get-DemoState
Invoke-WithBootstrapAccess -State $state -OperatorIpv4 $OperatorIpv4 `
    -AcknowledgeTemporaryPublicAccess:$AcknowledgeTemporaryPublicAccess -IncludeKeyImport -Action {
    param($current)
    Register-DemoGitHubApp $current
    Initialize-DemoRegistry $current
}
Invoke-AzJson @('webapp', 'config', 'appsettings', 'set', '--name', $state.azure.workerAppName,
    '--resource-group', $state.resourceGroup, '--settings', "Broker__GitHubAppId=$($state.github.appId)",
    "Broker__KeyVaultKeyUri=$($state.github.keyUri)", 'Broker__KillSwitch=true') | Out-Null
Write-Host 'Bootstrap complete; data endpoints are private and temporary rights removed. Runtime remains stopped until verified code deployment and explicit enablement.'
