using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

public sealed class ModRoutingTests
{
    private const string Session = "ses_test1";
    private const string Other = "ses_test2";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static bool M(string matcher, string e) => ModRouting.MayMatch(J(matcher), J(e));

    private static ModHookSpec H(string @event, string? matcher = null) => new(@event, matcher is null ? null : J(matcher));

    private static ModRoute Kept(string name, params ModHookSpec[] hooks) => new($"{name}@v1", name, null, hooks);

    private static ModRoute Draft(string name, string session, params ModHookSpec[] hooks) => new($"{name}@draft:{session}", name, session, hooks);

    private static readonly JsonElement Row = J("""{ "component": "ToolUse", "requestId": "r1", "props": { "tool": "bash" } }""");

    private const string ChipsMatcher = """{ "component": ["ToolUse", "ToolResult"], "props": { "tool": ["bash", "shell"] } }""";

    [Fact]
    public void TestChipsMatcherPassesABashRowAndFailsAReadRow()
    {
        M(ChipsMatcher, """{ "component": "ToolUse", "requestId": "r1", "props": { "tool": "bash", "input": { "command": "dotnet test" } } }""").ShouldBeTrue();
        M(ChipsMatcher, """{ "component": "ToolResult", "requestId": "r2", "props": { "tool": "shell", "output": "Passed: 3" } }""").ShouldBeTrue();
        M(ChipsMatcher, """{ "component": "ToolUse", "requestId": "r3", "props": { "tool": "read" } }""").ShouldBeFalse();
    }

    [Fact]
    public void NoMatcherPassesAndAbsentFieldsFail()
    {
        ModRouting.MayMatch(null, Row).ShouldBeTrue();
        ModRouting.MayMatch(J("null"), Row).ShouldBeTrue();
        M("""{ "missing": 1 }""", """{ "a": 1 }""").ShouldBeFalse();
        M("""{ "a": null }""", """{ "b": 1 }""").ShouldBeFalse();
        M("""{ "a": null }""", """{ "a": null }""").ShouldBeTrue();
    }

    [Fact]
    public void LiteralsCompareByKindAndValue()
    {
        M("""{ "a": "x" }""", """{ "a": "X" }""").ShouldBeFalse();
        M("""{ "a": 1 }""", """{ "a": 1.0 }""").ShouldBeTrue();
        M("""{ "a": 1 }""", """{ "a": "1" }""").ShouldBeFalse();
        M("""{ "a": true }""", """{ "a": false }""").ShouldBeFalse();
        M("""{ "a": false }""", """{ "a": false }""").ShouldBeTrue();
    }

    [Theory]
    [InlineData("\\\\bcaf\\u00e9\\\\b", "caf\\u00e9 au lait")]
    [InlineData("\\\\u{1F600}", "smile")]
    [InlineData("(a)\\\\1", "zzz")]
    [InlineData("a)b[", "zzz")]
    public void ARegexPassesForAnyString(string source, string value)
        => M($$"""{ "t": { "$regex": "{{source}}", "flags": "u" } }""", $$"""{ "t": "{{value}}" }""").ShouldBeTrue();

    [Fact]
    public void ARegexFailsForANonStringOrAnAbsentField()
    {
        M("""{ "n": { "$regex": "1" } }""", """{ "n": 1 }""").ShouldBeFalse();
        M("""{ "n": { "$regex": ".*" } }""", """{ "n": null }""").ShouldBeFalse();
        M("""{ "n": { "$regex": ".*" } }""", "{}").ShouldBeFalse();
    }

    [Fact]
    public void KeptModsAreOrderedByNameThenDraftsByName()
    {
        var loaded = new[] { Draft("b-draft", Session, H("ui.render")), Kept("zeta", H("ui.render")), Kept("Mid", H("ui.render")), Draft("a-draft", Session, H("ui.render")), Kept("alpha", H("ui.render")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Row)
            .ShouldBe(["Mid@v1", "alpha@v1", "zeta@v1", $"a-draft@draft:{Session}", $"b-draft@draft:{Session}"]);
    }

    [Fact]
    public void ADraftReplacesItsKeptModInItsSessionOnlyAndEvenWithoutAHookForTheEvent()
    {
        var loaded = new[] { Kept("test-chips", H("ui.render")), Draft("test-chips", Session, H("turn.complete")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Row).ShouldBeEmpty();
        ModRouting.ChainFor(loaded, "ui.render", Other, Row).ShouldBe(["test-chips@v1"]);
    }

    [Fact]
    public void AnotherSessionsDraftIsNeverInTheChain()
    {
        var loaded = new[] { Kept("demo-mod", H("ui.render")), Draft("other-draft", Other, H("ui.render")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Row).ShouldBe(["demo-mod@v1"]);
        ModRouting.ChainFor(loaded, "ui.render", Other, Row).ShouldBe(["demo-mod@v1", $"other-draft@draft:{Other}"]);
    }

    [Fact]
    public void OnlyModsWithAMatchingHookForTheEventAreIncluded()
    {
        var loaded = new[]
        {
            Kept("a", H("turn.complete")),
            Kept("b", H("ui.render", """{ "component": "ComposerBand" }"""), H("ui.render", """{ "component": "ToolUse" }""")),
            Kept("c", H("ui.render", """{ "component": "ComposerBand" }""")),
            Kept("d"),
        };
        ModRouting.ChainFor(loaded, "ui.render", Session, Row).ShouldBe(["b@v1"]);
    }

    [Theory]
    [InlineData("ui.press")]
    [InlineData("ui.input")]
    [InlineData("ui.select")]
    public void ControlEventsPassAnyHookForThem(string @event)
    {
        var loaded = new[] { Kept("a", H(@event, """{ "mod": "a", "element": "go" }""")), Kept("b", H("ui.render")) };
        ModRouting.ChainFor(loaded, @event, Session, J("""{ "handle": "h1" }""")).ShouldBe(["a@v1"]);
    }

    [Fact]
    public void TurnCompleteRoutesByItsFields()
    {
        var mod = Kept("a", H("turn.complete", """{ "isFailed": true }"""));
        ModRouting.ChainFor([mod], "turn.complete", Session, J("""{ "isFailed": true }""")).ShouldBe(["a@v1"]);
        ModRouting.ChainFor([mod], "turn.complete", Session, J("""{ "isFailed": false }""")).ShouldBeEmpty();
    }
}
