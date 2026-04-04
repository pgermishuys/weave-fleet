namespace WeaveFleet.Application.Analytics;

/// <summary>Per-token pricing for a model (prices are per individual token, not per million).</summary>
internal sealed record ModelPriceInfo(
    double InputPricePerToken,
    double OutputPricePerToken,
    double CacheReadPricePerToken);

/// <summary>
/// Static model pricing lookup for estimating costs when actual cost data is unavailable.
/// Prices are sourced from published per-million-token rates, divided to per-token.
/// Uses substring matching on model ID so version suffixes don't break lookups.
/// </summary>
public static class ModelPricing
{
    // Prices per token (= published $/MTok ÷ 1,000,000)
    private static readonly (string Prefix, ModelPriceInfo Price)[] PricingTable =
    [
        // Anthropic Claude 4 family
        ("claude-opus-4",           new(15.0 / 1e6,   75.0 / 1e6,   1.50 / 1e6)),
        ("claude-sonnet-4",         new( 3.0 / 1e6,   15.0 / 1e6,   0.30 / 1e6)),
        ("claude-haiku-4",          new( 0.80 / 1e6,   4.0 / 1e6,   0.08 / 1e6)),

        // Anthropic Claude 3.x family (fallback)
        ("claude-3-5-sonnet",       new( 3.0 / 1e6,   15.0 / 1e6,   0.30 / 1e6)),
        ("claude-3-5-haiku",        new( 0.80 / 1e6,   4.0 / 1e6,   0.08 / 1e6)),
        ("claude-3-opus",           new(15.0 / 1e6,   75.0 / 1e6,   1.50 / 1e6)),
        ("claude-haiku",            new( 0.25 / 1e6,   1.25 / 1e6,  0.03 / 1e6)),

        // OpenAI GPT-4.1 family
        ("gpt-4.1-mini",            new( 0.40 / 1e6,   1.60 / 1e6,   0.10 / 1e6)),
        ("gpt-4.1-nano",            new( 0.10 / 1e6,   0.40 / 1e6,   0.025 / 1e6)),
        ("gpt-4.1",                 new( 2.0 / 1e6,    8.0 / 1e6,    0.50 / 1e6)),

        // OpenAI GPT-4o family
        ("gpt-4o-mini",             new( 0.15 / 1e6,   0.60 / 1e6,  0.075 / 1e6)),
        ("gpt-4o",                  new( 2.50 / 1e6,  10.0 / 1e6,   0.625 / 1e6)),

        // OpenAI o-series reasoning models
        ("o4-mini",                 new( 1.10 / 1e6,   4.40 / 1e6,   0.275 / 1e6)),
        ("o3-mini",                 new( 1.10 / 1e6,   4.40 / 1e6,   0.275 / 1e6)),
        ("o3",                      new(10.0 / 1e6,   40.0 / 1e6,   2.50 / 1e6)),
        ("o1-mini",                 new( 1.10 / 1e6,   4.40 / 1e6,   0.275 / 1e6)),
        ("o1",                      new(15.0 / 1e6,   60.0 / 1e6,   3.75 / 1e6)),

        // Google Gemini 2.x family
        ("gemini-2.5-flash",        new( 0.15 / 1e6,   0.60 / 1e6,  0.0375 / 1e6)),
        ("gemini-2.5-pro",          new( 1.25 / 1e6,  10.0 / 1e6,   0.3125 / 1e6)),
        ("gemini-2.0-flash",        new( 0.10 / 1e6,   0.40 / 1e6,  0.025 / 1e6)),
    ];

    /// <summary>
    /// Estimates the cost for a given model and token counts.
    /// Returns <c>null</c> if the model ID is not recognized.
    /// </summary>
    public static double? EstimateCost(
        string? modelId,
        double inputTokens,
        double outputTokens,
        double reasoningTokens,
        double cacheReadTokens)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var normalizedId = modelId.ToLowerInvariant();

        foreach (var (prefix, price) in PricingTable)
        {
            if (normalizedId.Contains(prefix, StringComparison.Ordinal))
            {
                return (inputTokens * price.InputPricePerToken)
                     + (outputTokens * price.OutputPricePerToken)
                     + (reasoningTokens * price.OutputPricePerToken)  // reasoning billed at output rate
                     + (cacheReadTokens * price.CacheReadPricePerToken);
            }
        }

        return null;
    }
}
