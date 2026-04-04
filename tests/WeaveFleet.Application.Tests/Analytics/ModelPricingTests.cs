using WeaveFleet.Application.Analytics;

namespace WeaveFleet.Application.Tests.Analytics;

public sealed class ModelPricingTests
{
    [Theory]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("claude-opus-4")]
    [InlineData("claude-haiku")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4.1")]
    [InlineData("o3")]
    [InlineData("o4-mini")]
    public void KnownModel_ReturnsPositiveCost(string modelId)
    {
        var cost = ModelPricing.EstimateCost(modelId, inputTokens: 1000, outputTokens: 500,
            reasoningTokens: 0, cacheReadTokens: 0);

        Assert.NotNull(cost);
        Assert.True(cost > 0, $"Expected positive cost for {modelId}");
    }

    [Fact]
    public void UnknownModel_ReturnsNull()
    {
        var cost = ModelPricing.EstimateCost("some-unknown-model-xyz",
            inputTokens: 1000, outputTokens: 500, reasoningTokens: 0, cacheReadTokens: 0);

        Assert.Null(cost);
    }

    [Fact]
    public void NullModelId_ReturnsNull()
    {
        var cost = ModelPricing.EstimateCost(null,
            inputTokens: 1000, outputTokens: 500, reasoningTokens: 0, cacheReadTokens: 0);

        Assert.Null(cost);
    }

    [Fact]
    public void ZeroTokens_ReturnsZeroCost()
    {
        var cost = ModelPricing.EstimateCost("claude-sonnet-4",
            inputTokens: 0, outputTokens: 0, reasoningTokens: 0, cacheReadTokens: 0);

        Assert.NotNull(cost);
        Assert.Equal(0.0, cost!.Value, precision: 10);
    }

    [Fact]
    public void CacheReadTokens_UseLowerPricingThanInput()
    {
        // Same total token count, but all as cache reads vs all as input
        var costFromInput = ModelPricing.EstimateCost("claude-sonnet-4",
            inputTokens: 1000, outputTokens: 0, reasoningTokens: 0, cacheReadTokens: 0);

        var costFromCacheRead = ModelPricing.EstimateCost("claude-sonnet-4",
            inputTokens: 0, outputTokens: 0, reasoningTokens: 0, cacheReadTokens: 1000);

        Assert.NotNull(costFromInput);
        Assert.NotNull(costFromCacheRead);
        Assert.True(costFromCacheRead < costFromInput,
            "Cache read tokens should cost less than full input tokens");
    }

    [Fact]
    public void EstimateReasonableValueForKnownModel()
    {
        // claude-sonnet-4: $3/MTok in, $15/MTok out
        // 1000 input + 500 output = (1000 * 3/1_000_000) + (500 * 15/1_000_000) = $0.003 + $0.0075 = $0.0105
        var cost = ModelPricing.EstimateCost("claude-sonnet-4-20250514",
            inputTokens: 1000, outputTokens: 500, reasoningTokens: 0, cacheReadTokens: 0);

        Assert.NotNull(cost);
        Assert.InRange(cost!.Value, 0.009, 0.015); // within ±30% of expected
    }
}
