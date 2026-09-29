#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot\Bootstrap-Access.ps1"
$state = Get-DemoState
Assert-DemoContext $state
Close-BootstrapAccess $state
Write-Host 'Vault and storage public access disabled; recorded temporary roles and operator-IP rules removed.'
