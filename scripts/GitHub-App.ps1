. "$PSScriptRoot\Common.ps1"

function ConvertTo-Base64Url {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Import-GitHubPrivateKey {
    param([System.Collections.IDictionary]$State, [Parameter(Mandatory)][string]$Pem)
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($Pem)
        if ($rsa.KeySize -ne 2048) { throw 'This costed demo requires a GitHub RSA-2048 key; do not silently change HSM tier.' }
        $parameters = $rsa.ExportParameters($true)
        $jwk = @{ kty = 'RSA-HSM'; key_ops = @('sign', 'verify') }
        foreach ($entry in @{ n = 'Modulus'; e = 'Exponent'; d = 'D'; p = 'P'; q = 'Q'; dp = 'DP'; dq = 'DQ'; qi = 'InverseQ' }.GetEnumerator()) {
            $jwk[$entry.Key] = ConvertTo-Base64Url $parameters.($entry.Value)
        }
        $bearer = Get-AzureBearer 'https://vault.azure.net'
        $key = Invoke-JsonRequest -Uri "https://$($State.azure.vaultName).vault.azure.net/keys/github-app/import?api-version=7.4" `
            -Method POST -Bearer $bearer -Body @{
                key = $jwk
                attributes = @{ enabled = $true; exp = [DateTimeOffset]::UtcNow.AddDays(90).ToUnixTimeSeconds() }
                tags = @{ purpose = 'github-app-jwt'; demoId = $State.demoId }
            } -Operation 'Private key import'
        if ($key.key.kty -ne 'RSA-HSM' -or
            $key.key.kid -notmatch "^https://$([regex]::Escape($State.azure.vaultName))\.vault\.azure\.net/keys/github-app/[a-f0-9]{32}$") {
            throw 'Imported key protection or version URI differs from the expected vault.'
        }
        $State.github.keyUri = $key.key.kid
        Save-DemoState $State
    }
    finally {
        $rsa.Dispose()
        $Pem = $null
        # Managed strings cannot be reliably zeroed; no private material is persisted.
        if (Get-Variable parameters -ErrorAction SilentlyContinue) {
            foreach ($name in @('D', 'P', 'Q', 'DP', 'DQ', 'InverseQ')) {
                if ($parameters.$name) { [Array]::Clear($parameters.$name, 0, $parameters.$name.Length) }
            }
        }
        $jwk = $null
    }
}

function New-BootstrapAppJwt {
    param([System.Collections.IDictionary]$State)
    if ($State.github.keyUri -notmatch "^https://$([regex]::Escape($State.azure.vaultName))\.vault\.azure\.net/keys/github-app/[a-f0-9]{32}$") {
        throw 'Signing key URI must be the version-pinned key in the recorded demo vault.'
    }
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"RS256","typ":"JWT"}'))
    $payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes((@{
        iat = $now - 60; exp = $now + 480; iss = [string]$State.github.appId
    } | ConvertTo-Json -Compress)))
    $inputValue = "$header.$payload"
    $digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($inputValue))
    $signed = Invoke-JsonRequest -Uri "$($State.github.keyUri)/sign?api-version=7.4" -Method POST `
        -Bearer (Get-AzureBearer 'https://vault.azure.net') `
        -Body @{ alg = 'RS256'; value = (ConvertTo-Base64Url $digest) } -Operation 'Bootstrap App JWT signing'
    return "$inputValue.$($signed.value)"
}

function Write-LocalHtml {
    param([Net.HttpListenerContext]$Context, [string]$Html, [int]$Status = 200)
    $Context.Response.StatusCode = $Status
    $Context.Response.ContentType = 'text/html; charset=utf-8'
    $Context.Response.Headers['Cache-Control'] = 'no-store'
    $Context.Response.Headers['Content-Security-Policy'] = "default-src 'none'; form-action https://github.com; base-uri 'none'; frame-ancestors 'none'"
    $bytes = [Text.Encoding]::UTF8.GetBytes($Html)
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.Close()
}

function Confirm-DemoInstallation {
    param([System.Collections.IDictionary]$State, [long]$InstallationId)
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2026-03-10'; 'User-Agent' = 'Entra-GitHub-Identity-Demo' }
    $jwt = New-BootstrapAppJwt $State
    $repoInstallation = Invoke-JsonRequest -Uri "https://api.github.com/repos/$($State.repository)/installation" -Bearer $jwt -Headers $headers -Operation 'Sandbox installation verification'
    if ($InstallationId -eq 0) { $InstallationId = [long]$repoInstallation.id }
    $details = Invoke-JsonRequest -Uri "https://api.github.com/app/installations/$InstallationId" -Bearer $jwt -Headers $headers -Operation 'Installation verification'
    if ($details.account.login -ine $State.repository.Split('/')[0] -or $details.repository_selection -ne 'selected' -or
        $repoInstallation.id -ne $InstallationId -or $details.suspended_at -or
        $details.permissions.contents -ne 'write' -or $details.permissions.pull_requests -ne 'write' -or
        @($details.permissions.Keys | Where-Object { $_ -notin @('metadata', 'contents', 'pull_requests') }).Count) {
        throw 'Installation owner, repository selection, permissions or suspension state is invalid.'
    }
    $readToken = Invoke-JsonRequest -Uri "https://api.github.com/app/installations/$InstallationId/access_tokens" `
        -Method POST -Bearer $jwt -Headers $headers -Body @{ permissions = @{ contents = 'read' } } -Operation 'Read-only installation inspection'
    try {
        $repositories = Invoke-JsonRequest -Uri 'https://api.github.com/installation/repositories?per_page=100' `
            -Bearer $readToken.token -Headers $headers -Operation 'Selected-repository verification'
        if ($repositories.total_count -ne 1 -or $repositories.repositories.Count -ne 1 -or
            $repositories.repositories[0].full_name -ine $State.repository) {
            throw 'The App must be installed on exactly the single selected sandbox repository.'
        }
        $State.github.repositoryId = [long]$repositories.repositories[0].id
        $State.github.installationId = $InstallationId
        Save-DemoState $State
    }
    finally {
        Invoke-JsonRequest -Uri 'https://api.github.com/installation/token' -Method DELETE `
            -Bearer $readToken.token -Headers $headers -Operation 'Read-only inspection token revocation' | Out-Null
    }
}

function Register-DemoGitHubApp {
    param([System.Collections.IDictionary]$State)
    if ($State.github.ContainsKey('appId') -and -not $State.github.ContainsKey('keyUri')) {
        throw 'App registration was recorded but key import did not complete. Recover that App/key explicitly; do not create another App.'
    }
    if ($State.github.ContainsKey('keyUri')) {
        Confirm-DemoInstallation $State 0
        return
    }
    $owner = $State.repository.Split('/')[0]
    $nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $origin = 'http://localhost:8765'
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("$origin/")
    $manifest = @{
        name = "$($State.prefix)-broker-$($State.demoId.Substring(0, 6))"
        url = 'https://github.com'
        public = $false
        description = 'Synthetic Entra-authorised maintenance broker demonstration.'
        redirect_url = "$origin/callback"
        setup_url = "$origin/installed?state=$nonce"
        hook_attributes = @{ active = $false; url = 'https://github.com' }
        default_permissions = @{ contents = 'write'; pull_requests = 'write' }
        default_events = @()
    }
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2026-03-10'; 'User-Agent' = 'Entra-GitHub-Identity-Demo' }
    try {
        $listener.Start()
        if ($State.github.ContainsKey('installationId')) { return }
        if ($State.github.ContainsKey('appId')) {
            throw 'Existing App has no recorded installation. Supply a verified installation ID using recovery instructions, rather than reopening a stale registration callback.'
        }
        Start-Process "$origin/"
        Write-Host 'Approve the organisation-owned GitHub App in your browser, then install it on ONLY the sandbox repository. This consent cannot be bypassed.'
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(20)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $pending = $listener.BeginGetContext($null, $null)
            if (-not $pending.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds([Math]::Max(1, ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds)))) {
                throw 'GitHub App registration/installation timed out.'
            }
            $context = $listener.EndGetContext($pending)
            $request = $context.Request
            if ($request.HttpMethod -ne 'GET') { Write-LocalHtml $context '<p>Method not allowed.</p>' 405; continue }
            if ($request.Url.AbsolutePath -eq '/') {
                $encoded = [Net.WebUtility]::HtmlEncode(($manifest | ConvertTo-Json -Depth 10 -Compress))
                Write-LocalHtml $context "<!doctype html><title>Register identity demo</title><h1>Register the demo GitHub App</h1><p>Private organisation-owned App. Contents and pull requests only.</p><form method='post' action='https://github.com/organizations/$owner/settings/apps/new?state=$nonce'><input type='hidden' name='manifest' value='$encoded'><button type='submit'>Continue to GitHub approval</button></form>"
                continue
            }
            if ($request.QueryString['state'] -cne $nonce) { Write-LocalHtml $context '<p>Invalid state.</p>' 403; continue }
            if ($request.Url.AbsolutePath -eq '/callback') {
                $code = $request.QueryString['code']
                if ($code -notmatch '^[A-Za-z0-9_-]{10,200}$' -or $State.github.ContainsKey('appId')) {
                    Write-LocalHtml $context '<p>Invalid or already consumed registration callback.</p>' 400
                    continue
                }
                $result = Invoke-JsonRequest -Uri "https://api.github.com/app-manifests/$code/conversions" `
                    -Method POST -Headers $headers -Operation 'GitHub manifest conversion'
                if ($result.owner.login -ine $owner) { throw 'GitHub App owner differs from the selected organisation.' }
                $State.github.appId = $result.id
                $State.github.slug = $result.slug
                Save-DemoState $State
                Import-GitHubPrivateKey -State $State -Pem $result.pem
                $result = $null
                $installUrl = "https://github.com/apps/$($State.github.slug)/installations/new"
                Write-LocalHtml $context "<!doctype html><title>Install demo App</title><h1>Key imported into Key Vault</h1><p>Next select ONLY the sandbox repository, not all repositories.</p><a href='$installUrl'>Review and install the App</a>"
                continue
            }
            if ($request.Url.AbsolutePath -eq '/installed') {
                $installation = 0L
                if (-not [long]::TryParse($request.QueryString['installation_id'], [ref]$installation) -or $installation -le 0) {
                    Write-LocalHtml $context '<p>Invalid installation.</p>' 400
                    continue
                }
                Confirm-DemoInstallation $State $installation
                Write-LocalHtml $context '<!doctype html><title>App approved</title><h1>Registration and installation recorded</h1><p>Return to the terminal. The script will close bootstrap access before enabling runtime operations.</p>'
                return
            }
            Write-LocalHtml $context '<p>Not found.</p>' 404
        }
        throw 'GitHub App registration/installation timed out.'
    }
    finally { $listener.Close() }
}
