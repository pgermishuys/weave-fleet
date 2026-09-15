using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>How a profile reaches OpenCode, and how OpenCode's complaints about one come back.</summary>
public sealed class OpenCodeProfileTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"fleet-profiles-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task a_profile_becomes_an_OPENCODE_CONFIG_file_with_its_content()
    {
        var runtime = CreateRuntime();
        const string content = """{ "model": "fakework/work-model" }""";

        var ready = (await runtime.PrepareRuntimeAsync(Context(Profile(content)), CancellationToken.None))
            .ShouldBeOfType<RuntimePreparation.Ready>();

        var environment = ((OpenCodeLaunchArtifacts)ready.Artifacts).EnvironmentVariables;
        var path = environment["OPENCODE_CONFIG"];
        path.ShouldStartWith(Path.Combine(_dataDirectory, "opencode", "profiles"));
        (await File.ReadAllTextAsync(path)).ShouldBe(content);
    }

    [Fact]
    public async Task no_profile_leaves_the_environment_as_it_was()
    {
        var ready = (await CreateRuntime().PrepareRuntimeAsync(Context(profile: null), CancellationToken.None))
            .ShouldBeOfType<RuntimePreparation.Ready>();

        ((OpenCodeLaunchArtifacts)ready.Artifacts).EnvironmentVariables.ContainsKey("OPENCODE_CONFIG").ShouldBeFalse();
    }

    [Fact]
    public void each_version_of_a_profile_gets_its_own_file_so_running_sessions_keep_theirs()
    {
        var runtime = CreateRuntime();

        var first = runtime.WriteProfileFile("""{ "model": "a/one" }""");
        var again = runtime.WriteProfileFile("""{ "model": "a/one" }""");
        var edited = runtime.WriteProfileFile("""{ "model": "a/two" }""");

        again.ShouldBe(first);
        edited.ShouldNotBe(first);
        File.ReadAllText(first).ShouldBe("""{ "model": "a/one" }""");
    }

    [Fact]
    public void a_json_syntax_error_comes_back_as_the_lines_opencode_points_at()
    {
        const string body = """{"name":"ConfigJsonError","data":{"path":"/p.json","message":"\n--- JSONC Input ---\n{ \"model\": \"fake/global-model\", }\n{ oops\n--- Errors ---\nEndOfFileExpected at line 2, column 1\n   Line 2: { oops\n          ^\n--- End ---"}}""";

        var check = OpenCodeHttpClient.ParseConfigError(400, body);

        check.Ok.ShouldBeFalse();
        check.Error.ShouldBe("This profile isn't valid JSON.");
        check.Details.ShouldNotBeNull();
        check.Details[0].ShouldBe("EndOfFileExpected at line 2, column 1");
        check.Details.ShouldContain("   Line 2: { oops");
    }

    [Fact]
    public void values_of_the_wrong_shape_come_back_one_line_per_key()
    {
        const string body = """{"name":"ConfigInvalidError","data":{"path":"/p.json","issues":[{"path":["model"],"message":"Expected string | undefined, got 42"},{"path":["provider","x"],"message":"Expected object | undefined, got \"nope\""}]}}""";

        var check = OpenCodeHttpClient.ParseConfigError(400, body);

        check.Ok.ShouldBeFalse();
        check.Error.ShouldBe("OpenCode rejected a value in this profile.");
        check.Details.ShouldBe(["model: Expected string | undefined, got 42", "provider.x: Expected object | undefined, got \"nope\""]);
    }

    [Fact]
    public void an_error_it_doesnt_know_still_fails_the_check()
    {
        OpenCodeHttpClient.ParseConfigError(500, "not json").Ok.ShouldBeFalse();
        OpenCodeHttpClient.ParseConfigError(400, """{"name":"SomethingNew","data":{"message":"boom"}}""")
            .Details.ShouldBe(["boom"]);
    }

    private OpenCodeHarnessRuntime CreateRuntime() => new(
        httpClientFactory: new TestHttpClientFactory(),
        portAllocator: new PortAllocator(10000, 10099),
        options: new FleetOptions { DatabasePath = Path.Combine(_dataDirectory, "fleet.db") },
        scopeFactory: TestServiceScopeFactory.CreateEmpty(),
        logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
        loggerFactory: NullLoggerFactory.Instance);

    private static HarnessProfile Profile(string content) => new()
    {
        Id = "work",
        HarnessType = "opencode",
        Name = "Work",
        Content = content,
    };

    private static RuntimePreparationContext Context(HarnessProfile? profile) => new()
    {
        UserId = "user-1",
        UserCredentials = [],
        WorkingDirectory = Path.GetTempPath(),
        Profile = profile,
    };

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
