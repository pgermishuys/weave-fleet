using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

public sealed class ModRoutingTests
{
    private const string Session = "ses_test1";
    private const string Other = "ses_test2";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static bool M(string matcher, string e) => ModRouting.Matches(J(matcher), J(e));

    private static string Rx(string source, string? flags = null)
        => flags is null
            ? $$"""{ "$regex": {{JsonSerializer.Serialize(source)}} }"""
            : $$"""{ "$regex": {{JsonSerializer.Serialize(source)}}, "flags": "{{flags}}" }""";

    // ---- Matches ----

    [Fact]
    public void NoMatcherMatchesEverything()
    {
        ModRouting.Matches(null, J("""{ "a": 1 }""")).ShouldBeTrue();
        ModRouting.Matches(null, J("5")).ShouldBeTrue();
        ModRouting.Matches(J("null"), J("""{ "a": 1 }""")).ShouldBeTrue();
        ModRouting.Matches(default(JsonElement), J("""{ "a": 1 }""")).ShouldBeTrue();
    }

    [Fact]
    public void EmptyMatcherMatchesAnyObject() => M("{}", """{ "a": 1 }""").ShouldBeTrue();

    [Fact]
    public void MatcherOnNonObjectEventDoesNotMatch()
    {
        M("""{ "a": 1 }""", "5").ShouldBeFalse();
        M("""{ "a": 1 }""", "[1]").ShouldBeFalse();
        M("""{ "a": 1 }""", "null").ShouldBeFalse();
        M("{}", "\"x\"").ShouldBeFalse();
    }

    [Fact]
    public void AbsentKeyFails() => M("""{ "a": 1 }""", """{ "b": 1 }""").ShouldBeFalse();

    [Fact]
    public void NullIsNotAbsent()
    {
        M("""{ "a": null }""", """{ "a": null }""").ShouldBeTrue();
        M("""{ "a": null }""", """{ "b": 1 }""").ShouldBeFalse();
        M("""{ "a": null }""", """{ "a": 0 }""").ShouldBeFalse();
    }

    [Fact]
    public void EveryFieldMustMatch()
    {
        M("""{ "a": 1, "b": "x" }""", """{ "a": 1, "b": "x", "c": true }""").ShouldBeTrue();
        M("""{ "a": 1, "b": "x" }""", """{ "a": 1, "b": "y" }""").ShouldBeFalse();
    }

    [Fact]
    public void NumbersCompareByValue()
    {
        M("""{ "a": 1 }""", """{ "a": 1.0 }""").ShouldBeTrue();
        M("""{ "a": 1.0 }""", """{ "a": 1 }""").ShouldBeTrue();
        M("""{ "a": 1 }""", """{ "a": 2 }""").ShouldBeFalse();
    }

    [Fact]
    public void KindsNeverCompareEqual()
    {
        M("""{ "a": "1" }""", """{ "a": 1 }""").ShouldBeFalse();
        M("""{ "a": 1 }""", """{ "a": "1" }""").ShouldBeFalse();
        M("""{ "a": true }""", """{ "a": 1 }""").ShouldBeFalse();
        M("""{ "a": false }""", """{ "a": null }""").ShouldBeFalse();
    }

    [Fact]
    public void BooleansMatchByValue()
    {
        M("""{ "isFailed": true }""", """{ "isFailed": true }""").ShouldBeTrue();
        M("""{ "isFailed": true }""", """{ "isFailed": false }""").ShouldBeFalse();
        M("""{ "isFailed": false }""", """{ "isFailed": false }""").ShouldBeTrue();
    }

    [Fact]
    public void StringsCompareOrdinally()
    {
        M("""{ "a": "bash" }""", """{ "a": "bash" }""").ShouldBeTrue();
        M("""{ "a": "bash" }""", """{ "a": "Bash" }""").ShouldBeFalse();
    }

    [Fact]
    public void ArrayMatchesWhenAnyElementDoes()
    {
        M("""{ "tool": ["bash", "shell"] }""", """{ "tool": "shell" }""").ShouldBeTrue();
        M("""{ "tool": ["bash", "shell"] }""", """{ "tool": "read" }""").ShouldBeFalse();
        M("""{ "tool": [] }""", """{ "tool": "read" }""").ShouldBeFalse();
    }

    [Fact]
    public void ArrayOfRegexes()
    {
        var m = $$"""{ "tool": [{{Rx("^ba")}}, {{Rx("^sh")}}] }""";
        M(m, """{ "tool": "shell" }""").ShouldBeTrue();
        M(m, """{ "tool": "read" }""").ShouldBeFalse();
    }

