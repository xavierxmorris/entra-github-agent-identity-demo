# Deploy and operate the isolated demo

Last source verification: 2026-09-29. Azure CLI 2.87.0, Bicep 0.46.1,
.NET SDK 10.0.401, MSAL.NET 4.90.1 and Checkov 3.3.19 were used locally.

## Approval and prerequisites

No script should be used as an implicit approval to change a live tenant.
Deployment creates an isolated resource group, two B1 plans/apps, three managed
identities, private endpoints/DNS, ZRS storage, a Premium vault and diagnostics.
The low-usage estimate is approximately USD 60/month, **not a spending cap**.
The vault has seven-day purge protection; immediate permanent deletion is
deliberately unavailable. No existing production network or enterprise policy
needs to be changed.

The operator needs resource creation and scoped role-assignment/custom-role
permissions, plus permission to create the two Entra applications and grant
their custom API permissions. An appropriate tenant administrator might be
needed. Runtime identities get no directory administration rights.
Provider registration, regional quotas and provider-side validation must be
checked on the actual subscription; the local build is not a quota test.

Use an empty, private, disposable GitHub organisation repository, with GitHub
Actions enabled and administrative permission. The public source repository
must contain only this generic code. Never include a transcript, customer
drawing, tenant state, token, private key or real customer data.

## Headless setup, with necessary consent

The following examples use placeholders. Windows commands assume the repository
root and an already authenticated `az` and `gh`. No command creates a client
secret or a runtime PAT.

```powershell
az account set --subscription '<subscription-guid>'
.\scripts\Initialize-Demo.ps1 -SubscriptionId '<subscription-guid>' `
  -Repository 'YOUR-ORG/identity-sandbox'
.\scripts\Initialize-Entra.ps1 -AcknowledgeDirectoryChanges
.\scripts\Initialize-Sandbox.ps1 -AcknowledgeSandboxChanges
.\scripts\Deploy-Infrastructure.ps1 -AcknowledgeCostsAndLiveChanges
.\scripts\Initialize-Sandbox.ps1 -AcknowledgeSandboxChanges
```

`Initialize-Demo` only records nonsecret local state. Entra setup creates one
single-tenant API and one public delegated client, an application role and a
delegated scope. Consent is specific to the selected test user, not every user
in the tenant. The user and applications must still satisfy Conditional Access.

The second sandbox pass wires the nonsecret environment variables after the CI
managed identity exists. Trust is exact: repository + `agent-demo` environment.
The environment permits the `main` branch only. Main requires reviews/checks,
has no bypass actors, and covers workflow files with CODEOWNERS. No automatic
merge is enabled. Runtime `GITHUB_TOKEN` permissions remain read-only.

### If tenant permission grants are denied

Azure subscription `Owner` does not confer an Entra directory-administration
role. If provisioning returns `403 Authorization_RequestDenied` while creating
an app-role assignment, do not disable assignment requirements or substitute a
user PAT. The scripts refuse billed compute until directory grants are complete.

You can prepare only the isolated resource group, managed identity and exact
federation so the administrator receives all IDs in one request:

```powershell
.\scripts\Prepare-WorkloadIdentity.ps1 -AcknowledgeIdentityChanges
```

Give a tenant administrator the reviewed repository scripts and the **private**
`.local\admin-grants.json` through an approved internal channel. A suitable
active Entra role is Application Administrator or Cloud Application
Administrator for these custom API grants, with the required delegated Graph
permissions. Global Administrator is not the default request.

Under their own authorised sign-in in the same tenant, the administrator runs:

```powershell
.\scripts\Grant-DemoConsent.ps1 -RequestPath '<private-path>\admin-grants.json' `
  -AcknowledgeCustomApiGrants
```

The script verifies the tagged apps and managed identity before granting the
workload `Maintenance.Request`, assigning the single test user, and granting
that user only `Agent.Invoke` for the public client. It does not grant the
runtime Microsoft Graph permissions, broad tenant-wide consent or Azure
subscription ownership. Eligible PIM roles must be activated through the
normal approved process; the scripts cannot bypass PIM or MFA.

After grants are in place, rerun `Initialize-Entra.ps1`, then the infrastructure
steps. Partial state is retained so the same apps/identity are reused.

### GitHub App registration and HSM import

Prefer a trusted, private administrative host permitted to reach the table and
vault private endpoints. The isolated demo does not provision a private
administrative runner or VPN. Its endpoint NSG permits the application
integration ranges; a separate private admin host requires an explicitly
authorised network/NSG change.

If that connectivity is unavailable, a separately approved selected-IP window
can be used from the trusted operator workstation:

```powershell
.\scripts\Bootstrap-Demo.ps1 -AcknowledgeAppRegistrationAndKeyImport `
  -OperatorIpv4 '<your-single-public-egress-ipv4>' `
  -AcknowledgeTemporaryPublicAccess
```

The script grants temporary, narrowly scoped bootstrap roles, waits for actual
data access, and opens a loopback consent page on `localhost:8765`. Approve the
private organisation-owned App and install it on **only the sandbox repository**.
There is no cookie automation or consent bypass.

The manifest response's key is imported directly from memory as RSA-HSM. It is
not written to a PEM file, a workflow secret, an environment variable or an
ordinary runner. The script verifies the installation/repository selection
using a temporary read-only GitHub token and revokes that token. Unused OAuth
and webhook secrets are discarded; the webhook is inactive.

