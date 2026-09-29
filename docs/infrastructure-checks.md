# Infrastructure validation decisions

Verified locally on 2026-09-29:

```text
az bicep build --file .\infra\main.bicep
Exit 0; no template warnings/errors (CLI update notice only).

checkov -d .\infra --framework bicep --quiet
Checkov 3.3.19: 37 passed, 0 failed, 21 resource-scoped skips,
0 parsing errors, 39 resources; exit 0.
```

This is **not** a claim that every default policy passed. The unsuppressed first
scan was retained in private local evidence: the Bicep scan had 21 failures;
scanning the compiled ARM as well produced another 39 largely duplicate
findings. The final gate scans Bicep source so resource-specific explanations
are retained; the separate Bicep compiler validates the generated ARM.
The scanner did not supply severity metadata without its commercial service.

No broad skip list is used. Each exception is attached to its resource:

| Check | Decision and boundary |
| --- | --- |
| CKV_AZURE_36 | `bypass: None` is deliberately stricter than trusted-service bypass. Runtime uses private endpoints. |
| CKV_AZURE_43 | Parser cannot resolve the generated storage name. Prefix syntax/length is constrained in scripts and offline checks; suffix is eight lowercase `uniqueString` characters. |
| CKV_AZURE_206 | Check only accepts geo-replicated SKUs. Approved `Standard_ZRS` is zone-replicated within one region; no regional DR claim. |
| CKV_AZURE_225 / 212 | B1 and one instance per isolated plan are explicit demo cost/availability tradeoffs, not production HA. |
| CKV_AZURE_17 | Selected authentication is Entra OAuth bearer validation, not mutual TLS. |
| CKV_AZURE_222 | Only admission has authenticated public ingress. Worker is explicitly disabled; configuration child resources inherit the site boundary. |
| CKV_AZURE_13 | Installed check targets legacy `authsettings.enabled`; the template uses `authsettingsV2.platform.enabled` and independently validates JWTs in application code. Logging resources are not authentication settings. |
| CKV_AZURE_63 | Actual logs schema enables `httpLogs.fileSystem.enabled`; check expects `web` config's `httpLoggingEnabled` on every config child, including authentication. |
| CKV_AZURE_65 / 66 | Detailed errors/request tracing stay off to reduce sensitive-data capture. HTTP, console, audit and platform diagnostics remain enabled centrally. |
| CKV_AZURE_80 | Linux .NET 10, not Windows .NET Framework. Runtime is configured on the parent site. |
| CKV_AZURE_88 | No Azure Files dependency is needed; durable state is in Table/Queue. Application filesystem is not the ledger. |

Read the source exceptions before reusing them. They do not justify suppressing
these controls on a different resource or a production deployment. Never enable
detailed errors, a trusted-service bypass or an unnecessary file share merely
to silence a schema-insensitive rule.

## Phase completion

- Local Bicep compilation: passed.
- Checkov: passed with the 21 explicit resource exceptions above, not zero findings.
- Private storage/vault, separate identities, RBAC, TLS and no secret parameters:
  present in generated IaC; live enforcement not yet verified.
- Existing production resources: no reference/recreation; isolated new group only.
- Files are under `infra`; supplied source drawings/transcript were not modified.

Actual provider availability, quota, policy, RBAC propagation and private DNS
must still be checked in Azure. The public admission endpoint and public
Entra-protected Azure Monitor endpoints are deliberate design exceptions.
