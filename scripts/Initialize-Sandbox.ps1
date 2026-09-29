#requires -Version 7.4
[CmdletBinding()]
param([switch]$AcknowledgeSandboxChanges)
. "$PSScriptRoot\Common.ps1"
if (-not $AcknowledgeSandboxChanges) { throw 'Explicit approval is required to initialise the isolated sandbox and its repository-scoped protection.' }
$state = Get-DemoState
$repo = Invoke-GitHub "repos/$($state.repository)"
if (-not $repo.private -or -not $repo.permissions.admin) { throw 'Use a private isolated repository on which you have administration permission.' }
if (-not $state.github.ContainsKey('sandboxInitialised') -and $repo.size -gt 0) {
    throw 'Repository is not empty and was not recorded as this demo sandbox. Refusing to overwrite existing work.'
}
if ($repo.full_name -cne $state.repository) {
    throw 'Use the canonical repository owner/name casing from GitHub in state before creating exact OIDC trust.'
}
$user = Invoke-GitHub 'user'
$files = @{
    'README.md' = "# Identity sandbox`n`nSynthetic maintenance PRs only. No production data or dependency updates. The App never merges.`n"
    '.github/CODEOWNERS' = "* @$($user.login)`n"
    '.github/workflows/request-maintenance.yml' = Get-Content (Join-Path $script:DemoRoot 'templates\request-maintenance.yml') -Raw
    '.github/workflows/sandbox-policy.yml' = Get-Content (Join-Path $script:DemoRoot 'templates\sandbox-policy.yml') -Raw
}
if (-not $state.github.ContainsKey('sandboxInitialised')) {
    # Record the bounded destination before the first write so interrupted setup can resume.
    $state.github.sandboxInitialised = $true
    $state.github.seededPaths = @()
    $state.github.repositoryId = $repo.id
    Save-DemoState $state
}
foreach ($path in @('README.md', '.github/CODEOWNERS', '.github/workflows/request-maintenance.yml', '.github/workflows/sandbox-policy.yml')) {
    if ($path -in $state.github.seededPaths) { continue }
    Invoke-GitHub -Path "repos/$($state.repository)/contents/$path" -Method PUT -Body @{
        message = "chore: initialise isolated identity demo ($path)"
        branch = 'main'
        content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($files[$path]))
    } | Out-Null
    $state.github.seededPaths += $path
    Save-DemoState $state
}
Invoke-GitHub -Path "repos/$($state.repository)" -Method PATCH -Body @{
    default_branch = 'main'; allow_auto_merge = $false; delete_branch_on_merge = $false
    allow_merge_commit = $false; allow_rebase_merge = $false; allow_squash_merge = $true
} | Out-Null
Invoke-GitHub -Path "repos/$($state.repository)/actions/permissions/workflow" -Method PUT -Body @{
    default_workflow_permissions = 'read'; can_approve_pull_request_reviews = $false
} | Out-Null
$ruleset = @{
    name = "$($state.prefix)-main-protection"; target = 'branch'; enforcement = 'active'; bypass_actors = @()
    conditions = @{ ref_name = @{ include = @('refs/heads/main'); exclude = @() } }
    rules = @(
        @{ type = 'deletion' },
        @{ type = 'non_fast_forward' },
        @{ type = 'pull_request'; parameters = @{
            required_approving_review_count = 1; dismiss_stale_reviews_on_push = $true
            require_code_owner_review = $true; require_last_push_approval = $true
            required_review_thread_resolution = $true
        } },
        @{ type = 'required_status_checks'; parameters = @{
            strict_required_status_checks_policy = $true
            required_status_checks = @(@{ context = 'sandbox-policy'; integration_id = 15368 })
        } }
    )
}
if ($state.github.ContainsKey('rulesetId')) {
    Invoke-GitHub -Path "repos/$($state.repository)/rulesets/$($state.github.rulesetId)" -Method PUT -Body $ruleset | Out-Null
}
else {
    $created = Invoke-GitHub -Path "repos/$($state.repository)/rulesets" -Method POST -Body $ruleset
    $state.github.rulesetId = $created.id
    Save-DemoState $state
}
$environment = [uri]::EscapeDataString($state.environment)
Invoke-GitHub -Path "repos/$($state.repository)/environments/$environment" -Method PUT -Body @{
    deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true }
} | Out-Null
$policies = Invoke-GitHub "repos/$($state.repository)/environments/$environment/deployment-branch-policies"
if ($policies.branch_policies.Count -eq 0) {
    Invoke-GitHub -Path "repos/$($state.repository)/environments/$environment/deployment-branch-policies" -Method POST `
        -Body @{ name = 'main'; type = 'branch' } | Out-Null
}
elseif ($policies.branch_policies.Count -ne 1 -or $policies.branch_policies[0].name -ne 'main' -or $policies.branch_policies[0].type -ne 'branch') {
    throw 'Environment branch policies are broader than the approved main-only trust.'
}
if ($state.azure.ContainsKey('ciClientId')) {
    $variables = @{
        ENTRA_TENANT_ID = $state.tenantId; CI_CLIENT_ID = $state.azure.ciClientId
        BROKER_API_ID = $state.entra.api.clientId; BROKER_ORIGIN = $state.azure.admissionUrl
    }
    $existing = Invoke-GitHub "repos/$($state.repository)/environments/$environment/variables?per_page=100"
    foreach ($entry in $variables.GetEnumerator()) {
        if ($entry.Key -in @($existing.variables.name)) {
            Invoke-GitHub -Path "repos/$($state.repository)/environments/$environment/variables/$($entry.Key)" `
                -Method PATCH -Body @{ name = $entry.Key; value = $entry.Value } | Out-Null
        }
        else {
            Invoke-GitHub -Path "repos/$($state.repository)/environments/$environment/variables" `
                -Method POST -Body @{ name = $entry.Key; value = $entry.Value } | Out-Null
        }
    }
}
Write-Host 'Sandbox main requires review and checks, with no ruleset bypass. Runtime workflow has only read-only GitHub permissions plus OIDC.'