Both data services are returned to public-access-disabled state and temporary
roles/IP rules are removed in `finally`. Runtime kill switches remain on.
If the process is interrupted, immediately run:

```powershell
.\scripts\Close-Bootstrap.ps1
```

Do not assume an interrupted process closed the window. If App creation
succeeded but key import failed, use GitHub's private-key management to
regenerate/import the key from a trusted operator process. Do not create
duplicate Apps or print the manifest response. If import succeeded and the
installation callback was lost, a repeat bootstrap verifies the existing
installation instead of registering another App.

### Publish and deploy a verified package

Push and commit the sanitised source first. The script requires a clean worktree
and a commit present in the intended public repository.

```powershell
.\scripts\Publish-Package.ps1 -PublicRepository 'YOUR-USER/entra-github-agent-identity-demo' `
  -AcknowledgePublicBinaryPublication
.\scripts\Deploy-Package.ps1 -AcknowledgeLiveCodeDeployment
.\scripts\Set-DemoRuntime.ps1 -Enabled $true -AcknowledgeGitHubWrites
```

Publication enables immutable releases, uploads the package while the release is
a draft, then publishes it. Both publication and deployment verify GitHub's
immutable flag and the asset SHA-256. The remote ZIP contains compiled public
code, not environment-specific configuration. ARM remote-package deployment
does not require opening worker ingress, SCM access or basic publishing auth.

Admission health alone does not prove worker execution. Complete both live
identity paths before marking the deployment verified.

## Execute the two identity paths

Autonomous, with no user credentials in the job:

```powershell
gh workflow run request-maintenance.yml --repo 'YOUR-ORG/identity-sandbox' --ref main
gh run list --repo 'YOUR-ORG/identity-sandbox' --workflow request-maintenance.yml --limit 3
```

The job exchanges Actions OIDC for an Entra API access token, requests only
`sandbox-maintenance`, and polls the caller-isolated operation. It never
receives a GitHub App token or private key.

Delegated workforce test:

```powershell
dotnet run --project .\tools\DelegatedClient -c Release -- .\.local\state.json
```

This uses MSAL system-browser authentication and the `Agent.Invoke` scope.
The cache stays in memory. To investigate/replay the same request, provide the
recorded request GUID as a second argument; do not invent a new ID to bypass an
ambiguous outcome. Neither client claims success for a failed/expired/poisoned
operation. A completed operation produces `demo/maintenance.json` and an App
PR, not a real package update or an automatic merge.

The demo safety budget is **six starts/hour and one active operation per
installation**. Keep the smoke test small; do not load-test GitHub as a proxy
for millions of bank customers.

## Revoke, stop and recover

Immediate administrative runtime stop, using ARM rather than data-plane access:

```powershell
.\scripts\Set-DemoRuntime.ps1 -Enabled $false
```

App-setting changes restart processes; they cannot undo an HTTP request already
sent to GitHub. A more targeted registry revocation uses a private admin host
or an explicitly acknowledged selected-IP window:

```powershell
.\scripts\Set-DemoGrant.ps1 -Mode Workload -Enabled $false -AcknowledgePolicyChange `
  -OperatorIpv4 '<operator-ipv4>' -AcknowledgeTemporaryPublicAccess
```

Use `Delegated` for the test user's grant, or `Global` for the durable control.
Grant versions increment on disable/re-enable, preventing old decisions from
regaining authority just because a grant is enabled again. Registry changes use
ETags, not unconditional overwrites. Re-enabling runtime does not override a
disabled registry grant.

Pending work/outbox messages survive restarts. A stale working record is
reconciled read-only, within its original authority window. Otherwise it is
marked `reconciliation_required`; do not reset its state manually to pending.
Investigate the deterministic branch/PR, original request and correlated audit.
Storage outages remain failures, not successful poison acknowledgements.

## Teardown

Stop runtime, then uninstall the App from the sandbox in GitHub. Delete the
unneeded App registration in GitHub settings when the demonstration is over.
The Azure/Entra cleanup script requires the exact group name and verifies
ownership; it deliberately does not delete GitHub repositories or organisations.

```powershell
.\scripts\Remove-Demo.ps1 -ConfirmResourceGroup 'rg-agentid-demo-3709' `
  -AcknowledgePermanentDemoDeletion
```

Purge-protected vault recovery remains possible for seven days. The public
source and immutable release remain available; cleanup does not erase source
or rewrite Git history. Preserve appropriate audit records before deleting
operation storage. Production retention requires an archival/idempotency
design rather than blindly expiring ledger rows.

## Sources

- [App registration and permissions](https://learn.microsoft.com/graph/api/application-post-applications)
- [App-role assignments](https://learn.microsoft.com/graph/api/serviceprincipal-post-approleassignedto)
- [User-specific delegated consent](https://learn.microsoft.com/graph/api/oauth2permissiongrant-post)
- [MSAL interactive authentication](https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/desktop-mobile/acquiring-tokens-interactively)
- [GitHub manifest protocol](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest)
- [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
- [Network-secured App Service deployment](https://learn.microsoft.com/azure/app-service/deploy-zip#deploy-to-network-secured-apps)
