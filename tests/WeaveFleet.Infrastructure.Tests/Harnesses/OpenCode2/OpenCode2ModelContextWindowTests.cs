using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>The catalog carries each model's context window from V2's <c>limit.context</c>, and leaves it out when V2 gives none.</summary>
public sealed class OpenCode2ModelContextWindowTests
{
    [Fact]
    public void A_models_context_window_comes_from_its_limit_and_an_unstated_one_stays_empty()
    {
        var providers = new[] { new OpenCode2ProviderInfo { Id = "p", Name = "P", Activation = "enabled" } };
        var models = new[]
        {
            new OpenCode2ModelInfo { Id = "sized", ProviderId = "p", Limit = new OpenCode2ModelLimit { Context = 1_000_000, Output = 64_000 } },
            new OpenCode2ModelInfo { Id = "zero", ProviderId = "p", Limit = new OpenCode2ModelLimit { Context = 0 } },
            new OpenCode2ModelInfo { Id = "none", ProviderId = "p" },
        };

        var listed = OpenCode2Catalog.ToProviderInfos(providers, models).Single().Models.ToDictionary(model => model.Id);

        listed["sized"].ContextWindow.ShouldBe(1_000_000);
        listed["zero"].ContextWindow.ShouldBeNull();
        listed["none"].ContextWindow.ShouldBeNull();
    }
}
