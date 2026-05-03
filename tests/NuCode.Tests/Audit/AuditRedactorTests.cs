using NuCode.Audit;

namespace NuCode;

public sealed class AuditRedactorTests
{
    [Fact]
    public static void empty_args_returns_empty_string()
    {
        var result = AuditRedactor.Summarize("read", new Dictionary<string, object?>());
        result.ShouldBeEmpty();
    }

    [Fact]
    public static void non_sensitive_args_are_included()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/tmp/foo.txt" };
        var result = AuditRedactor.Summarize("read", args);
        result.ShouldBe("path=/tmp/foo.txt");
    }

    [Fact]
    public static void sensitive_keys_are_redacted()
    {
        foreach (var key in new[] { "content", "text", "body", "secret", "token", "password", "key" })
        {
            var args = new Dictionary<string, object?> { [key] = "super-secret-value" };
            var result = AuditRedactor.Summarize("write", args);
            result.ShouldContain("[redacted]");
            result.ShouldNotContain("super-secret-value");
        }
    }

    [Fact]
    public static void sensitive_keys_are_case_insensitive()
    {
        var args = new Dictionary<string, object?> { ["Content"] = "secret", ["TOKEN"] = "abc" };
        var result = AuditRedactor.Summarize("write", args);
        result.ShouldContain("[redacted]");
        result.ShouldNotContain("secret");
        result.ShouldNotContain("abc");
    }

    [Fact]
    public static void long_value_is_truncated_to_200_chars()
    {
        var longValue = new string('x', 300);
        var args = new Dictionary<string, object?> { ["path"] = longValue };
        var result = AuditRedactor.Summarize("read", args);
        result.Length.ShouldBe(200);
        result.ShouldEndWith("...");
    }

    [Fact]
    public static void bash_tool_includes_only_command()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = "ls -la",
            ["stdin"] = "some input",
        };
        var result = AuditRedactor.Summarize("bash", args);
        result.ShouldBe("command=ls -la");
        result.ShouldNotContain("stdin");
    }

    [Fact]
    public static void bash_tool_case_insensitive()
    {
        var args = new Dictionary<string, object?> { ["command"] = "echo hello" };
        var result = AuditRedactor.Summarize("Bash", args);
        result.ShouldBe("command=echo hello");
    }

    [Fact]
    public static void multiple_args_joined_with_comma()
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = "/foo",
            ["mode"] = "r",
        };
        var result = AuditRedactor.Summarize("read", args);
        result.ShouldContain("path=/foo");
        result.ShouldContain("mode=r");
        result.ShouldContain(", ");
    }

    [Fact]
    public static void null_value_renders_as_null_string()
    {
        var args = new Dictionary<string, object?> { ["offset"] = null };
        var result = AuditRedactor.Summarize("read", args);
        result.ShouldBe("offset=null");
    }
}
