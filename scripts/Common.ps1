#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:DemoRoot = Split-Path $PSScriptRoot -Parent
$script:StatePath = Join-Path $script:DemoRoot '.local\state.json'

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $text = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI operation failed (exit $LASTEXITCODE). No subsequent step was run." }
    if ($text) { return ($text -join "`n" | ConvertFrom-Json -AsHashtable) }
}

function Get-DemoState {
    if (-not (Test-Path -LiteralPath $script:StatePath)) {
        throw 'Run Initialize-Demo.ps1 to create ignored, nonsecret deployment state first.'
    }
    return Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json -AsHashtable
}

function Invoke-GitHub {
    param([Parameter(Mandatory)][string]$Path, [string]$Method = 'GET', [object]$Body)
    $arguments = @('api', '--hostname', 'github.com', '--method', $Method, $Path,
        '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2026-03-10')
    $temporary = $null
    try {
        if ($null -ne $Body) {
            $directory = Join-Path $script:DemoRoot '.local'
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $temporary = Join-Path $directory ('github-' + [guid]::NewGuid().ToString('N') + '.json')
            $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
            $arguments += @('--input', $temporary)
        }
        $text = & gh @arguments
        if ($LASTEXITCODE -ne 0) { throw "GitHub administrative operation failed (exit $LASTEXITCODE)." }
        if ($text) { return ($text -join "`n" | ConvertFrom-Json -AsHashtable) }
    }
    finally {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary }
    }
}

function Save-DemoState {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$State)
    $directory = Split-Path $script:StatePath -Parent
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = Join-Path $directory ('state-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $State | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporary -Destination $script:StatePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}

function Assert-DemoContext {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$State)
    $account = Invoke-AzJson @('account', 'show')
    $cloud = Invoke-AzJson @('cloud', 'show')
    if ($cloud.name -ne 'AzureCloud') { throw 'This demo currently supports Azure public cloud only.' }
    if ($account.id -ne $State.subscriptionId -or $account.tenantId -ne $State.tenantId) {
        throw 'Azure CLI subscription/tenant differs from recorded demo state. Select the intended account explicitly.'
    }
    if ($State.prefix -notmatch '^[a-z][a-z0-9]{2,10}$' -or
        $State.resourceGroup -notmatch '^rg-agentid-demo-[a-z0-9-]{1,30}$' -or
        $State.repository -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$') {
        throw 'Invalid demo resource names in local state.'
    }
}

function Get-AzureBearer {
    param([Parameter(Mandatory)][string]$Resource)
    $result = Invoke-AzJson @('account', 'get-access-token', '--resource', $Resource)
    if (-not $result.accessToken) { throw 'Azure CLI did not return an access token.' }
    return [string]$result.accessToken
}

function Invoke-JsonRequest {
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [ValidateSet('GET', 'POST', 'PUT', 'PATCH', 'DELETE')][string]$Method = 'GET',
        [string]$Bearer = '',
        [object]$Body,
        [hashtable]$Headers = @{},
        [string]$Operation = 'API request',
        [int[]]$ExpectedStatus = @(200, 201, 202, 204),
        [switch]$ReturnResponse
    )
    if ($Uri.Scheme -ne 'https' -or $Uri.UserInfo) { throw 'Remote API calls require HTTPS without embedded credentials.' }
    $parameters = @{
        Uri = $Uri; Method = $Method; Headers = $Headers
        MaximumRedirection = 0; TimeoutSec = 45; SkipHttpErrorCheck = $true
    }
    if ($Bearer) {
        $parameters.Authentication = 'Bearer'
        $parameters.Token = ConvertTo-SecureString -String $Bearer -AsPlainText -Force
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 30 -Compress
    }
    $response = Invoke-WebRequest @parameters
    if ([int]$response.StatusCode -notin $ExpectedStatus) {
        # Never include a response body or credential-bearing request in an exception.
        $serviceCode = ''
        if ($Uri.Host -eq 'graph.microsoft.com' -and $response.Content) {
            $errorPayload = $response.Content | ConvertFrom-Json -AsHashtable -ErrorAction SilentlyContinue
            if ($errorPayload -and $errorPayload.error -and $errorPayload.error.code -match '^[A-Za-z0-9_.-]{1,100}$') {
                $serviceCode = " ($($errorPayload.error.code))"
            }
        }
        throw "$Operation failed: HTTP $([int]$response.StatusCode)$serviceCode. Stop and inspect the service's audit logs."
    }
    if ($ReturnResponse) { return $response }
    if ($response.Content) { return $response.Content | ConvertFrom-Json -AsHashtable }
}

function Invoke-Graph {
    param([Parameter(Mandatory)][string]$Path, [string]$Method = 'GET', [object]$Body)
    if ($Path -notmatch '^/(applications|servicePrincipals|oauth2PermissionGrants|me)(/|\?|$|\()') {
        throw 'Graph path is outside this demo provisioning scope.'
    }
    $token = Get-AzureBearer 'https://graph.microsoft.com'
    return Invoke-JsonRequest -Uri "https://graph.microsoft.com/v1.0$Path" -Method $Method -Bearer $token -Body $Body -Operation 'Entra provisioning'
}

function Get-GraphCollection {
    param([Parameter(Mandatory)][string]$Path)
    $next = "https://graph.microsoft.com/v1.0$Path"
    $token = Get-AzureBearer 'https://graph.microsoft.com'
    for ($page = 0; $next; $page++) {
        if ($page -ge 100) { throw 'Graph pagination exceeded the demo provisioning limit.' }
        $uri = [uri]$next
        if ($uri.Host -ne 'graph.microsoft.com' -or -not $uri.AbsolutePath.StartsWith('/v1.0/')) {
            throw 'Unexpected Graph continuation origin.'
        }
        $result = Invoke-JsonRequest -Uri $uri -Bearer $token -Operation 'Entra enumeration'
        foreach ($item in $result.value) { $item }
        $next = $result['@odata.nextLink']
    }
}

function Ensure-AppRoleAssignment {
    param([string]$ResourceId, [string]$PrincipalId, [string]$RoleId)
    foreach ($id in @($ResourceId, $PrincipalId, $RoleId)) {
        if (-not [guid]::TryParse($id, [ref]([guid]::Empty))) { throw 'Invalid role assignment identifier.' }
    }
    $existing = @(Get-GraphCollection "/servicePrincipals/$ResourceId/appRoleAssignedTo")
    if (@($existing | Where-Object { $_.principalId -eq $PrincipalId -and $_.appRoleId -eq $RoleId }).Count -eq 0) {
        Invoke-Graph -Path "/servicePrincipals/$ResourceId/appRoleAssignedTo" -Method POST -Body @{
            principalId = $PrincipalId; resourceId = $ResourceId; appRoleId = $RoleId
        } | Out-Null
    }
}

function Assert-BrokerUri {
    param([Parameter(Mandatory)][string]$Value)
    $uri = [uri]$Value
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.Port -ne 443 -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or
        -not $uri.Host.EndsWith('.azurewebsites.net', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Expected the exact HTTPS App Service origin, without path, query or credentials.'
    }
    return $uri.AbsoluteUri.TrimEnd('/')
}
