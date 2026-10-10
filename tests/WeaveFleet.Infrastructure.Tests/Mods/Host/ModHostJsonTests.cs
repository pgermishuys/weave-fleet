using System.Text.Json;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>The bytes Fleet writes and reads, against the shapes the host (mods/host) writes and reads.</summary>
public sealed class ModHostJsonTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_kept_load_is_written_with_a_number_version_and_no_session()
    {
        var json = JsonSerializer.Serialize(ModLoadParams.Kept("test-chips", 3, "/data/mods/test-chips/v3"), ModHostJsonContext.Default.ModLoadParams);

        json.ShouldBe("""{"id":"test-chips@v3","name":"test-chips","version":3,"root":"/data/mods/test-chips/v3"}""");
    }

    [Fact]
    public void A_draft_load_is_written_with_the_string_draft_and_its_session()
    {
        var json = JsonSerializer.Serialize(ModLoadParams.Draft("test-chips", "ses_test1", "/drafts/ses_test1/test-chips"), ModHostJsonContext.Default.ModLoadParams);

        json.ShouldBe("""{"id":"test-chips@draft:ses_test1","name":"test-chips","version":"draft","sessionId":"ses_test1","root":"/drafts/ses_test1/test-chips"}""");
    }

    [Fact]
    public void A_dispatch_is_written_camel_case_and_leaves_out_a_missing_surface()
    {
        var dispatch = new ModWireDispatch("ui.render", "ses_test1", Json("""{"site":"status"}"""), ["test-chips@v3", "demo-mod@draft:ses_test1"], null);

        JsonSerializer.Serialize(dispatch, ModHostJsonContext.Default.ModWireDispatch)
            .ShouldBe("""{"event":"ui.render","sessionId":"ses_test1","e":{"site":"status"},"mods":["test-chips@v3","demo-mod@draft:ses_test1"]}""");

        JsonSerializer.Serialize(dispatch with { Surface = "phone" }, ModHostJsonContext.Default.ModWireDispatch)
            .ShouldEndWith(""","surface":"phone"}""");
    }

    [Fact]
    public void The_small_requests_are_written_as_the_host_reads_them()
    {
        JsonSerializer.Serialize(new InitializeParams(1, "0.40.0"), ModHostJsonContext.Default.InitializeParams)
            .ShouldBe("""{"protocol":1,"fleetVersion":"0.40.0"}""");
        JsonSerializer.Serialize(new CheckParams("/stage/test-chips", "/stage/test-chips/mod.json"), ModHostJsonContext.Default.CheckParams)
            .ShouldBe("""{"root":"/stage/test-chips","manifest":"/stage/test-chips/mod.json"}""");
        JsonSerializer.Serialize(new UnloadParams("test-chips@v3"), ModHostJsonContext.Default.UnloadParams)
            .ShouldBe("""{"id":"test-chips@v3"}""");
        JsonSerializer.Serialize(new ForgetParams("ses_test1"), ModHostJsonContext.Default.ForgetParams)
            .ShouldBe("""{"sessionId":"ses_test1"}""");
        JsonSerializer.Serialize(new EmptyParams(), ModHostJsonContext.Default.EmptyParams).ShouldBe("{}");
    }

    [Fact]
    public void A_dispatch_answer_with_failures_and_drawn_by_is_read()
    {
        const string sample = """
            {"result":{"type":"Row","children":[]},"drawnBy":["test-chips@v3"],
             "failures":[{"mod":"demo-mod@v1","event":"ui.render","kind":"timeout","message":"took too long","strikes":2},
                         {"mod":"test-chips@draft:ses_test1","event":"turn.complete","kind":"throw","message":"boom","strikes":1,"sessionId":"ses_test1"}]}
            """;

        var result = JsonSerializer.Deserialize(sample, ModHostJsonContext.Default.ModWireDispatchResult).ShouldNotBeNull();

        result.Result.GetProperty("type").GetString().ShouldBe("Row");
        result.DrawnBy.ShouldBe(["test-chips@v3"]);
        result.Failures.Count.ShouldBe(2);
        result.Failures[0].ShouldBe(new ModHookFailure("demo-mod@v1", "ui.render", "timeout", "took too long", 2));
        result.Failures[1].SessionId.ShouldBe("ses_test1");
    }

    [Fact]
    public void A_dispatch_answer_without_drawn_by_is_read()
    {
        var result = JsonSerializer.Deserialize("""{"result":null,"failures":[]}""", ModHostJsonContext.Default.ModWireDispatchResult).ShouldNotBeNull();

        result.DrawnBy.ShouldBeNull();
        result.Result.ValueKind.ShouldBe(JsonValueKind.Null);
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void A_load_answer_keeps_the_report_and_reads_hooks_with_and_without_matchers()
    {
        const string sample = """
            {"check":{"ok":true,"name":"test-chips","errors":[],"warnings":[]},
             "hooks":[{"event":"ui.render"},
                      {"event":"turn.complete","matcher":{"$regex":"^b","flags":"i"}},
                      {"event":"session.start","matcher":"main"}]}
            """;

        var result = JsonSerializer.Deserialize(sample, ModHostJsonContext.Default.ModLoadResult).ShouldNotBeNull();

        result.Check.GetProperty("name").GetString().ShouldBe("test-chips");
        result.Hooks.Count.ShouldBe(3);
        result.Hooks[0].Matcher.ShouldBeNull();
        result.Hooks[1].Matcher.ShouldNotBeNull().GetProperty("$regex").GetString().ShouldBe("^b");
        result.Hooks[1].Matcher!.Value.GetProperty("flags").GetString().ShouldBe("i");
        result.Hooks[2].Matcher.ShouldNotBeNull().GetString().ShouldBe("main");
    }

    [Fact]
    public void A_hook_without_a_matcher_is_written_without_the_field()
    {
        JsonSerializer.Serialize(new ModHookSpec("ui.render", null), ModHostJsonContext.Default.ModHookSpec)
            .ShouldBe("""{"event":"ui.render"}""");
    }

    [Fact]
    public void An_initialize_answer_is_read()
    {
        var result = JsonSerializer.Deserialize("""{"protocol":1,"hostVersion":"1.0.0","bunVersion":"1.3.0"}""", ModHostJsonContext.Default.ModHostInitializeResult);

        result.ShouldBe(new ModHostInitializeResult(1, "1.0.0", "1.3.0"));
    }
}
