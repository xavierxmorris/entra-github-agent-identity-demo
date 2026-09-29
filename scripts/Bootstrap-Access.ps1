. "$PSScriptRoot\Common.ps1"

function Close-BootstrapAccess {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$State)
    $failures = [System.Collections.Generic.List[string]]::new()
    $closures = @{
        vault = @('keyvault', 'update', '--name', $State.azure.vaultName,
            '--resource-group', $State.resourceGroup, '--public-network-access', 'Disabled')
        storage = @('storage', 'account', 'update', '--name', $State.azure.storageAccountName,
            '--resource-group', $State.resourceGroup, '--public-network-access', 'Disabled')
    }
    foreach ($entry in $closures.GetEnumerator()) {
        try { Invoke-AzJson $entry.Value | Out-Null }
        catch { $failures.Add("$($entry.Key) public access was NOT confirmed disabled: $($_.Exception.Message)") }
    }
    if ($State.ContainsKey('bootstrap')) {
        foreach ($id in @($State.bootstrap.createdRoleIds)) {
            if (-not ($id.StartsWith($State.azure.vaultId + '/providers/Microsoft.Authorization/roleAssignments/', [StringComparison]::OrdinalIgnoreCase) -or
                $id.StartsWith($State.azure.policiesTableId + '/providers/Microsoft.Authorization/roleAssignments/', [StringComparison]::OrdinalIgnoreCase))) {
                $failures.Add('Unexpected temporary role scope; refusing to delete that role.')
                continue
            }
            try {
                Invoke-AzJson @('role', 'assignment', 'delete', '--ids', $id) | Out-Null
                $State.bootstrap.createdRoleIds = @($State.bootstrap.createdRoleIds | Where-Object { $_ -ne $id })
                Save-DemoState $State
            }
            catch { $failures.Add("Temporary role was NOT confirmed removed: $id. $($_.Exception.Message)") }
        }
        if ($State.bootstrap.ipv4) {
            try {
                Invoke-AzJson @('keyvault', 'network-rule', 'remove', '--name', $State.azure.vaultName,
                    '--resource-group', $State.resourceGroup, '--ip-address', $State.bootstrap.ipv4) | Out-Null
            }
            catch { $failures.Add("Vault operator-IP rule was NOT confirmed removed: $($_.Exception.Message)") }
            try {
                Invoke-AzJson @('storage', 'account', 'network-rule', 'remove', '--account-name', $State.azure.storageAccountName,
                    '--resource-group', $State.resourceGroup, '--ip-address', $State.bootstrap.ipv4) | Out-Null
            }
            catch { $failures.Add("Storage operator-IP rule was NOT confirmed removed: $($_.Exception.Message)") }
        }
    }
    $vault = Invoke-AzJson @('keyvault', 'show', '--name', $State.azure.vaultName, '--resource-group', $State.resourceGroup)
    $storage = Invoke-AzJson @('storage', 'account', 'show', '--name', $State.azure.storageAccountName, '--resource-group', $State.resourceGroup)
    if ($vault.properties.publicNetworkAccess -ne 'Disabled' -or $storage.publicNetworkAccess -ne 'Disabled') {
        $failures.Add('Final network state is not private.')
    }
    if ($failures.Count) { throw ("BOOTSTRAP CLEANUP INCOMPLETE. Run Close-Bootstrap.ps1 immediately.`n" + ($failures -join "`n")) }
    if ($State.ContainsKey('bootstrap')) {
        $State.bootstrap.active = $false
        $State.bootstrap.closedAt = [DateTimeOffset]::UtcNow.ToString('O')
        Save-DemoState $State
    }
}