    [Fact]
    public void ArrayElementsCanBeObjects()
    {
        M("""{ "p": [{ "a": 1 }, { "a": 2 }] }""", """{ "p": { "a": 2, "z": 0 } }""").ShouldBeTrue();
        M("""{ "p": [{ "a": 1 }, { "a": 2 }] }""", """{ "p": { "a": 3 } }""").ShouldBeFalse();
    }

    [Fact]
    public void RegexMatchesAnywhereInTheString()
    {
        M($$"""{ "cmd": {{Rx("test")}} }""", """{ "cmd": "dotnet test src" }""").ShouldBeTrue();
        M($$"""{ "cmd": {{Rx("^test")}} }""", """{ "cmd": "dotnet test src" }""").ShouldBeFalse();
    }

    [Fact]
    public void RegexIsCaseSensitiveUnlessIFlag()
    {
        M($$"""{ "cmd": {{Rx("TEST")}} }""", """{ "cmd": "dotnet test" }""").ShouldBeFalse();
        M($$"""{ "cmd": {{Rx("TEST", "i")}} }""", """{ "cmd": "dotnet test" }""").ShouldBeTrue();
    }

    [Fact]
    public void RegexMultilineFlag()
    {
        const string e = """{ "t": "one\ntwo" }""";
        M($$"""{ "t": {{Rx("^two$")}} }""", e).ShouldBeFalse();
        M($$"""{ "t": {{Rx("^two$", "m")}} }""", e).ShouldBeTrue();
    }

    [Fact]
    public void RegexDotAllFlag()
    {
        const string e = """{ "t": "one\ntwo" }""";
        M($$"""{ "t": {{Rx("one.two")}} }""", e).ShouldBeFalse();
        M($$"""{ "t": {{Rx("one.two", "s")}} }""", e).ShouldBeTrue();
    }

    [Fact]
    public void RegexIgnoresGlobalStickyAndOtherFlags()
    {
        M($$"""{ "t": {{Rx("b", "gyd")}} }""", """{ "t": "abc" }""").ShouldBeTrue();
        M($$"""{ "t": {{Rx("x", "gyd")}} }""", """{ "t": "abc" }""").ShouldBeFalse();
        M($$"""{ "t": {{Rx("b", "u")}} }""", """{ "t": "abc" }""").ShouldBeTrue();
    }

    [Fact]
    public void RegexOnNonStringDoesNotMatch()
    {
        M($$"""{ "n": {{Rx("1")}} }""", """{ "n": 1 }""").ShouldBeFalse();
        M($$"""{ "n": {{Rx(".*")}} }""", """{ "n": null }""").ShouldBeFalse();
        M($$"""{ "n": {{Rx(".*")}} }""", """{ "n": ["a"] }""").ShouldBeFalse();
    }

    [Fact]
    public void RegexOnAbsentKeyDoesNotMatch() => M($$"""{ "n": {{Rx(".*")}} }""", "{}").ShouldBeFalse();

    [Fact]
    public void PatternDotNetCannotCompileCountsAsAMatch()
    {
        // Being inclusive is safe: the host applies its own matcher again.
        M($$"""{ "t": {{Rx("a)b")}} }""", """{ "t": "zzz" }""").ShouldBeTrue();
        M($$"""{ "t": {{Rx("[")}} }""", """{ "t": "zzz" }""").ShouldBeTrue();
    }

