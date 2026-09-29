#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RequestPath, [switch]$AcknowledgeCustomApiGrants)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgeCustomApiGrants) { throw 'Review the request and explicitly approve the custom demo API grants. No Microsoft Graph runtime permissions are requested.' }
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json -AsHashtable
$account = Invoke-AzJson @('account', 'show')
if ($account.tenantId -ne $request.tenantId) { throw 'Sign into the tenant named in the reviewed admin request.' }
foreach ($kind in @('api', 'delegated')) {
    $record = $request[$kind]
    $app = Invoke-Graph "/applications/$($record.objectId)"
    $sp = Invoke-Graph "/servicePrincipals/$($record.principalId)"
    if ($app.appId -ne $record.clientId -or $sp.appId -ne $record.clientId -or
        "agent-identity-demo:$($request.demoId)" -notin $app.tags) {
        throw 'Directory application identity/ownership differs from the reviewed request.'
    }
    if ($kind -eq 'api') {
        if (@($app.appRoles | Where-Object { $_.id -eq $request.workloadRoleId -and $_.value -eq 'Maintenance.Request' -and 'Application' -in $_.allowedMemberTypes }).Count -ne 1 -or
            @($app.appRoles | Where-Object { $_.id -eq $request.userRoleId -and $_.value -eq 'Delegated.Demo' -and 'User' -in $_.allowedMemberTypes }).Count -ne 1 -or
            @($app.api.oauth2PermissionScopes | Where-Object { $_.id -eq $request.scopeId -and $_.value -eq 'Agent.Invoke' }).Count -ne 1) {
            throw 'Requested custom roles/scopes differ from the registered demo API.'
        }
    }
}
$workload = Invoke-Graph "/servicePrincipals/$($request.ciPrincipalId)"
if ($workload.appId -ne $request.ciClientId -or $workload.servicePrincipalType -ne 'ManagedIdentity') {
    throw 'Workload is not the reviewed managed identity.'
}
Ensure-AppRoleAssignment $request.api.principalId $request.ciPrincipalId $request.workloadRoleId
Ensure-AppRoleAssignment $request.api.principalId $request.delegatedUserObjectId $request.userRoleId
Ensure-AppRoleAssignment $request.delegated.principalId $request.delegatedUserObjectId ([guid]::Empty.ToString())
$filter = [uri]::EscapeDataString("clientId eq '$($request.delegated.principalId)'")
$grants = @(Get-GraphCollection "/oauth2PermissionGrants?`$filter=$filter")
$specific = @($grants | Where-Object {
    $_.resourceId -eq $request.api.principalId -and $_.consentType -eq 'Principal' -and
    $_.principalId -eq $request.delegatedUserObjectId
})
if ($specific.Count -eq 0) {
    Invoke-Graph -Path '/oauth2PermissionGrants' -Method POST -Body @{
        clientId = $request.delegated.principalId; resourceId = $request.api.principalId
        consentType = 'Principal'; principalId = $request.delegatedUserObjectId; scope = 'Agent.Invoke'
    } | Out-Null
}
elseif ($specific.Count -ne 1 -or $specific[0].scope -ne 'Agent.Invoke') { throw 'Existing delegated consent differs from the reviewed scope.' }
Write-Host 'Custom API workload and test-user grants configured. No all-users consent or runtime directory permissions were granted.'
