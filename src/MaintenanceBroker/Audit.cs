namespace MaintenanceBroker;

public sealed class AuditContext(string correlationId)
{
    public string CorrelationId { get; } = correlationId;
    public CallerIdentity? Caller { get; set; }
    public string? CapabilityId { get; set; }
    public Guid? RequestId { get; set; }
    public long? RepositoryId { get; set; }
    public string? Branch { get; set; }
    public string Decision { get; set; } = "not_evaluated";
}

public sealed class AuditWriter(ILogger<AuditWriter> logger)
{
    public void Write(AuditContext context, string operation, string result,
        string? githubRequestId = null, int? pullRequestNumber = null, int? upstreamStatus = null)
    {
        logger.LogInformation(
            "BrokerAudit CorrelationId={CorrelationId} TenantId={TenantId} ObjectId={ObjectId} " +
            "ClientId={ClientId} IdentityMode={IdentityMode} CapabilityId={CapabilityId} " +
            "PolicyDecision={PolicyDecision} RepositoryId={RepositoryId} Operation={Operation} " +
            "Result={Result} RequestId={RequestId} GitHubRequestId={GitHubRequestId} " +
            "PullRequestNumber={PullRequestNumber} UpstreamStatus={UpstreamStatus} Branch={Branch}",
            context.CorrelationId, context.Caller?.TenantId, context.Caller?.ObjectId,
            context.Caller?.ClientId, context.Caller?.Mode, context.CapabilityId, context.Decision,
            context.RepositoryId, operation, result, context.RequestId, githubRequestId,
            pullRequestNumber, upstreamStatus, context.Branch);
    }
}
