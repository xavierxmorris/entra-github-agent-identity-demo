#requires -Version 7.4
[CmdletBinding()]
param([switch]$AcknowledgeLiveCodeDeployment)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgeLiveCodeDeployment) { throw 'Explicit approval is required to change the live demo code.' }
$state = Get-DemoState
Assert-DemoContext $state
if (-not $state.ContainsKey('package')) { throw 'Publish the versioned package first.' }
$release = Invoke-GitHub "repos/$($state.package.repository)/releases/tags/$($state.package.tag)"
$asset = @($release.assets | Where-Object { $_.name -ceq 'broker.zip' })
if ($release.immutable -ne $true -or $asset.Count -ne 1 -or $asset[0].digest -cne "sha256:$($state.package.sha256)") {
    throw 'The remotely verified immutable release and digest are required; a mutable download URL is not sufficient.'
}
$uri = [uri]$state.package.uri
$expected = "https://github.com/$($state.package.repository)/releases/download/$($state.package.tag)/broker.zip"
if ($uri.AbsoluteUri -cne $expected -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Unexpected package URI.' }
$download = Join-Path $script:DemoRoot '.local\verified-broker.zip'
Invoke-WebRequest -Uri $uri -OutFile $download -TimeoutSec 120
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -cne $state.package.sha256) {
    throw 'Remote package digest mismatch; neither app will be updated.'
}
foreach ($app in @($state.azure.workerAppName, $state.azure.admissionAppName)) {
    $path = "https://management.azure.com/subscriptions/$($state.subscriptionId)/resourceGroups/$($state.resourceGroup)/providers/Microsoft.Web/sites/$app/extensions/onedeploy?api-version=2024-11-01"
    Invoke-JsonRequest -Uri $path -Method PUT -Bearer (Get-AzureBearer 'https://management.azure.com/') `
        -Body @{ properties = @{ type = 'zip'; packageUri = $expected; clean = $true; restart = $true } } -Operation 'ARM remote-package deployment' | Out-Null
}
$origin = Assert-BrokerUri $state.azure.admissionUrl
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
do {
    $response = Invoke-WebRequest -Uri "$origin/healthz" -TimeoutSec 15 -SkipHttpErrorCheck -MaximumRedirection 0
    if ($response.StatusCode -eq 200 -and $response.Headers['Content-Type'] -match 'application/json') {
        Write-Host 'Admission health endpoint is responding. Private worker execution must still be verified with an authorised end-to-end operation.'
        return
    }
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Admission did not become healthy; inspect central logs. Do not report deployment success.' }
    Start-Sleep -Seconds 10
} while ($true)
