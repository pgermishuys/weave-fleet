using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Signing in to OpenCode 2's providers from Fleet, live, with dummy keys only. No sign-in is completed with a real
/// provider: the browser sign-in is failed through its own callback, which is what shows Fleet can reach it.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    private const string DummyKey = "sk-ant-dummy-FLEETLIVE-0000";

    [OpenCode2Fact]
    public async Task A_dummy_key_signs_in_offers_the_providers_models_and_signing_out_takes_them_away()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("sign-in-key");

        var before = Ok(await WithSignInAsync(s => s.ListAsync(OpenCode2HarnessSession.Type, cts.Token)));
        var anthropic = before.Providers.Single(p => p.Id == "anthropic");
        anthropic.Methods.ShouldContain(m => m.Type == HarnessSignInMethodTypes.Key);
        anthropic.Connections.ShouldNotContain(c => c.Kind == HarnessSignInConnectionKinds.Credential);
        before.Providers.Single(p => p.Id == "openai").Methods.ShouldContain(m => m.Type == HarnessSignInMethodTypes.OAuth && m.Id == "chatgpt-browser");
        (await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token)).Providers.ShouldNotContain(p => p.Id == "anthropic");

        Ok(await WithSignInAsync(s => s.SignInWithKeyAsync(OpenCode2HarnessSession.Type, "anthropic", DummyKey, null, cts.Token)));
        try
        {
            var signedIn = Ok(await WithSignInAsync(s => s.ListAsync(OpenCode2HarnessSession.Type, cts.Token)))
                .Providers.Single(p => p.Id == "anthropic").Connections.Single(c => c.Kind == HarnessSignInConnectionKinds.Credential);
            signedIn.Active.ShouldBeTrue();
            signedIn.Label.ShouldBe("Anthropic");
            (await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token)).Providers.ShouldContain(p => p.Id == "anthropic");

            Ok(await WithSignInAsync(s => s.SignOutAsync(OpenCode2HarnessSession.Type, signedIn.Id, cts.Token)));
        }
        finally
        {
            await SignOutEverywhereAsync("anthropic", cts.Token);
        }

        Ok(await WithSignInAsync(s => s.ListAsync(OpenCode2HarnessSession.Type, cts.Token)))
            .Providers.Single(p => p.Id == "anthropic").Connections.ShouldNotContain(c => c.Kind == HarnessSignInConnectionKinds.Credential);
        (await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token)).Providers.ShouldNotContain(p => p.Id == "anthropic");
    }

    [OpenCode2Fact]
    public async Task A_key_method_that_needs_more_says_what_is_missing()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var refused = await WithSignInAsync(s => s.SignInWithKeyAsync(OpenCode2HarnessSession.Type, "azure", DummyKey, null, cts.Token));

        refused.IsFailure.ShouldBeTrue();
        refused.Error.Description.ShouldBe("Missing required form field: resourceName");
    }

    [OpenCode2Fact]
    public async Task A_browser_sign_in_that_comes_back_to_this_machine_takes_the_address_a_browser_elsewhere_landed_on()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var attempt = Ok(await WithSignInAsync(s => s.StartAsync(OpenCode2HarnessSession.Type, "openai", "chatgpt-browser", null, cts.Token)));
        try
        {
            attempt.Url.ShouldStartWith("https://auth.openai.com/");
            attempt.NeedsCode.ShouldBeFalse();
            attempt.CallbackAddress.ShouldNotBeNull().ShouldStartWith("http://localhost:");
            Ok(await WithSignInAsync(s => s.GetAttemptAsync(OpenCode2HarnessSession.Type, "openai", attempt.Id, cts.Token)))
                .Status.ShouldBe(HarnessSignInAttemptStates.Pending);

            // What a phone's address bar shows after the user says no: the provider's error goes to V2's listener here.
            Ok(await WithSignInAsync(s => s.ForwardCallbackAsync(
                OpenCode2HarnessSession.Type, "openai", attempt.Id, $"{attempt.CallbackAddress}?error=access_denied", cts.Token)));

            var status = Ok(await WithSignInAsync(s => s.GetAttemptAsync(OpenCode2HarnessSession.Type, "openai", attempt.Id, cts.Token)));
            status.ShouldBe(new HarnessSignInAttemptStatus(HarnessSignInAttemptStates.Failed, "access_denied"));
        }
        finally
        {
            await WithSignInAsync(s => s.CancelAsync(OpenCode2HarnessSession.Type, "openai", attempt.Id, CancellationToken.None));
        }
    }

    [OpenCode2Fact]
    public async Task A_cancelled_browser_sign_in_is_gone()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var attempt = Ok(await WithSignInAsync(s => s.StartAsync(OpenCode2HarnessSession.Type, "openai", "chatgpt-browser", null, cts.Token)));
        Ok(await WithSignInAsync(s => s.CancelAsync(OpenCode2HarnessSession.Type, "openai", attempt.Id, cts.Token)));

        Ok(await WithSignInAsync(s => s.GetAttemptAsync(OpenCode2HarnessSession.Type, "openai", attempt.Id, cts.Token)))
            .Status.ShouldBe(HarnessSignInAttemptStates.Gone);
        var refused = await WithSignInAsync(s => s.ForwardCallbackAsync(
            OpenCode2HarnessSession.Type, "openai", attempt.Id, $"{attempt.CallbackAddress}?code=x", cts.Token));
        refused.Error.Code.ShouldEndWith(".NotFound");
    }

    private async Task SignOutEverywhereAsync(string providerId, CancellationToken ct)
    {
        var list = await WithSignInAsync(s => s.ListAsync(OpenCode2HarnessSession.Type, ct));
        if (list.IsFailure)
            return;
        foreach (var connection in list.Value.Providers.Single(p => p.Id == providerId).Connections)
        {
            if (connection.Kind == HarnessSignInConnectionKinds.Credential)
                await WithSignInAsync(s => s.SignOutAsync(OpenCode2HarnessSession.Type, connection.Id, ct));
        }
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        return result.Value;
    }

    private async Task<T> WithSignInAsync<T>(Func<HarnessSignInService, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<HarnessSignInService>());
    }
}
