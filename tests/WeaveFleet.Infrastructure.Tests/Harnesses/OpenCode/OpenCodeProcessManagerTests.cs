using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeProcessManagerTests
{
    [Fact]
    public void OpenCode_gets_none_of_Fleets_own_settings_but_keeps_its_own_and_the_callers()
    {
        Environment.SetEnvironmentVariable("Fleet__OpenCodeEnvTestSecret", "s3cret");
        Environment.SetEnvironmentVariable("ASPNETCORE_OPENCODE_ENV_TEST", "http://0.0.0.0:2113");
        Environment.SetEnvironmentVariable("OPENCODE_ENV_TEST_USER_SETTING", "kept");
        try
        {
            var psi = OpenCodeProcessManager.BuildStartInfo(new OpenCodeProcessOptions
            {
                Port = 0,
                Hostname = "127.0.0.1",
                WorkingDirectory = Path.GetTempPath(),
                Password = "pw",
                Username = "opencode",
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["FLEET_URL"] = "http://127.0.0.1:2113",
                    ["ANTHROPIC_API_KEY"] = "key",
                },
                StartupTimeout = TimeSpan.FromSeconds(1),
            });

            psi.Environment.Keys.ShouldNotContain("Fleet__OpenCodeEnvTestSecret");
            psi.Environment.Keys.ShouldNotContain("ASPNETCORE_OPENCODE_ENV_TEST");
            psi.Environment["OPENCODE_ENV_TEST_USER_SETTING"].ShouldBe("kept");
            psi.Environment["OPENCODE_SERVER_PASSWORD"].ShouldBe("pw");
            psi.Environment["OPENCODE_SERVER_USERNAME"].ShouldBe("opencode");
            psi.Environment.Keys.ShouldContain("OPENCODE_CONFIG_CONTENT");
            psi.Environment["FLEET_URL"].ShouldBe("http://127.0.0.1:2113");
            psi.Environment["ANTHROPIC_API_KEY"].ShouldBe("key");
            psi.Environment.Keys.ShouldContain("PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Fleet__OpenCodeEnvTestSecret", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_OPENCODE_ENV_TEST", null);
            Environment.SetEnvironmentVariable("OPENCODE_ENV_TEST_USER_SETTING", null);
        }
    }
}
