#requires -Version 7.4
[CmdletBinding()]
param([switch]$AcknowledgeCostsAndLiveChanges)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgeCostsAndLiveChanges) {
    throw 'Requires explicit approval: approximately USD 60/month at low usage, new live Azure resources, seven-day purge-protected vault retention.'
}
$state = Get-DemoState
Assert-DemoContext $state
if (-not $state.entra.ContainsKey('api') -or -not $state.entra.ContainsKey('delegated')) {
    throw 'Complete Entra initialisation before Azure provisioning.'
}
if (-not $state.entra.ContainsKey('grantsConfigured') -or -not $state.entra.grantsConfigured) {
    throw 'Entra grants are incomplete. Resolve the directory-admin requirement before creating billed compute.'
}
foreach ($provider in @('Microsoft.Web', 'Microsoft.Storage', 'Microsoft.KeyVault', 'Microsoft.Network',
    'Microsoft.ManagedIdentity', 'Microsoft.OperationalInsights', 'Microsoft.Insights')) {
    $registration = Invoke-AzJson @('provider', 'show', '--namespace', $provider)
    if ($registration.registrationState -ne 'Registered') {
        Invoke-AzJson @('provider', 'register', '--namespace', $provider, '--wait') | Out-Null
    }
}
$capacity = Invoke-AzJson @('appservice', 'list-locations', '--sku', 'B1', '--linux-workers-enabled')
if (@($capacity | Where-Object { ($_.name -replace ' ', '') -eq $state.location }).Count -eq 0) {
    throw 'Linux B1 is not advertised for the approved region. No silent region/SKU substitution.'
}
$exists = Invoke-AzJson @('group', 'exists', '--name', $state.resourceGroup)
if ($exists) {
    $group = Invoke-AzJson @('group', 'show', '--name', $state.resourceGroup)
    if ($group.tags.demoId -ne $state.demoId) { throw 'Resource group ownership tag mismatch. No existing workload will be changed.' }
}
else {
    Invoke-AzJson @('group', 'create', '--name', $state.resourceGroup, '--location', $state.location,
        '--tags', "demoId=$($state.demoId)", 'workload=entra-github-agent-identity-demo') | Out-Null
}
$parameters = @{
    location = @{ value = $state.location }
    prefix = @{ value = $state.prefix }
    apiClientId = @{ value = $state.entra.api.clientId }
    delegatedClientId = @{ value = $state.entra.delegated.clientId }
    githubSubject = @{ value = "repo:$($state.repository):environment:$($state.environment)" }
    killSwitch = @{ value = $true }
}
if ($state.github.ContainsKey('appId')) { $parameters.githubAppId = @{ value = [string]$state.github.appId } }
if ($state.github.ContainsKey('keyUri')) { $parameters.keyVaultKeyUri = @{ value = $state.github.keyUri } }
$parameterFile = Join-Path $script:DemoRoot '.local\deployment.parameters.json'
@{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'; parameters = $parameters
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $parameterFile -Encoding utf8NoBOM
$template = Join-Path $script:DemoRoot 'infra\main.bicep'
& az bicep build --file $template
if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
$arguments = @('--resource-group', $state.resourceGroup, '--template-file', $template,
    '--parameters', "@$parameterFile", '--name', 'agent-identity-demo')
Invoke-AzJson (@('deployment', 'group', 'validate') + $arguments) | Out-Null
$preview = Invoke-AzJson (@('deployment', 'group', 'what-if', '--no-pretty-print') + $arguments)
$preview | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $script:DemoRoot '.local\what-if.json')
if (@($preview.changes | Where-Object { $_.changeType -eq 'Delete' }).Count) {
    throw 'What-if contains deletion. Refusing to apply; inspect .local\what-if.json.'
}
$deployment = Invoke-AzJson (@('deployment', 'group', 'create') + $arguments + @('--mode', 'Incremental'))
if ($deployment.properties.provisioningState -ne 'Succeeded') { throw 'Deployment did not report Succeeded.' }
foreach ($entry in $deployment.properties.outputs.GetEnumerator()) { $state.azure[$entry.Key] = $entry.Value.value }
Save-DemoState $state
Ensure-AppRoleAssignment $state.entra.api.principalId $state.azure.ciPrincipalId $state.entra.workloadRoleId
Write-Host 'Infrastructure deployed with both kill switches on. Bootstrap/import and policy seeding remain mandatory.'