function Invoke-WithBootstrapAccess {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$State,
        [Parameter(Mandatory)][scriptblock]$Action,
        [string]$OperatorIpv4 = '',
        [switch]$AcknowledgeTemporaryPublicAccess,
        [switch]$IncludeKeyImport
    )
    Assert-DemoContext $State
    if ($State.ContainsKey('bootstrap') -and $State.bootstrap.active) {
        throw 'An earlier bootstrap window is still recorded active. Run Close-Bootstrap.ps1 before continuing.'
    }
    if ($OperatorIpv4) {
        $parsed = $null
        if (-not [Net.IPAddress]::TryParse($OperatorIpv4, [ref]$parsed) -or
            $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
            $OperatorIpv4 -eq '0.0.0.0' -or $OperatorIpv4.Contains('/')) {
            throw 'Use exactly one public IPv4 address, not a range or wildcard.'
        }
        if (-not $AcknowledgeTemporaryPublicAccess) { throw 'The selected-IP public bootstrap window needs explicit acknowledgement.' }
    }
    $State.bootstrap = @{
        active = $true; ipv4 = $OperatorIpv4; createdRoleIds = @(); keyImport = [bool]$IncludeKeyImport
        openedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Save-DemoState $State
    try {
        $scopes = @(@{ id = $State.azure.policiesTableId; role = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' })
        if ($IncludeKeyImport) { $scopes += @{ id = $State.azure.vaultId; role = '14b46e9e-c2b7-41b4-b07b-48a6ebf60603' } }
        foreach ($scope in $scopes) {
            $roles = @(Invoke-AzJson @('role', 'assignment', 'list', '--scope', $scope.id,
                '--assignee', $State.operatorObjectId, '--include-inherited'))
            if (@($roles | Where-Object { $_.roleDefinitionId.EndsWith('/' + $scope.role) }).Count -eq 0) {
                $roleName = [guid]::NewGuid().ToString()
                $roleId = $scope.id + '/providers/Microsoft.Authorization/roleAssignments/' + $roleName
                # Record intent first so a lost create response is still recoverable.
                $State.bootstrap.createdRoleIds += $roleId
                Save-DemoState $State
                Invoke-AzJson @('role', 'assignment', 'create', '--name', $roleName, '--scope', $scope.id,
                    '--assignee-object-id', $State.operatorObjectId, '--assignee-principal-type', 'User', '--role', $scope.role) | Out-Null
            }
        }
        if ($OperatorIpv4) {
            if ($IncludeKeyImport) {
                Invoke-AzJson @('keyvault', 'network-rule', 'add', '--name', $State.azure.vaultName,
                    '--resource-group', $State.resourceGroup, '--ip-address', $OperatorIpv4) | Out-Null
                Invoke-AzJson @('keyvault', 'update', '--name', $State.azure.vaultName,
                    '--resource-group', $State.resourceGroup, '--default-action', 'Deny', '--bypass', 'None',
                    '--public-network-access', 'Enabled') | Out-Null
            }
            Invoke-AzJson @('storage', 'account', 'network-rule', 'add', '--account-name', $State.azure.storageAccountName,
                '--resource-group', $State.resourceGroup, '--ip-address', $OperatorIpv4) | Out-Null
            Invoke-AzJson @('storage', 'account', 'update', '--name', $State.azure.storageAccountName,
                '--resource-group', $State.resourceGroup, '--default-action', 'Deny', '--bypass', 'None',
                '--public-network-access', 'Enabled') | Out-Null
        }
        $probes = @(@{
            uri = "https://$($State.azure.storageAccountName).table.core.windows.net/policies()?`$top=1"
            token = Get-AzureBearer 'https://storage.azure.com/'
            headers = @{ 'x-ms-version' = '2019-02-02'; Accept = 'application/json;odata=nometadata' }
        })
        if ($IncludeKeyImport) {
            $probes += @{
                uri = "https://$($State.azure.vaultName).vault.azure.net/keys?api-version=7.4"
                token = Get-AzureBearer 'https://vault.azure.net'; headers = @{}
            }
        }
        foreach ($probe in $probes) {
            $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
            do {
                $probe.headers['x-ms-date'] = [DateTime]::UtcNow.ToString('R')
                $response = Invoke-JsonRequest -Uri $probe.uri -Bearer $probe.token -Headers $probe.headers `
                    -ExpectedStatus @(200, 403) -ReturnResponse -Operation 'Bootstrap access probe'
                if ($response.StatusCode -eq 200) { break }
                if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Bootstrap data access still denied after five minutes; check RBAC, IP and private connectivity.' }
                Write-Host 'Waiting for scoped bootstrap RBAC/network propagation (HTTP 403).'
                Start-Sleep -Seconds 10
            } while ($true)
        }
        & $Action $State
    }
    finally {
        Close-BootstrapAccess $State
    }
}
