namespace WeaveFleet.E2E.Infrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HarnessSmokeTheoryAttribute : TheoryAttribute
{
    private const string SmokeEnvironmentVariable = "FLEET_HARNESS_SMOKE";

    public HarnessSmokeTheoryAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(SmokeEnvironmentVariable), "1", StringComparison.Ordinal))
            Skip = $"Harness smoke tests are opt-in. Set {SmokeEnvironmentVariable}=1 to run them.";
    }
}
