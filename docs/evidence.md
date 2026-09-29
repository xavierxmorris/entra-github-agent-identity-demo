# Evidence register

Status as of 2026-09-29: **local verification complete; live setup blocked on
tenant-admin permission grants before paid compute deployment**.
Do not convert this register to a deployment-success claim without completing
the live rows.

| Evidence | Result |
| --- | --- |
| Release broker build, .NET SDK 10.0.401 | Passed, zero warnings/errors |
| Broker tests | 177 passed; zero failed/skipped |
| MSAL.NET 4.90.1 delegated-client Release build | Passed, zero warnings/errors |
| Offline provisioning/contract checks | 60 passed |
| PowerShell in-memory RSA-2048 PEM import | Supported; synthetic key only |
| Bicep 0.46.1 compilation | Passed |
| Checkov 3.3.19 Bicep scan | 37 passed, zero failed, 21 documented exceptions |
| Hosted GitHub CI | [Run 36548338888 passed](https://github.com/xavierxmorris/entra-github-agent-identity-demo/actions/runs/36548338888): broker/client builds, tests, script contracts, Bicep and Checkov |
| Public source and binary | [Commit 4f74a5f](https://github.com/xavierxmorris/entra-github-agent-identity-demo/commit/4f74a5f05666cebfdbd26e17e41756e785003d73); [immutable release](https://github.com/xavierxmorris/entra-github-agent-identity-demo/releases/tag/demo-4f74a5f05666), GitHub asset digest verified |
| Azure prerequisite checks | Subscription Owner verified; Linux B1 advertised in approved region; regional usage 0 of 10 |
| Azure resources | Isolated resource group and federated CI managed identity created; paid compute/storage/vault not deployed |
| Entra API and public client | Registrations/service principals created; custom role assignment rejected with HTTP 403 `Authorization_RequestDenied` |
| Entra workload/user permission grants | Pending tenant-admin action; no weaker credential fallback |
| Private sandbox repository | Synthetic workflow, main-only environment, CODEOWNERS and review/check rules configured without bypass actors |
| GitHub App registration and HSM key import | Not performed |
| Workload identity creates an App-authored PR | Pending |
| Delegated workforce identity creates an App-authored PR | Pending |
| Missing/wrong role or scope; wrong caller; cross-caller status denial | Covered locally; live checks pending |
| Admission cannot sign; worker/data public access denied | IaC present; live checks pending |
| Revoked queued grant/global stop | Covered locally; live checks pending |
| Restart, duplicate delivery, outbox crash windows, conditional worker race | Covered locally; live restart check pending |
| Ambiguous outcome and poison visibility | Covered locally; live fault injection pending |
| Million-user throughput, WAF, regional failover, customer banking permissions | Not implemented or claimed |

Only synthetic PR URLs, operation IDs, pass/fail outcomes and sanitised evidence
should be published. Keep tenant/principal IDs, private repository audit detail,
deployment state, raw traces and all credentials out of this public register.

Immutable `broker.zip` SHA-256:
`8ea5b6f246cd75a8c55ef343bdd52b4467c5405ee41f380bf529f60a7cf6d4e3`.
