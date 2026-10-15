using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.Copilot;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

public sealed class CopilotHarnessRuntimeTests
{
    private sealed class Preferences(Dictionary<string, string> values) : IUserPreferenceRepository
    {
        public Task<string?> GetAsync(string key) => Task.FromResult(values.TryGetValue(key, out var v) ? v : null);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync() => Task.FromResult<IReadOnlyDictionary<string, string>>(values);
        public Task SetAsync(string key, string value) { values[key] = value; return Task.CompletedTask; }
    }

    private static CopilotHarnessRuntime Runtime(params (string Key, string Value)[] on)
    {
        var services = new ServiceCollection();
        services.AddScoped<IUserPreferenceRepository>(_ => new Preferences(on.ToDictionary(p => p.Key, p => p.Value)));
        return new CopilotHarnessRuntime(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task with_the_experimental_switch_off_nothing_starts_and_it_says_why()
    {
        var runtime = Runtime();

        (await runtime.CheckAvailabilityAsync(CancellationToken.None)).Reason.ShouldBe(CopilotHarnessRuntime.FeatureOffMessage);
        (await Should.ThrowAsync<InvalidOperationException>(() => runtime.SpawnAsync(
            new HarnessSpawnOptions { SessionId = "s", WorkingDirectory = Path.GetTempPath(), OwnerUserId = "u" }, CancellationToken.None)))
            .Message.ShouldBe(CopilotHarnessRuntime.FeatureOffMessage);
        await Should.ThrowAsync<InvalidOperationException>(() => runtime.ResumeAsync(
            new HarnessResumeOptions { SessionId = "s", WorkingDirectory = Path.GetTempPath(), ResumeToken = "t", OwnerUserId = "u" }, CancellationToken.None));
        (await runtime.GetCatalogAsync("u", Path.GetTempPath(), null, CancellationToken.None)).ShouldBeNull();
    }
}