    [Fact]
    public void PatternThatTimesOutCountsAsAMatch()
    {
        var evil = new string('a', 40) + "!";
        var sw = Stopwatch.StartNew();
        M($$"""{ "t": {{Rx("^(a+)+$")}} }""", $$"""{ "t": "{{evil}}" }""").ShouldBeTrue();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ManyDistinctRegexesStayCorrect()
    {
        for (var i = 0; i < 600; i++)
        {
            M($$"""{ "t": {{Rx($"^v{i}$")}} }""", $$"""{ "t": "v{{i}}" }""").ShouldBeTrue();
            M($$"""{ "t": {{Rx($"^v{i}$")}} }""", """{ "t": "nope" }""").ShouldBeFalse();
        }
    }

    [Fact]
    public void NestedObjectsMatchRecursively()
    {
        const string matcher = """{ "component": "ToolUse", "props": { "tool": ["bash", "shell"] } }""";
        const string toolUse = """{ "component": "ToolUse", "requestId": "r1", "props": { "tool": "bash", "input": { "command": "dotnet test" }, "status": "completed" } }""";
        M(matcher, toolUse).ShouldBeTrue();
        M(matcher, toolUse.Replace("\"bash\"", "\"read\"")).ShouldBeFalse();
        M(matcher, toolUse.Replace("ToolUse", "ToolResult")).ShouldBeFalse();
    }

    [Fact]
    public void NestedMatcherNeedsAnObjectValue()
    {
        M("""{ "props": { "tool": "bash" } }""", """{ "props": "bash" }""").ShouldBeFalse();
        M("""{ "props": { "tool": "bash" } }""", """{ "props": [ { "tool": "bash" } ] }""").ShouldBeFalse();
        M("""{ "props": { "tool": "bash" } }""", """{ "props": null }""").ShouldBeFalse();
        M("""{ "props": { "tool": "bash" } }""", """{ "props": {} }""").ShouldBeFalse();
    }

    [Fact]
    public void NestedRegex()
    {
        M($$"""{ "props": { "input": { "command": {{Rx("^dotnet test")}} } } }""",
            """{ "props": { "input": { "command": "dotnet test x.sln" } } }""").ShouldBeTrue();
    }

    [Fact]
    public void TestChipsMatcherAgainstRows()
    {
        const string matcher = """{ "component": ["ToolUse", "ToolResult"], "props": { "tool": ["bash", "shell"] } }""";
        M(matcher, """{ "component": "ToolUse", "requestId": "r1", "props": { "tool": "bash", "input": { "command": "dotnet test" } } }""").ShouldBeTrue();
        M(matcher, """{ "component": "ToolResult", "requestId": "r2", "props": { "tool": "bash", "input": { "command": "dotnet test" }, "output": "Passed: 3" } }""").ShouldBeTrue();
        M(matcher, """{ "component": "ToolUse", "requestId": "r3", "props": { "tool": "read", "input": { "filePath": "a.cs" } } }""").ShouldBeFalse();
    }

    [Fact]
    public void ComposerBandMatcher()
    {
        M("""{ "component": "ComposerBand" }""", """{ "component": "ComposerBand", "requestId": "r1", "props": {} }""").ShouldBeTrue();
        M("""{ "component": "ComposerBand" }""", """{ "component": "ToolUse", "requestId": "r1", "props": {} }""").ShouldBeFalse();
    }

    // ---- ChainFor ----

    private static ModHookSpec H(string @event, string? matcher = null)
        => new(@event, matcher is null ? null : J(matcher));

    private static ModRoute Kept(string name, params ModHookSpec[] hooks) => new($"{name}@v1", name, null, hooks);

    private static ModRoute Draft(string name, string session, params ModHookSpec[] hooks)
        => new($"{name}@draft:{session}", name, session, hooks);

    private static readonly JsonElement Render = J("""{ "component": "ToolUse", "requestId": "r1", "props": { "tool": "bash" } }""");

    [Fact]
    public void KeptModsAreOrderedByNameOrdinal()
    {
        var chain = ModRouting.ChainFor(
            [Kept("zeta", H("ui.render")), Kept("alpha", H("ui.render")), Kept("Mid", H("ui.render")), Kept("beta", H("ui.render"))],
            "ui.render", Session, Render);
        chain.ShouldBe(["Mid@v1", "alpha@v1", "beta@v1", "zeta@v1"]);
    }

    [Fact]
    public void DraftsComeAfterKeptModsByName()
    {
        var chain = ModRouting.ChainFor(
            [Draft("b-draft", Session, H("ui.render")), Kept("zeta", H("ui.render")), Draft("a-draft", Session, H("ui.render")), Kept("alpha", H("ui.render"))],
            "ui.render", Session, Render);
        chain.ShouldBe(["alpha@v1", "zeta@v1", $"a-draft@draft:{Session}", $"b-draft@draft:{Session}"]);
    }

    [Fact]
    public void ADraftReplacesTheKeptModWithItsNameInItsSession()
    {
        var loaded = new[] { Kept("test-chips", H("ui.render")), Kept("demo-mod", H("ui.render")), Draft("test-chips", Session, H("ui.render")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Render)
            .ShouldBe(["demo-mod@v1", $"test-chips@draft:{Session}"]);
    }

    [Fact]
    public void AnotherSessionsDraftIsNeverInTheChain()
    {
        var loaded = new[] { Kept("demo-mod", H("ui.render")), Draft("other-draft", Other, H("ui.render")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Render).ShouldBe(["demo-mod@v1"]);
    }

    [Fact]
    public void AnotherSessionsDraftWithAKeptModsNameLeavesTheKeptModInThisSession()
    {
        var loaded = new[] { Kept("test-chips", H("ui.render")), Draft("test-chips", Other, H("ui.render")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Render).ShouldBe(["test-chips@v1"]);
        ModRouting.ChainFor(loaded, "ui.render", Other, Render).ShouldBe([$"test-chips@draft:{Other}"]);
    }

    [Fact]
    public void ADraftWithoutAHookForTheEventDropsTheKeptModToo()
    {
        var loaded = new[] { Kept("test-chips", H("ui.render")), Draft("test-chips", Session, H("turn.complete")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Render).ShouldBeEmpty();
        ModRouting.ChainFor(loaded, "turn.complete", Session, J("{}")).ShouldBe([$"test-chips@draft:{Session}"]);
    }

    [Fact]
    public void ADraftWhoseMatcherFailsAlsoDropsTheKeptMod()
    {
        var loaded = new[] { Kept("test-chips", H("ui.render")), Draft("test-chips", Session, H("ui.render", """{ "component": "ComposerBand" }""")) };
        ModRouting.ChainFor(loaded, "ui.render", Session, Render).ShouldBeEmpty();
    }

    [Fact]
    public void InputOrderDoesNotMatter()
    {
        var a = new[] { Kept("a", H("ui.render")), Kept("b", H("ui.render")), Draft("c", Session, H("ui.render")) };
        var b = a.Reverse().ToArray();
        ModRouting.ChainFor(b, "ui.render", Session, Render).ShouldBe(ModRouting.ChainFor(a, "ui.render", Session, Render));
    }

    [Fact]
    public void ModsWithHooksOnlyForOtherEventsAreExcluded()
    {
        var loaded = new[] { Kept("a", H("turn.complete")), Kept("b", H("ui.render")), Kept("c") };
        ModRouting.ChainFor(loaded, "ui.render", Session, Render).ShouldBe(["b@v1"]);
    }

    [Fact]
    public void AMatchingSecondHookIsEnough()
    {
        var mod = Kept("a", H("ui.render", """{ "component": "ComposerBand" }"""), H("ui.render", """{ "component": "ToolUse" }"""));
        ModRouting.ChainFor([mod], "ui.render", Session, Render).ShouldBe(["a@v1"]);
    }

    [Fact]
    public void AModWhoseMatchersAllFailIsExcluded()
    {
        var mod = Kept("a", H("ui.render", """{ "component": "ComposerBand" }"""), H("ui.render", """{ "component": "Nope" }"""));
        ModRouting.ChainFor([mod], "ui.render", Session, Render).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ui.press")]
    [InlineData("ui.input")]
    [InlineData("ui.select")]
    public void ControlEventsIgnoreMatchers(string @event)
    {
        var mod = Kept("a", H(@event, """{ "mod": "a", "element": "go" }"""));
        // Fleet's e carries a handle, not yet mod and element, so the matcher can't be judged here.
        ModRouting.ChainFor([mod], @event, Session, J("""{ "handle": "h1" }""")).ShouldBe(["a@v1"]);
        ModRouting.ChainFor([Kept("b", H("ui.render"))], @event, Session, J("""{ "handle": "h1" }""")).ShouldBeEmpty();
    }

    [Fact]
    public void SessionStartAndTurnCompleteRouteByEventAndFieldMatchers()
    {
        var mod = Kept("a", H("session.start", """{ "reason": "reload" }"""), H("turn.complete", """{ "isFailed": true }"""));
        ModRouting.ChainFor([mod], "session.start", Session, J("""{ "sessionId": "ses_test1", "reason": "reload" }""")).ShouldBe(["a@v1"]);
        ModRouting.ChainFor([mod], "session.start", Session, J("""{ "sessionId": "ses_test1", "reason": "start" }""")).ShouldBeEmpty();
        ModRouting.ChainFor([mod], "turn.complete", Session, J("""{ "isFailed": true }""")).ShouldBe(["a@v1"]);
        ModRouting.ChainFor([mod], "turn.complete", Session, J("""{ "isFailed": false }""")).ShouldBeEmpty();
    }

    [Fact]
    public void NoLoadedModsGivesAnEmptyChain() => ModRouting.ChainFor([], "ui.render", Session, Render).ShouldBeEmpty();
}
