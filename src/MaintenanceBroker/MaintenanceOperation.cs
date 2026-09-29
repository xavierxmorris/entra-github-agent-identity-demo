using System.Text;
using System.Text.Json;

namespace MaintenanceBroker;

public interface IMaintenanceOperation
{
    Task<MaintenanceResult> ExecuteAsync(OperationPlan plan, AuditContext context, IExecutionGuard guard,
        CancellationToken cancellationToken);
}

public sealed class MaintenanceOperation(
    IAppJwtFactory jwtFactory,
    GitHubTransport github,
    ActionGate gate,
    AuditWriter audit) : IMaintenanceOperation
{
    public async Task<MaintenanceResult> ExecuteAsync(
        OperationPlan plan, AuditContext context, IExecutionGuard guard, CancellationToken cancellationToken)
    {
        gate.DemandEnabled();
        await guard.DemandAsync(cancellationToken);
        string? installationToken = null;
        try
        {
            audit.Write(context, "sign_app_jwt", "started");
            var jwt = await jwtFactory.CreateAsync(guard, cancellationToken);
            audit.Write(context, "sign_app_jwt", "completed");
            var issued = await github.SendAsync(HttpMethod.Post,
                $"app/installations/{plan.Capability.InstallationId}/access_tokens", jwt, new
                {
                    repository_ids = new[] { plan.Capability.RepositoryId },
                    permissions = new
                    {
                        contents = guard.ReadOnly ? "read" : "write",
                        pull_requests = guard.ReadOnly ? "read" : "write"
                    }
                }, context, "create_installation_token", guard, cancellationToken);
            installationToken = issued.Body.Text("token", 16384);
            if (issued.BlockedUntil is not null)
                await guard.ObserveAsync(issued.BlockedUntil.Value, cancellationToken);

            // A token is never returned to the caller, cached, or placed in a default HTTP header.
            return await ExecuteWithTokenAsync(plan, context, installationToken, guard, cancellationToken);
        }
        catch (BrokerException exception)
        {
            audit.Write(context, "maintenance_pr", exception.Code);
            throw;
        }
        finally
        {
            if (installationToken is not null)
            {
                // Cleanup must survive request cancellation, rate exhaustion and a newly enabled kill switch.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await github.SendAsync(HttpMethod.Delete, "installation/token", installationToken,
                        null, context, "revoke_installation_token", guard, cleanup.Token, cleanup: true);
                }
                catch (Exception exception) when (exception is BrokerException or OperationCanceledException)
                {
                    audit.Write(context, "revoke_installation_token", "revocation_failed");
                    throw new BrokerException("token_revocation_failed");
                }
            }
        }
    }

    private async Task<MaintenanceResult> ExecuteWithTokenAsync(
        OperationPlan plan, AuditContext context, string token, IExecutionGuard guard, CancellationToken cancellationToken)
    {
        var capability = plan.Capability;
        var prefix = $"repos/{capability.Owner}/{capability.Name}";
        var repository = await GetAsync(prefix, "validate_repository");
        if (repository.Body.Number("id") != capability.RepositoryId ||
            !string.Equals(repository.Body.Text("full_name"), $"{capability.Owner}/{capability.Name}",
                StringComparison.OrdinalIgnoreCase))
            throw new BrokerException("repository_identity_mismatch");

        var existing = await GetAsync($"{prefix}/pulls?state=all&head=" +
            $"{Uri.EscapeDataString(capability.Owner + ":" + plan.Branch)}&base=" +
            $"{Uri.EscapeDataString(capability.BaseBranch)}&per_page=2", "find_pull_request");
        if (existing.Body.ValueKind != JsonValueKind.Array || existing.Body.GetArrayLength() > 1)
            throw new BrokerException("ambiguous_pull_request");
        if (existing.Body.GetArrayLength() == 1)
        {
            var pr = existing.Body[0];
            ValidatePullRequest(pr, plan);
            var headSha = pr.Field("head").Sha("sha");
            var baseSha = pr.Field("base").Sha("sha");
            var file = await ReadFileAsync(headSha);
            if (file is null || file.Content != plan.Content)
                throw new BrokerException("existing_operation_content_mismatch");
            await ValidateDiffAsync(baseSha, headSha, allowEmpty: true);
            return Result(pr, "existing", existing.RequestId);
        }

        if (guard.ReadOnly) throw new BrokerException("reconciliation_required", StatusCodes.Status409Conflict);

        var branchPath = $"{prefix}/git/ref/heads/{EncodeRef(plan.Branch)}";
        var branch = await GetAsync(branchPath, "read_demo_branch", allowNotFound: true);
        if (branch.NotFound)
        {
            var baseRef = await GetAsync($"{prefix}/git/ref/heads/{EncodeRef(capability.BaseBranch)}",
                "read_base_branch");
            var baseSha = baseRef.Body.Field("object").Sha("sha");
            await github.SendAsync(HttpMethod.Post, $"{prefix}/git/refs", token,
                new { @ref = $"refs/heads/{plan.Branch}", sha = baseSha },
                context, "create_demo_branch", guard, cancellationToken);
            branch = await GetAsync(branchPath, "read_demo_branch");
        }
        var branchSha = branch.Body.Field("object").Sha("sha");
        var current = await ReadFileAsync(branchSha);
        var hasChange = await ValidateDiffAsync(capability.BaseBranch, branchSha, allowEmpty: true);
        if (hasChange && current?.Content != plan.Content)
            throw new BrokerException("demo_branch_conflict", StatusCodes.Status409Conflict);
        if (current?.Content != plan.Content)
        {
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Content));
            object payload = current is null
                ? new { message = OperationPlan.Title, content, branch = plan.Branch }
                : new { message = OperationPlan.Title, content, branch = plan.Branch, sha = current.Sha };
            await github.SendAsync(HttpMethod.Put, $"{prefix}/contents/{OperationPlan.FilePath}", token,
                payload, context, "write_synthetic_metadata", guard, cancellationToken);
        }

        // Refuse a PR if another writer contaminated the demo branch, including with a workflow change.
        branch = await GetAsync(branchPath, "verify_demo_branch");
        branchSha = branch.Body.Field("object").Sha("sha");
        var finalFile = await ReadFileAsync(branchSha);
        if (finalFile?.Content != plan.Content) throw new BrokerException("demo_branch_conflict");
        await ValidateDiffAsync(capability.BaseBranch, branchSha, allowEmpty: false);
        var created = await github.SendAsync(HttpMethod.Post, $"{prefix}/pulls", token, new
        {
            title = OperationPlan.Title,
            head = plan.Branch,
            @base = capability.BaseBranch,
            body = OperationPlan.PullRequestBody,
            maintainer_can_modify = false
        }, context, "create_pull_request", guard, cancellationToken);
        ValidatePullRequest(created.Body, plan);
        if (created.Body.Field("head").Sha("sha") != branchSha)
            throw new BrokerException("demo_branch_conflict");
        return Result(created.Body, "created", created.RequestId);

        Task<GitHubReply> GetAsync(string path, string operation, bool allowNotFound = false) =>
            github.SendAsync(HttpMethod.Get, path, token, null, context, operation, guard, cancellationToken, allowNotFound);

        async Task<RepositoryFile?> ReadFileAsync(string reference)
        {
            var reply = await GetAsync($"{prefix}/contents/{OperationPlan.FilePath}?ref=" +
                Uri.EscapeDataString(reference), "read_synthetic_metadata", allowNotFound: true);
            if (reply.NotFound) return null;
            if (reply.Body.Text("type") != "file" || reply.Body.Text("path") != OperationPlan.FilePath ||
                reply.Body.Text("encoding") != "base64" || reply.Body.Number("size") is < 0 or > 8192)
                throw new BrokerException("unsafe_existing_file");
            var sha = reply.Body.Sha("sha");
            try
            {
                var bytes = Convert.FromBase64String(reply.Body.Text("content", 16384, allowEmpty: true));
                if (bytes.Length != reply.Body.Number("size"))
                    throw new BrokerException("github_invalid_response");
                return new(sha, new UTF8Encoding(false, true).GetString(bytes));
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
            {
                throw new BrokerException("github_invalid_response");
            }
        }

        async Task<bool> ValidateDiffAsync(string baseRef, string headRef, bool allowEmpty)
        {
            var diff = await GetAsync($"{prefix}/compare/{Uri.EscapeDataString(baseRef)}..." +
                $"{Uri.EscapeDataString(headRef)}?per_page=2", "validate_demo_diff");
            var total = diff.Body.Number("total_commits");
            var files = diff.Body.Field("files");
            if (files.ValueKind != JsonValueKind.Array || total is < 0 or > 1)
                throw new BrokerException("demo_branch_conflict", StatusCodes.Status409Conflict);
            if (allowEmpty && total == 0 && files.GetArrayLength() == 0) return false;
            if (total != 1 || files.GetArrayLength() != 1 ||
                files[0].Text("filename") != OperationPlan.FilePath ||
                files[0].Text("status") is not ("added" or "modified"))
                throw new BrokerException("demo_branch_conflict", StatusCodes.Status409Conflict);
            return true;
        }

        MaintenanceResult Result(JsonElement pr, string status, string? githubRequestId)
        {
            var number = checked((int)pr.Number("number"));
            var result = new MaintenanceResult(status, capability.Id, plan.Key.RequestId,
                capability.RepositoryId, plan.Branch, number,
                $"https://github.com/{capability.Owner}/{capability.Name}/pull/{number}",
                pr.Text("state"), githubRequestId);
            audit.Write(context, "pull_request_result", status, githubRequestId, number);
            return result;
        }
    }

    private static void ValidatePullRequest(JsonElement pr, OperationPlan plan)
    {
        if (pr.Number("number") is <= 0 or > int.MaxValue || pr.Text("state") is not ("open" or "closed") ||
            pr.Field("head").Text("ref") != plan.Branch ||
            pr.Field("head").Field("repo").Number("id") != plan.Capability.RepositoryId ||
            pr.Field("base").Text("ref") != plan.Capability.BaseBranch ||
            pr.Field("base").Field("repo").Number("id") != plan.Capability.RepositoryId)
            throw new BrokerException("pull_request_identity_mismatch");
    }

    private static string EncodeRef(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
    private sealed record RepositoryFile(string Sha, string Content);
}
