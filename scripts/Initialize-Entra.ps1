#requires -Version 7.4
[CmdletBinding()]
param([switch]$AcknowledgeDirectoryChanges)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgeDirectoryChanges) { throw 'This creates demo apps, service principals and a user-specific delegated grant. Explicit acknowledgement is required.' }
$state = Get-DemoState
Assert-DemoContext $state
$entra = $state.entra

if (-not $entra.ContainsKey('workloadRoleId')) {
    $entra.workloadRoleId = [guid]::NewGuid().ToString()
    $entra.userRoleId = [guid]::NewGuid().ToString()
    $entra.scopeId = [guid]::NewGuid().ToString()
    Save-DemoState $state
}
foreach ($kind in @('api', 'delegated')) {
    if (-not $entra.ContainsKey($kind)) {
        $app = Invoke-Graph -Path '/applications' -Method POST -Body @{
            displayName = "$($state.prefix)-$kind"
            signInAudience = 'AzureADMyOrg'
            tags = @("agent-identity-demo:$($state.demoId)")
        }
        $entra[$kind] = @{ objectId = $app.id; clientId = $app.appId }
        Save-DemoState $state
    }
    $record = $entra[$kind]
    $current = Invoke-Graph "/applications/$($record.objectId)"
    if ($current.appId -ne $record.clientId -or "agent-identity-demo:$($state.demoId)" -notin $current.tags) {
        throw 'Recorded application ownership does not match; refusing to modify it.'
    }
}
$api = $entra.api
$delegated = $entra.delegated
Invoke-Graph -Path "/applications/$($api.objectId)" -Method PATCH -Body @{
    identifierUris = @("api://$($api.clientId)")
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = @(@{
            id = $entra.scopeId; value = 'Agent.Invoke'; type = 'Admin'; isEnabled = $true
            adminConsentDisplayName = 'Request an approved synthetic maintenance operation'
            adminConsentDescription = 'Act for the assigned test user; the broker independently verifies capability entitlement.'
        })
    }
    appRoles = @(
        @{
            id = $entra.workloadRoleId; value = 'Maintenance.Request'; isEnabled = $true
            allowedMemberTypes = @('Application'); displayName = 'Request maintenance'
            description = 'An assigned workload may request a broker-authorised maintenance operation.'
        },
        @{
            id = $entra.userRoleId; value = 'Delegated.Demo'; isEnabled = $true
            allowedMemberTypes = @('User'); displayName = 'Delegated demo user'
            description = 'Assigned test user; this role never substitutes for Agent.Invoke scope.'
        }
    )
} | Out-Null
Invoke-Graph -Path "/applications/$($delegated.objectId)" -Method PATCH -Body @{
    isFallbackPublicClient = $true
    publicClient = @{ redirectUris = @('http://localhost') }
    requiredResourceAccess = @(@{
        resourceAppId = $api.clientId
        resourceAccess = @(@{ id = $entra.scopeId; type = 'Scope' })
    })
} | Out-Null

foreach ($kind in @('api', 'delegated')) {
    $record = $entra[$kind]
    if (-not $record.ContainsKey('principalId')) {
        $found = @(Get-GraphCollection "/servicePrincipals?`$filter=appId%20eq%20'$($record.clientId)'")
        if ($found.Count -gt 1) { throw 'Unexpected duplicate service principals.' }
        $sp = if ($found.Count -eq 1) { $found[0] } else {
            Invoke-Graph -Path '/servicePrincipals' -Method POST -Body @{ appId = $record.clientId }
        }
        $record.principalId = $sp.id
        Save-DemoState $state
    }
    Invoke-Graph -Path "/servicePrincipals/$($record.principalId)" -Method PATCH -Body @{
        appRoleAssignmentRequired = $true
    } | Out-Null
}
Ensure-AppRoleAssignment $api.principalId $state.delegatedUserObjectId $entra.userRoleId
Ensure-AppRoleAssignment $delegated.principalId $state.delegatedUserObjectId ([guid]::Empty.ToString())
$filter = [uri]::EscapeDataString("clientId eq '$($delegated.principalId)'")
$grants = @(Get-GraphCollection "/oauth2PermissionGrants?`$filter=$filter")
$specific = @($grants | Where-Object {
    $_.resourceId -eq $api.principalId -and $_.consentType -eq 'Principal' -and
    $_.principalId -eq $state.delegatedUserObjectId
})
if ($specific.Count -eq 0) {
    $grant = Invoke-Graph -Path '/oauth2PermissionGrants' -Method POST -Body @{
        clientId = $delegated.principalId; resourceId = $api.principalId
        consentType = 'Principal'; principalId = $state.delegatedUserObjectId; scope = 'Agent.Invoke'
    }
    $entra.delegatedGrantId = $grant.id
    Save-DemoState $state
}
elseif ($specific.Count -ne 1 -or $specific[0].scope -ne 'Agent.Invoke') {
    throw 'Existing delegated grant differs from the least-privilege demo grant; inspect it explicitly.'
}
if ($state.azure.ContainsKey('ciPrincipalId')) {
    Ensure-AppRoleAssignment $api.principalId $state.azure.ciPrincipalId $entra.workloadRoleId
}
$entra.grantsConfigured = $true
Save-DemoState $state
Write-Host 'Demo apps and user-specific consent configured. No client secret or directory API permission was granted to a runtime.'
