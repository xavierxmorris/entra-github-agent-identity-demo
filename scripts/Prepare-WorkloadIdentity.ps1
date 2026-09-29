#requires -Version 7.4
[CmdletBinding()]
param([switch]$AcknowledgeIdentityChanges)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgeIdentityChanges) { throw 'Explicit approval is required to create the isolated resource group, CI identity and exact OIDC trust.' }
$state = Get-DemoState
Assert-DemoContext $state
$exists = Invoke-AzJson @('group', 'exists', '--name', $state.resourceGroup)
if ($exists) {
    $group = Invoke-AzJson @('group', 'show', '--name', $state.resourceGroup)
    if ($group.tags.demoId -ne $state.demoId) { throw 'Resource group is not owned by this demo.' }
}
else {
    Invoke-AzJson @('group', 'create', '--name', $state.resourceGroup, '--location', $state.location,
        '--tags', "demoId=$($state.demoId)", 'workload=entra-github-agent-identity-demo') | Out-Null
}
$name = "id-$($state.prefix)-ci"
$identities = @(Invoke-AzJson @('identity', 'list', '--resource-group', $state.resourceGroup))
$existing = @($identities | Where-Object { $_.name -ceq $name })
if ($existing.Count -eq 0) {
    $identity = Invoke-AzJson @('identity', 'create', '--name', $name, '--resource-group', $state.resourceGroup,
        '--location', $state.location, '--tags', 'workload=entra-github-agent-identity-demo', 'environment=demo')
}
elseif ($existing.Count -eq 1 -and $existing[0].tags.workload -eq 'entra-github-agent-identity-demo') { $identity = $existing[0] }
else { throw 'Existing identity does not match the isolated demo ownership.' }
$state.azure.ciClientId = $identity.clientId
$state.azure.ciPrincipalId = $identity.principalId
$state.azure.ciIdentityName = $name
Save-DemoState $state
Invoke-AzJson @('identity', 'federated-credential', 'create', '--name', 'protected-github-environment',
    '--identity-name', $name, '--resource-group', $state.resourceGroup,
    '--issuer', 'https://token.actions.githubusercontent.com',
    '--subject', "repo:$($state.repository):environment:$($state.environment)",
    '--audiences', 'api://AzureADTokenExchange') | Out-Null
$request = @{
    tenantId = $state.tenantId; demoId = $state.demoId
    api = $state.entra.api; delegated = $state.entra.delegated
    workloadRoleId = $state.entra.workloadRoleId; userRoleId = $state.entra.userRoleId
    scopeId = $state.entra.scopeId; delegatedUserObjectId = $state.delegatedUserObjectId
    ciClientId = $identity.clientId; ciPrincipalId = $identity.principalId
}
$request | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $script:DemoRoot '.local\admin-grants.json') -Encoding utf8NoBOM
Write-Host 'CI identity and exact federation prepared without paid compute. Tenant-specific admin grant request saved only in .local\admin-grants.json.'
