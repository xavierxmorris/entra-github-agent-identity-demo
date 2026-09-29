#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicRepository,
    [switch]$AcknowledgePublicBinaryPublication
)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgePublicBinaryPublication) { throw 'This publishes the sanitised application binary in a public GitHub release. Explicit approval is required.' }
if ($PublicRepository -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
$repo = Invoke-GitHub "repos/$PublicRepository"
if ($repo.private) { throw 'Network-secured package pull requires the approved public, sanitised source repository.' }
$dirty = & git -C $script:DemoRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the complete reviewed source before publishing a versioned binary.' }
$commit = (& git -C $script:DemoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot resolve source commit.' }
$remoteCommit = Invoke-GitHub "repos/$PublicRepository/commits/$commit"
if ($remoteCommit.sha -cne $commit) { throw 'The local source commit is not published to the intended repository.' }
$publish = Join-Path $script:DemoRoot ".local\publish-$($commit.Substring(0, 12))"
$zip = Join-Path $script:DemoRoot '.local\broker.zip'
$project = Join-Path $script:DemoRoot 'src\MaintenanceBroker\MaintenanceBroker.csproj'
& dotnet publish $project -c Release -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false --output $publish --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
    if ($file.Name -match '^\.env|\.pem$|\.pfx$|\.key$|state\.json|deployment\.parameters\.json') {
        throw "Forbidden package entry: $($file.Name)"
    }
}
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$tag = "demo-$($commit.Substring(0, 12))"
Invoke-GitHub -Path "repos/$PublicRepository/immutable-releases" -Method PUT | Out-Null
& gh release create $tag $zip --draft --repo $PublicRepository --target $commit --title "Identity demo $($commit.Substring(0, 12))" `
    --notes "Synthetic demo application; no environment settings, keys or tokens. SHA256 broker.zip: $hash"
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed. Existing releases are not overwritten.' }
& gh release edit $tag --repo $PublicRepository --draft=false
if ($LASTEXITCODE -ne 0) { throw 'Release remains unpublished; inspect the draft rather than replacing assets.' }
$release = Invoke-GitHub "repos/$PublicRepository/releases/tags/$tag"
$asset = @($release.assets | Where-Object { $_.name -ceq 'broker.zip' })
if ($release.immutable -ne $true -or $asset.Count -ne 1 -or $asset[0].digest -cne "sha256:$hash") {
    throw 'Release immutability or server-reported artifact digest did not verify.'
}
$state = Get-DemoState
$state.package = @{
    repository = $PublicRepository; commit = $commit; tag = $tag; sha256 = $hash; immutable = $true
    uri = "https://github.com/$PublicRepository/releases/download/$tag/broker.zip"
}
Save-DemoState $state
Write-Host "Published versioned sanitised package; SHA256 $hash."
