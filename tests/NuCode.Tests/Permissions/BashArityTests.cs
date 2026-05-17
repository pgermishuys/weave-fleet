using NuCode.Permissions;

namespace NuCode;

public sealed class BashArityTests
{
    [Theory]
    [InlineData("git commit -m msg", "git commit")]
    [InlineData("git push origin main", "git push")]
    [InlineData("git status", "git status")]
    [InlineData("docker compose up -d", "docker compose up")]
    [InlineData("dotnet build -c Release", "dotnet build -c")]
    [InlineData("dotnet test --no-build", "dotnet test --no-build")]
    [InlineData("npm run build", "npm run build")]
    [InlineData("npm install lodash", "npm install")]
    public void GetPrefixReturnsCorrectTokens(string command, string expectedPrefix)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var prefix = BashArity.GetPrefix(tokens);
        var result = string.Join(' ', prefix);

        result.ShouldBe(expectedPrefix);
    }

    [Fact]
    public void GetPrefixUnknownCommandDefaultsToFirstToken()
    {
        var tokens = new[] { "mycustomtool", "--flag", "arg" };

        var prefix = BashArity.GetPrefix(tokens);

        prefix.ShouldHaveSingleItem();
        prefix[0].ShouldBe("mycustomtool");
    }

    [Fact]
    public void GetPrefixEmptyArrayReturnsEmpty()
    {
        var prefix = BashArity.GetPrefix([]);

        prefix.ShouldBeEmpty();
    }

    [Fact]
    public void GetPrefixSingleTokenKnownCommand()
    {
        // "ls" has arity 1, but only one token provided
        var prefix = BashArity.GetPrefix(["ls"]);

        prefix.ShouldHaveSingleItem();
        prefix[0].ShouldBe("ls");
    }

    [Theory]
    [InlineData("ls -la", "ls *")]
    [InlineData("git commit -m 'init'", "git commit *")]
    [InlineData("docker compose up -d", "docker compose up *")]
    [InlineData("npm run build", "npm run build *")]
    [InlineData("mycustomtool --arg", "mycustomtool *")]
    public void BuildPatternReturnsCommandPrefixWithWildcard(string command, string expectedPattern)
    {
        var pattern = BashArity.BuildPattern(command);

        pattern.ShouldBe(expectedPattern);
    }

    [Fact]
    public void BuildPatternEmptyCommandReturnsWildcard()
    {
        var pattern = BashArity.BuildPattern("");

        pattern.ShouldBe("*");
    }

    [Theory]
    [InlineData("cat", 1)]
    [InlineData("ls", 1)]
    [InlineData("rm", 1)]
    [InlineData("git", 2)]
    [InlineData("docker", 2)]
    [InlineData("dotnet", 2)]
    [InlineData("npm", 2)]
    [InlineData("kubectl", 2)]
    [InlineData("aws", 3)]
    [InlineData("docker compose", 3)]
    [InlineData("git config", 3)]
    public void KnownCommandsHaveExpectedArity(string command, int expectedTokenCount)
    {
        // Build a command with enough tokens to hit the arity
        var tokens = new List<string>(command.Split(' '));
        // Pad with extra args to ensure we get the arity number of tokens
        for (var i = tokens.Count; i < expectedTokenCount + 2; i++)
        {
            tokens.Add($"arg{i}");
        }

        var prefix = BashArity.GetPrefix([.. tokens]);

        prefix.Length.ShouldBe(expectedTokenCount);
    }

    [Fact]
    public void LongestPrefixMatchWins()
    {
        // "git config" is arity 3, "git" is arity 2
        // "git config set user.name foo" should match "git config" (arity 3) → 3 tokens
        var tokens = new[] { "git", "config", "set", "user.name", "foo" };

        var prefix = BashArity.GetPrefix(tokens);

        prefix.Length.ShouldBe(3);
        prefix[0].ShouldBe("git");
        prefix[1].ShouldBe("config");
        prefix[2].ShouldBe("set");
    }

    [Fact]
    public void WindowsCommandsRecognized()
    {
        // Windows-specific commands should be in the arity table
        BashArity.GetPrefix(["dir", "/b"]).ShouldHaveSingleItem();
        BashArity.GetPrefix(["copy", "a.txt", "b.txt"]).ShouldHaveSingleItem();
        BashArity.GetPrefix(["del", "file.txt"]).ShouldHaveSingleItem();
        BashArity.GetPrefix(["choco", "install", "git"]).Length.ShouldBe(2);
        BashArity.GetPrefix(["winget", "install", "code"]).Length.ShouldBe(2);
    }
}
