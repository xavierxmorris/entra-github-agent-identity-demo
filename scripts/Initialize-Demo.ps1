#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$Repository,
    [string]$ResourceGroup = 'rg-agentid-demo-3709',
    [string]$Prefix = 'agentid3709',
    [string]$Location = 'australiaeast'
)
. "$PSScriptRoot\Common.ps1"
if (Test-Path -LiteralPath $script:StatePath) { throw 'State already exists; refusing to replace recorded resource ownership.' }
$account = Invoke-AzJson @('account', 'show')
if ($account.id -ne $SubscriptionId.ToString()) { throw 'Select the intended subscription with az account set before initialisation.' }
$user = Invoke-Graph '/me?$select=id'
$state = @{
    schemaVersion = 1
    demoId = [guid]::NewGuid().ToString()
    subscriptionId = $account.id
    tenantId = $account.tenantId
    operatorObjectId = $user.id
    delegatedUserObjectId = $user.id
    repository = $Repository
    environment = 'agent-demo'
    resourceGroup = $ResourceGroup
    prefix = $Prefix
    location = $Location
    entra = @{}
    azure = @{}
    github = @{}
}
Assert-DemoContext $state
Save-DemoState $state
Write-Host 'Nonsecret state saved under .local. No cloud resources or directory applications were created.'
