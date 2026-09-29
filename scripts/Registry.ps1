. "$PSScriptRoot\Common.ps1"

function Get-PrincipalPartition {
    param(
        [ValidateSet('Workload', 'Delegated')][string]$Mode,
        [guid]$TenantId, [guid]$ObjectId, [guid]$ClientId
    )
    $text = "$Mode|$($TenantId.ToString('D'))|$($ObjectId.ToString('D'))|$($ClientId.ToString('D'))"
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))
    return 'principal:' + [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-PolicyHeaders {
    return @{
        'x-ms-version' = '2019-02-02'
        'x-ms-date' = [DateTime]::UtcNow.ToString('R')
        Accept = 'application/json;odata=nometadata'
        DataServiceVersion = '3.0'
    }
}

function Add-Int64Property {
    param([System.Collections.IDictionary]$Entity, [string]$Name, [long]$Value)
    if ($Value -le 0) { throw "$Name must be a positive Int64." }
    $Entity["$Name@odata.type"] = 'Edm.Int64'
    $Entity[$Name] = $Value.ToString([Globalization.CultureInfo]::InvariantCulture)
}

function Ensure-PolicyEntity {
    param([System.Collections.IDictionary]$State, [hashtable]$Entity)
    $partition = $Entity.PartitionKey.Replace("'", "''")
    $row = $Entity.RowKey.Replace("'", "''")
    $filter = [uri]::EscapeDataString("PartitionKey eq '$partition' and RowKey eq '$row'")
    $origin = "https://$($State.azure.storageAccountName).table.core.windows.net"
    $token = Get-AzureBearer 'https://storage.azure.com/'
    $result = Invoke-JsonRequest -Uri "$origin/policies()?`$filter=$filter&`$top=2" -Bearer $token `
        -Headers (Get-PolicyHeaders) -Operation 'Policy seed lookup'
    if ($result.value.Count -gt 1) { throw 'Policy lookup unexpectedly returned multiple entities.' }
    if ($result.value.Count -eq 1) {
        foreach ($property in $Entity.Keys) {
            if ($property.EndsWith('@odata.type')) { continue }
            if ($result.value[0][$property] -cne $Entity[$property]) {
                throw "Existing policy $($Entity.PartitionKey)/$($Entity.RowKey) differs at $property. No disabled grant or changed binding will be silently overwritten."
            }
        }
        return
    }
    Invoke-JsonRequest -Uri "$origin/policies" -Method POST -Bearer $token -Body $Entity `
        -Headers (Get-PolicyHeaders) -Operation 'Policy seed insert' | Out-Null
}

function Initialize-DemoRegistry {
    param([System.Collections.IDictionary]$State)
    if (-not $State.github.ContainsKey('installationId') -or -not $State.github.ContainsKey('repositoryId')) {
        throw 'Verify the App installation and single selected repository first.'
    }
    $owner, $name = $State.repository.Split('/')
    $capability = @{
        PartitionKey = 'capability'; RowKey = 'sandbox-maintenance'; Enabled = $true
        Owner = $owner; Name = $name; BaseBranch = 'main'
    }
    Add-Int64Property $capability 'Version' 1
    Add-Int64Property $capability 'RepositoryId' $State.github.repositoryId
    Add-Int64Property $capability 'InstallationId' $State.github.installationId
    Ensure-PolicyEntity $State $capability
    $principals = @(
        @{ mode = 'Workload'; objectId = $State.azure.ciPrincipalId; clientId = $State.azure.ciClientId },
        @{ mode = 'Delegated'; objectId = $State.delegatedUserObjectId; clientId = $State.entra.delegated.clientId }
    )
    foreach ($principal in $principals) {
        $entity = $capability.Clone()
        $entity.PartitionKey = Get-PrincipalPartition $principal.mode $State.tenantId $principal.objectId $principal.clientId
        $entity.Mode = $principal.mode
        $entity.TenantId = ([guid]$State.tenantId).ToString('D')
        $entity.ObjectId = ([guid]$principal.objectId).ToString('D')
        $entity.ClientId = ([guid]$principal.clientId).ToString('D')
        Add-Int64Property $entity 'CapabilityVersion' 1
        Ensure-PolicyEntity $State $entity
    }
    # Runtime kill switches remain on until bootstrap access has been closed.
    Ensure-PolicyEntity $State @{ PartitionKey = 'control'; RowKey = 'global'; Enabled = $true }
}

function Set-PolicyEnabled {
    param([System.Collections.IDictionary]$State, [string]$PartitionKey, [string]$RowKey, [bool]$Enabled)
    $pk = [uri]::EscapeDataString($PartitionKey.Replace("'", "''"))
    $rk = [uri]::EscapeDataString($RowKey.Replace("'", "''"))
    $uri = "https://$($State.azure.storageAccountName).table.core.windows.net/policies(PartitionKey='$pk',RowKey='$rk')"
    $token = Get-AzureBearer 'https://storage.azure.com/'
    $response = Invoke-JsonRequest -Uri $uri -Bearer $token -Headers (Get-PolicyHeaders) -ReturnResponse -Operation 'Policy lookup'
    $entity = $response.Content | ConvertFrom-Json -AsHashtable
    $entity.Enabled = $Enabled
    if ($entity.ContainsKey('Version')) { Add-Int64Property $entity 'Version' ([long]$entity.Version + 1) }
    foreach ($property in @($entity.Keys)) {
        if ($property.StartsWith('odata.') -or $property -eq 'Timestamp') { $entity.Remove($property) }
    }
    $headers = Get-PolicyHeaders
    $headers['If-Match'] = [string]$response.Headers['ETag'][0]
    if (-not $headers['If-Match']) { throw 'Policy response has no ETag; refusing an unconditional update.' }
    Invoke-JsonRequest -Uri $uri -Method PUT -Bearer $token -Headers $headers -Body $entity -Operation 'Conditional policy update' | Out-Null
}
