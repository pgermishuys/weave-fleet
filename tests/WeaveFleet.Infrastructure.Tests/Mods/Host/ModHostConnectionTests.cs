using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>Starting a host process, with /bin/sh as Bun and a shell script as host.js. Unix only.</summary>
public sealed class ModHostConnectionTests : IDisposable
{
    private const string Initialized = """{"jsonrpc":"2.0","id":1,"result":{"protocol":1,"hostVersion":"h1","bunVersion":"b1"}}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-modhost-{Guid.NewGuid():N}");
    private readonly ModHostConnectionFactory _factory = new(NullLogger<ModHostConnectionFactory>.Instance);

    public ModHostConnectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModHostLaunch Launch(string script)
    {
        var path = Path.Combine(_root, "host.sh");
        File.WriteAllText(path, script);
        return new ModHostLaunch("/bin/sh", path, Path.Combine(_root, "work"), "1.2.3", "user-a");
    }

    private Task<IModHostConnection> StartAsync(string script)
        => _factory.StartAsync(Launch(script), new Calls(), CancellationToken.None);

    [Fact]
    public async Task Starts_with_an_empty_environment_in_the_working_folder_and_reads_initialize()
    {
        if (OperatingSystem.IsWindows())
            return;
        Environment.SetEnvironmentVariable("FLEET_SECRET_FOR_TEST", "secret");
        var env = Path.Combine(_root, "env.txt");

        await using var connection = await StartAsync($"env > '{env}'; pwd > '{env}.pwd'\nread line\necho '{Initialized}'\nread rest");

        connection.Host.ShouldBe(new ModHostInitializeResult(1, "h1", "b1"));
        File.ReadAllText(env).ShouldNotContain("FLEET_SECRET_FOR_TEST");
        Path.GetFileName(File.ReadAllText(env + ".pwd").Trim()).ShouldBe("work");
    }

    [Fact]
    public async Task Another_protocol_is_refused_and_the_process_is_gone()
    {
        if (OperatingSystem.IsWindows())
            return;
        var pid = Path.Combine(_root, "pid");
        var other = Initialized.Replace("\"protocol\":1", "\"protocol\":2");

        var e = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync($"echo $$ > '{pid}'\nread line\necho '{other}'\nread rest"));

        e.Message.ShouldContain("protocol 2");
        await GoneAsync(int.Parse(File.ReadAllText(pid).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Waits for the process to exit (it may have already): on its exit, not on a clock.</summary>
    private static async Task GoneAsync(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    [Fact]
    public async Task A_host_that_exits_at_once_is_not_ready_and_says_its_last_stderr_line()
    {
        if (OperatingSystem.IsWindows())
            return;

        var e = await Should.ThrowAsync<ModHostNotReadyException>(StartAsync("echo 'cannot start: bad thing' >&2\nexit 3"));

        e.Message.ShouldContain("cannot start: bad thing");
    }

    [Fact]
    public async Task A_missing_bun_or_script_is_refused_before_starting()
    {
        var launch = Launch("");

        await Should.ThrowAsync<ModHostNotReadyException>(_factory.StartAsync(launch with { BunPath = "/nope/bun" }, new Calls(), CancellationToken.None));
        await Should.ThrowAsync<ModHostNotReadyException>(_factory.StartAsync(launch with { HostScript = "/nope/host.js" }, new Calls(), CancellationToken.None));
    }

    [Fact]
    public async Task Shutdown_kills_a_host_that_ignores_it_within_the_grace()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var connection = await StartAsync($"read line\necho '{Initialized}'\nsleep 600");

        await connection.ShutdownAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5));

        await connection.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Calls : IModHostCalls
    {
        public Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct) => throw new NotSupportedException();

        public void HandleNotification(string method, JsonElement parameters)
        {
        }
    }
}
