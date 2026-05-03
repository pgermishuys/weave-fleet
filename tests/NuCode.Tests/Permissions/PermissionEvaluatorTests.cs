using System.Runtime.InteropServices;
using NuCode.Permissions;

namespace NuCode;

public sealed class PermissionEvaluatorTests
{
    [Fact]
    public void NoMatchingRulesReturnsAsk()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "empty",
        };

        var result = PermissionEvaluator.Evaluate("bash", "git commit", ruleset);

        result.ShouldBe(PermissionAction.Ask);
    }

    [Fact]
    public void ExactMatchReturnsAction()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("bash", "git commit", PermissionAction.Allow)],
        };

        var result = PermissionEvaluator.Evaluate("bash", "git commit", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void WildcardStarMatchesAnything()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("bash", "*", PermissionAction.Allow)],
        };

        var result = PermissionEvaluator.Evaluate("bash", "anything here", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void WildcardPermissionMatchesAnyPermission()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("*", "*", PermissionAction.Allow)],
        };

        var result = PermissionEvaluator.Evaluate("edit", "/some/file.cs", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void LastMatchWins()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules =
            [
                new PermissionRule("bash", "*", PermissionAction.Allow),
                new PermissionRule("bash", "rm *", PermissionAction.Deny),
            ],
        };

        // "rm -rf /" should match both rules; last one (Deny) wins
        var result = PermissionEvaluator.Evaluate("bash", "rm -rf /", ruleset);

        result.ShouldBe(PermissionAction.Deny);
    }

    [Fact]
    public void LastMatchWinsAllowOverridesDeny()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules =
            [
                new PermissionRule("bash", "*", PermissionAction.Deny),
                new PermissionRule("bash", "git *", PermissionAction.Allow),
            ],
        };

        var result = PermissionEvaluator.Evaluate("bash", "git status", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void MultipleRulesetsAreFlattenedInOrder()
    {
        var defaults = new PermissionRuleset
        {
            Name = "default",
            Rules = [new PermissionRule("bash", "*", PermissionAction.Deny)],
        };
        var overrides = new PermissionRuleset
        {
            Name = "override",
            Rules = [new PermissionRule("bash", "git *", PermissionAction.Allow)],
        };

        var result = PermissionEvaluator.Evaluate("bash", "git push", defaults, overrides);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void EarlierRulesetWinsWhenLaterHasNoMatch()
    {
        var first = new PermissionRuleset
        {
            Name = "first",
            Rules = [new PermissionRule("bash", "rm *", PermissionAction.Deny)],
        };
        var second = new PermissionRuleset
        {
            Name = "second",
            Rules = [new PermissionRule("edit", "*", PermissionAction.Allow)],
        };

        // "bash" + "rm -rf" matches first ruleset's rule, second has no bash rules
        var result = PermissionEvaluator.Evaluate("bash", "rm -rf", first, second);

        result.ShouldBe(PermissionAction.Deny);
    }

    [Fact]
    public void SpaceWildcardMatchesBothWithAndWithoutArgs()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("bash", "ls *", PermissionAction.Allow)],
        };

        // "ls *" pattern should match "ls" (no args) and "ls -la" (with args)
        PermissionEvaluator.Evaluate("bash", "ls", ruleset).ShouldBe(PermissionAction.Allow);
        PermissionEvaluator.Evaluate("bash", "ls -la", ruleset).ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void QuestionMarkMatchesSingleCharacter()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("edit", "?.cs", PermissionAction.Allow)],
        };

        PermissionEvaluator.Evaluate("edit", "a.cs", ruleset).ShouldBe(PermissionAction.Allow);
        PermissionEvaluator.Evaluate("edit", "ab.cs", ruleset).ShouldBe(PermissionAction.Ask);
    }

    [Fact]
    public void PathSeparatorsAreNormalized()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("edit", "src/NuCode/*", PermissionAction.Allow)],
        };

        // Backslash input should match forward-slash pattern
        var result = PermissionEvaluator.Evaluate("edit", "src\\NuCode\\File.cs", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void WildcardPermissionPatternMatchesSubstring()
    {
        // e.g., "mcp_*" should match "mcp_server_tool"
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("mcp_*", "*", PermissionAction.Allow)],
        };

        var result = PermissionEvaluator.Evaluate("mcp_server_tool", "some_pattern", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }

    [Fact]
    public void SpecialRegexCharsInPatternAreEscaped()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("edit", "file.txt", PermissionAction.Allow)],
        };

        // "file.txt" should NOT match "fileXtxt" (dot is escaped, not regex wildcard)
        PermissionEvaluator.Evaluate("edit", "file.txt", ruleset).ShouldBe(PermissionAction.Allow);
        PermissionEvaluator.Evaluate("edit", "fileXtxt", ruleset).ShouldBe(PermissionAction.Ask);
    }

    [Fact]
    public void EmptyRulesetsReturnAsk()
    {
        var result = PermissionEvaluator.Evaluate("bash", "anything");

        result.ShouldBe(PermissionAction.Ask);
    }

    [Fact]
    public void ComplexMultiRulesetScenario()
    {
        // Simulates: default = ask everything, config = allow git/ls, deny rm, session = allow rm for this dir
        var defaults = new PermissionRuleset
        {
            Name = "default",
            Rules = [new PermissionRule("bash", "*", PermissionAction.Ask)],
        };
        var config = new PermissionRuleset
        {
            Name = "config",
            Rules =
            [
                new PermissionRule("bash", "git *", PermissionAction.Allow),
                new PermissionRule("bash", "ls *", PermissionAction.Allow),
                new PermissionRule("bash", "rm *", PermissionAction.Deny),
            ],
        };
        var session = new PermissionRuleset
        {
            Name = "session",
            Rules = [new PermissionRule("bash", "rm *", PermissionAction.Allow)],
        };

        // git should be allowed (config rule)
        PermissionEvaluator.Evaluate("bash", "git status", defaults, config, session).ShouldBe(PermissionAction.Allow);
        // ls should be allowed (config rule)
        PermissionEvaluator.Evaluate("bash", "ls", defaults, config, session).ShouldBe(PermissionAction.Allow);
        // rm should be allowed (session overrides config deny — last match wins)
        PermissionEvaluator.Evaluate("bash", "rm temp.txt", defaults, config, session).ShouldBe(PermissionAction.Allow);
        // unknown command should be ask (only default matches)
        PermissionEvaluator.Evaluate("bash", "curl http://evil", defaults, config, session).ShouldBe(PermissionAction.Ask);
    }

    [Fact]
    public void NonMatchingPermissionTypeDoesNotMatch()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("bash", "*", PermissionAction.Allow)],
        };

        // "edit" permission should NOT match "bash" rule
        var result = PermissionEvaluator.Evaluate("edit", "somefile.cs", ruleset);

        result.ShouldBe(PermissionAction.Ask);
    }

    [Fact]
    public void WithRulesAppendsToRuleset()
    {
        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("bash", "git *", PermissionAction.Allow)],
        };

        var extended = ruleset.WithRules(new PermissionRule("bash", "rm *", PermissionAction.Deny));

        extended.Rules.Length.ShouldBe(2);
        extended.Rules[0].Pattern.ShouldBe("git *");
        extended.Rules[1].Pattern.ShouldBe("rm *");
        // Original is unchanged (immutable)
        ruleset.Rules.ShouldHaveSingleItem();
    }

    [Fact]
    public void CaseInsensitiveOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // This test is Windows-specific
        }

        var ruleset = new PermissionRuleset
        {
            Name = "test",
            Rules = [new PermissionRule("bash", "Git *", PermissionAction.Allow)],
        };

        var result = PermissionEvaluator.Evaluate("bash", "git status", ruleset);

        result.ShouldBe(PermissionAction.Allow);
    }
}
