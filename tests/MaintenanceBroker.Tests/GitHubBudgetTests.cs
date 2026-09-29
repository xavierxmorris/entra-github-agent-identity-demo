using System.Net;
using MaintenanceBroker;

namespace MaintenanceBroker.Tests;

public sealed class GitHubBudgetTests
{
    [Fact]
    public void Rate_limit_headers_hold_requests_until_the_longer_retry_or_reset_delay()
    {
        var clock = new ManualClock();
        var budget = new GitHubBudget(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", clock.GetUtcNow().AddSeconds(60).ToUnixTimeSeconds().ToString());
        response.Headers.Add("Retry-After", "120");
        Assert.True(budget.Observe(response));
        Assert.Throws<BrokerException>(budget.DemandAvailable);
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Throws<BrokerException>(budget.DemandAvailable);
        clock.Advance(TimeSpan.FromSeconds(1));
        budget.DemandAvailable();
    }

    [Fact]
    public void Secondary_limit_uses_retry_after_without_primary_exhaustion()
    {
        var clock = new ManualClock();
        var budget = new GitHubBudget(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "500");
        response.Headers.Add("Retry-After", "90");
        Assert.True(budget.Observe(response));
        Assert.Throws<BrokerException>(budget.DemandAvailable);
        clock.Advance(TimeSpan.FromSeconds(90));
        budget.DemandAvailable();
    }

    [Fact]
    public void Retry_after_is_not_shortened_to_a_local_maximum()
    {
        var clock = new ManualClock();
        var budget = new GitHubBudget(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "172800");
        Assert.True(budget.Observe(response));
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Throws<BrokerException>(budget.DemandAvailable);
        clock.Advance(TimeSpan.FromHours(23));
        budget.DemandAvailable();
    }
}
