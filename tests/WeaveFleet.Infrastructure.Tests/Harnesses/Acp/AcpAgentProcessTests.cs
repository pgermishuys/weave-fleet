using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

public sealed class AcpAgentProcessTests
{
    [Fact]
    public async Task an_agent_that_floods_stderr_keeps_only_the_tail()
    {
        if (OperatingSystem.IsWindows())
            return;

        // 300 000 characters, then a last line; the agent stays up until its input closes.
        await using var agent = AcpAgentProcess.Start("sh", ["-c", "head -c 300000 /dev/zero | tr '\\0' x >&2; echo the-end >&2; cat"],
            Path.GetTempPath(), new Dictionary<string, string>(), NullLogger.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!agent.Stderr.EndsWith("the-end", StringComparison.Ordinal))
            await Task.Delay(50, timeout.Token);

        agent.Stderr.Length.ShouldBeLessThanOrEqualTo(AcpAgentProcess.MaxStderr);
    }
}
