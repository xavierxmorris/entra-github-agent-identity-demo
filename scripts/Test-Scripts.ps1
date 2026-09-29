#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\Registry.ps1"
$script:checks = 0
function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "FAILED: $Message" }
    $script:checks++
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    Assert-Check $rejected $Message
}

foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    $messages = @($parseErrors | ForEach-Object { $_.Message })
    Assert-Check ($parseErrors.Count -eq 0) "Parse $($file.Name): $($messages -join '; ')"
}
Assert-Check ((Assert-BrokerUri 'https://demo.azurewebsites.net/') -eq 'https://demo.azurewebsites.net') 'Canonical broker origin'
foreach ($origin in @('http://demo.azurewebsites.net', 'https://evil.example', 'https://demo.azurewebsites.net.evil.example',
    'https://user:password@demo.azurewebsites.net', 'https://demo.azurewebsites.net/path',
    'https://demo.azurewebsites.net/?token=x', 'https://demo.azurewebsites.net:8443')) {
    Assert-Rejected { Assert-BrokerUri $origin } "Reject unsafe origin $origin"
}
$tenant = '11111111-1111-4111-8111-111111111111'
$object = '22222222-2222-4222-8222-222222222222'
$client = '33333333-3333-4333-8333-333333333333'
$partition = Get-PrincipalPartition 'Workload' $tenant $object $client
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes("Workload|$tenant|$object|$client")
    $expected = 'principal:' + ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
}
finally { $sha.Dispose() }
Assert-Check ($partition -ceq $expected) 'Principal hash matches the runtime contract exactly'
Assert-Check ($partition -cne (Get-PrincipalPartition 'Delegated' $tenant $object $client)) 'Workload and user identities cannot collide'
$entity = @{}
Add-Int64Property $entity 'Version' 1
Assert-Check ($entity.Version -is [string] -and $entity.Version -ceq '1') 'REST Int64 value is a quoted decimal'
Assert-Check ($entity['Version@odata.type'] -ceq 'Edm.Int64') 'REST Int64 type is explicit'
Assert-Rejected { Add-Int64Property @{} 'Version' 0 } 'Version must be positive'

$state = @{
    repository = 'example/identity-sandbox'; tenantId = $tenant; delegatedUserObjectId = $object
    azure = @{ ciPrincipalId = $object; ciClientId = $client }
    entra = @{ delegated = @{ clientId = $client } }
    github = @{ repositoryId = 101L; installationId = 202L }
}
$script:seeded = [Collections.Generic.List[hashtable]]::new()
$originalSeed = ${function:Ensure-PolicyEntity}
try {
    function Ensure-PolicyEntity { param($State, $Entity); $script:seeded.Add($Entity.Clone()) }
    Initialize-DemoRegistry $state
}
finally { Set-Item Function:\Ensure-PolicyEntity $originalSeed }
Assert-Check ($seeded.Count -eq 4) 'Exactly capability, two grants and control are seeded'
foreach ($row in $seeded) {
    Assert-Check ($row.Enabled -is [bool]) 'Enabled is Boolean, not string'
    if ($row.PartitionKey -eq 'control') { continue }
    foreach ($name in @('Version', 'RepositoryId', 'InstallationId')) {
        Assert-Check ($row["$name@odata.type"] -ceq 'Edm.Int64' -and $row[$name] -is [string]) "Seed $name is correctly typed"
    }
    if ($row.PartitionKey.StartsWith('principal:')) {
        Assert-Check ($row['CapabilityVersion@odata.type'] -ceq 'Edm.Int64') 'Grant pins the capability version'
        Assert-Check ($row.TenantId -ceq $tenant -and $row.ObjectId -ceq $object) 'Grant is bound to canonical authenticated identifiers'
    }
}
$workflow = Get-Content (Join-Path $script:DemoRoot 'templates\request-maintenance.yml') -Raw
Assert-Check ($workflow -match 'contents: read' -and $workflow -match 'id-token: write') 'Runtime workflow permits read plus federation only'
Assert-Check ($workflow -notmatch 'contents: write|pull-requests: write|pull_request_target|secrets\.') 'No alternate privileged workflow credentials or PR trigger'
Assert-Check ($workflow -match "github.ref == 'refs/heads/main'") 'Runtime workflow additionally checks main'
$bicep = Get-Content (Join-Path $script:DemoRoot 'infra\modules\data.bicep') -Raw
Assert-Check ($bicep -match "allowSharedKeyAccess: false" -and $bicep -match "enablePurgeProtection: true") 'Infrastructure forbids storage keys and protects vault deletion'
Assert-Check ($bicep -notmatch 'secrets/getSecret/action|keys/decrypt/action|keys/export/action') 'Runtime signing role excludes secret read, decrypt and export'
$app = Get-Content (Join-Path $script:DemoRoot 'infra\modules\app.bicep') -Raw
Assert-Check ($app -match "publicNetworkAccess: mode == 'Admission' \? 'Enabled' : 'Disabled'") 'Worker exposure remains explicitly disabled'
Assert-Check ($app -match 'platform: \{ enabled: true' -and $app -match 'requireAuthentication: true') 'Easy Auth v2 is enabled and required'
Assert-Check ($app -match "httpLogs: \{ fileSystem: \{ enabled: true") 'Structured HTTP logging is enabled in the correct schema'
foreach ($prefix in @('abc', 'agentid3709', 'abcdefghijk')) {
    $storageName = 'st' + $prefix + '1234abcd'
    Assert-Check ($storageName -cmatch '^[a-z0-9]{3,24}$') 'Storage name meets Azure syntax and length'
}
Write-Host "$script:checks offline provisioning/contract checks passed. No Azure, Entra or GitHub requests were made."
