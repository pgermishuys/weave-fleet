using NuCode.Permissions;

namespace NuCode;

public sealed class PermissionServiceTests
{
    private static readonly SessionId TestSession = new("test-session");
    private static readonly SessionId OtherSession = new("other-session");

    private static PermissionRuleset AllowAll() => new()
    {
        Name = "allow-all",
        Rules = [new PermissionRule("bash", "*", PermissionAction.Allow)],
    };

    private static PermissionRuleset DenyAll() => new()
    {
        Name = "deny-all",
        Rules = [new PermissionRule("bash", "*", PermissionAction.Deny)],
    };

    private static PermissionRuleset AskAll() => new()
    {
        Name = "ask-all",
        Rules = [new PermissionRule("bash", "*", PermissionAction.Ask)],
    };

    [Fact]
    public async Task AllowedPatternResolvesImmediately()
    {
        var service = new PermissionService();

        // Should complete without blocking
        await service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git status"],
            ["git *"],
            [AllowAll()],
            CancellationToken.None);

        // No pending requests
        service.GetPendingRequests().ShouldBeEmpty();
    }

    [Fact]
    public async Task DeniedPatternThrowsImmediately()
    {
        var service = new PermissionService();

        var ex = await Should.ThrowAsync<PermissionDeniedException>(() =>
            service.RequestPermissionAsync(
                TestSession,
                "bash",
                ["rm -rf /"],
                ["rm *"],
                [DenyAll()],
                CancellationToken.None));

        ex.Permission.ShouldBe("bash");
        ex.Pattern.ShouldContain("rm -rf /");
    }

    [Fact]
    public async Task AskPatternCreatesPendingRequest()
    {
        var service = new PermissionService();

        // Start the request in the background (it will block)
        var requestTask = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["curl http://example.com"],
            ["curl *"],
            [AskAll()],
            CancellationToken.None);

        // Give it a moment to register
        await Task.Delay(50);

        // Should have a pending request
        var pending = service.GetPendingRequests();
        pending.ShouldHaveSingleItem();
        pending[0].Permission.ShouldBe("bash");
        pending[0].Patterns.ShouldContain("curl http://example.com");

        // Clean up — allow to unblock
        service.ReplyToPermission(pending[0].Id, PermissionDecision.Allow);
        await requestTask;
    }

    [Fact]
    public async Task AllowDecisionResolvesRequest()
    {
        var service = new PermissionService();

        var requestTask = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["wget http://example.com"],
            ["wget *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();

        service.ReplyToPermission(pending[0].Id, PermissionDecision.Allow);

        // Should complete successfully
        await requestTask;
        service.GetPendingRequests().ShouldBeEmpty();
    }

    [Fact]
    public async Task DenyDecisionThrowsPermissionDenied()
    {
        var service = new PermissionService();

        var requestTask = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["rm -rf /"],
            ["rm *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();

        service.ReplyToPermission(pending[0].Id, PermissionDecision.Deny);

        await Should.ThrowAsync<PermissionDeniedException>(() => requestTask);
    }

    [Fact]
    public async Task AlwaysAllowAddsRulesToApprovedRuleset()
    {
        var service = new PermissionService();

        var requestTask = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git push"],
            ["git *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();

        service.ReplyToPermission(pending[0].Id, PermissionDecision.AlwaysAllow);
        await requestTask;

        // Approved ruleset should now contain the "git *" rule
        var approved = service.GetApprovedRuleset();
        approved.Rules.ShouldNotBeEmpty();
        approved.Rules.ShouldContain(r => r.Permission == "bash" && r.Pattern == "git *");
    }

    [Fact]
    public async Task AlwaysAllowAutoApprovesMatchingPendingRequests()
    {
        var service = new PermissionService();

        // Create two pending requests for git operations
        var request1 = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git push"],
            ["git *"],
            [AskAll()],
            CancellationToken.None);

        var request2 = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git pull"],
            ["git *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();
        pending.Count.ShouldBe(2);

        // "Always allow" the first request — should auto-approve the second
        service.ReplyToPermission(pending[0].Id, PermissionDecision.AlwaysAllow);

        // Both should complete
        await Task.WhenAll(request1, request2);
    }

    [Fact]
    public async Task DenyRejectsAllPendingInSameSession()
    {
        var service = new PermissionService();

        var request1 = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["curl http://evil.com"],
            ["curl *"],
            [AskAll()],
            CancellationToken.None);

        var request2 = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["wget http://evil.com"],
            ["wget *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();
        pending.Count.ShouldBe(2);

        // Deny the first — should reject the second too
        service.ReplyToPermission(pending[0].Id, PermissionDecision.Deny);

        await Should.ThrowAsync<PermissionDeniedException>(() => request1);
        await Should.ThrowAsync<PermissionDeniedException>(() => request2);
    }

    [Fact]
    public async Task DenyDoesNotAffectOtherSessions()
    {
        var service = new PermissionService();

        var request1 = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["curl http://evil.com"],
            ["curl *"],
            [AskAll()],
            CancellationToken.None);

        var request2 = service.RequestPermissionAsync(
            OtherSession,
            "bash",
            ["wget http://example.com"],
            ["wget *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();
        pending.Count.ShouldBe(2);

        // Find the request for TestSession and deny it
        var testSessionRequest = pending.First(r => r.SessionId == TestSession);
        service.ReplyToPermission(testSessionRequest.Id, PermissionDecision.Deny);

        await Should.ThrowAsync<PermissionDeniedException>(() => request1);

        // Other session request should still be pending
        var remainingPending = service.GetPendingRequests();
        remainingPending.ShouldHaveSingleItem();
        remainingPending[0].SessionId.ShouldBe(OtherSession);

        // Clean up
        service.ReplyToPermission(remainingPending[0].Id, PermissionDecision.Allow);
        await request2;
    }

    [Fact]
    public async Task CancellationTokenCancelsRequest()
    {
        var service = new PermissionService();
        using var cts = new CancellationTokenSource();

        var requestTask = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["dangerous-command"],
            ["dangerous-command *"],
            [AskAll()],
            cts.Token);

        await Task.Delay(50);
        service.GetPendingRequests().ShouldHaveSingleItem();

        await cts.CancelAsync();

        await Should.ThrowAsync<TaskCanceledException>(() => requestTask);
    }

    [Fact]
    public void ReplyToUnknownRequestIdIsNoOp()
    {
        var service = new PermissionService();

        // Should not throw
        service.ReplyToPermission("nonexistent-id", PermissionDecision.Allow);
    }

    [Fact]
    public async Task SessionApprovedRulesetIsUsedInSubsequentEvaluations()
    {
        var service = new PermissionService();

        // First request: ask → always allow
        var request1 = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git push"],
            ["git *"],
            [AskAll()],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingRequests();
        service.ReplyToPermission(pending[0].Id, PermissionDecision.AlwaysAllow);
        await request1;

        // Second request for same pattern should resolve immediately (session-approved ruleset)
        await service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git pull"],
            ["git *"],
            [AskAll()],
            CancellationToken.None);

        service.GetPendingRequests().ShouldBeEmpty();
    }

    [Fact]
    public async Task MixedPatternsWithSomeAllowedAndSomeAsk()
    {
        var service = new PermissionService();
        var partialAllow = new PermissionRuleset
        {
            Name = "partial",
            Rules = [new PermissionRule("bash", "git *", PermissionAction.Allow)],
        };

        // patterns include "git status" (allowed) and "curl" (ask)
        var requestTask = service.RequestPermissionAsync(
            TestSession,
            "bash",
            ["git status", "curl http://example.com"],
            ["git *", "curl *"],
            [partialAllow],
            CancellationToken.None);

        await Task.Delay(50);

        // Should still create a pending request because "curl" needs ask
        var pending = service.GetPendingRequests();
        pending.ShouldHaveSingleItem();

        service.ReplyToPermission(pending[0].Id, PermissionDecision.Allow);
        await requestTask;
    }

    [Fact]
    public async Task MixedPatternsWithSomeDeniedThrowsImmediately()
    {
        var service = new PermissionService();
        var mixed = new PermissionRuleset
        {
            Name = "mixed",
            Rules =
            [
                new PermissionRule("bash", "git *", PermissionAction.Allow),
                new PermissionRule("bash", "rm *", PermissionAction.Deny),
            ],
        };

        // First pattern allowed, second denied → should throw immediately
        await Should.ThrowAsync<PermissionDeniedException>(() =>
            service.RequestPermissionAsync(
                TestSession,
                "bash",
                ["git status", "rm -rf /"],
                ["git *", "rm *"],
                [mixed],
                CancellationToken.None));

        service.GetPendingRequests().ShouldBeEmpty();
    }

    [Fact]
    public void InitialApprovedRulesetIsEmpty()
    {
        var service = new PermissionService();

        var approved = service.GetApprovedRuleset();

        approved.Name.ShouldBe("session-approved");
        approved.Rules.ShouldBeEmpty();
    }
}
