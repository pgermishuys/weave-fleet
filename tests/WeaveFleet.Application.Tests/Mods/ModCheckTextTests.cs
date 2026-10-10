using System.Text.Json;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>The report as text, the same as the host's <c>formatReport</c> (<c>mods/host/test/check/report.test.ts</c>).</summary>
public sealed class ModCheckTextTests
{
    private static JsonElement Report(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Test_chips_reads_as_the_contracts_report()
    {
        var report = Report("""
            {"ok":true,"name":"test-chips","version":"0.1.0","description":"Draws test runs as passed, failed and skipped counts",
             "lines":47,"sha256":"ab","hooks":[{"event":"ui.render","matcher":{"component":["ToolUse","ToolResult"],"props":{"tool":["bash","shell"]}}}],
             "calls":["ui.resolve"],"state":[],"pages":[],"errors":[],"warnings":[]}
            """);

        ModCheckText.Format(report).ShouldBe(string.Join('\n',
            "test-chips 0.1.0 · 47 lines",
            """hooks:  ui.render ["ToolUse","ToolResult"] { props: { tool: ["bash","shell"] } }""",
            "calls:  ui.resolve",
            "state:  (none)",
            "pages:  (none)"));
    }

    [Fact]
    public void Each_hook_is_on_its_own_line_with_regexes_numbers_booleans_and_null()
    {
        var report = Report("""
            {"ok":true,"name":"kitchen-sink","version":"1.2.3","description":"d","lines":30,"sha256":"",
             "hooks":[{"event":"session.start"},{"event":"turn.complete"},
                      {"event":"ui.render","matcher":{"component":{"$regex":"^Tool","flags":"i"},"props":{"n":[1,-2.5],"ok":true,"none":null}}},
                      {"event":"ui.press"}],
             "calls":["clock.after","state.get"],"state":["count","seen"],"pages":["pages/demo.html"],"errors":[],"warnings":[]}
            """);

        ModCheckText.Format(report).Split('\n').ShouldBe(
        [
            "kitchen-sink 1.2.3 · 30 lines",
            "hooks:  session.start",
            "        turn.complete",
            "        ui.render /^Tool/i { props: { n: [1,-2.5], ok: true, none: null } }",
            "        ui.press",
            "calls:  clock.after, state.get",
            "state:  count, seen",
            "pages:  pages/demo.html",
        ]);
    }

    [Fact]
    public void Errors_and_warnings_follow_with_position_code_and_message()
    {
        var report = Report("""
            {"ok":false,"name":"m","version":"1","description":"d","lines":1,"sha256":"","hooks":[],"calls":[],"state":[],"pages":[],
             "errors":[{"line":12,"column":5,"code":"global","message":"fetch isn't available to mods"},{"code":"manifest","message":"no"}],
             "warnings":[{"line":3,"column":1,"code":"page-missing","message":"x"}]}
            """);

        ModCheckText.Format(report).ShouldBe(string.Join('\n',
            "m 1 · 1 line",
            "hooks:  (none)",
            "calls:  (none)",
            "state:  (none)",
            "pages:  (none)",
            "error   12:5  global  fetch isn't available to mods",
            "error   -  manifest  no",
            "warning 3:1  page-missing  x"));
    }

    [Fact]
    public void Other_matchers_print_whole_and_odd_keys_are_quoted()
    {
        var report = Report("""
            {"ok":true,"name":"m","version":"1","description":"d","lines":2,"sha256":"","calls":[],"state":[],"pages":[],"errors":[],"warnings":[],
             "hooks":[{"event":"ui.press","matcher":{"id":"a","x-y":{"$regex":"q","flags":""},"empty":{}}},{"event":"ui.render","matcher":{"component":"Box"}}]}
            """);

        ModCheckText.Format(report).Split('\n')[1..3].ShouldBe(
        [
            """hooks:  ui.press { id: "a", "x-y": /q/, empty: {} }""",
            "        ui.render \"Box\"",
        ]);
    }

    [Fact]
    public void Strings_are_quoted_as_JavaScript_quotes_them()
    {
        var report = Report("""
            {"ok":true,"name":"m","version":"1","description":"d","lines":3,"sha256":"","calls":[],"state":[],"pages":[],"errors":[],"warnings":[],
             "hooks":[{"event":"ui.render","matcher":{"component":"ToolUse","props":{"title":"a \"b\" \\ c\n\u0001 é <x>","$weird":1}}}]}
            """);

        ModCheckText.Format(report).Split('\n')[1].ShouldBe(
            """hooks:  ui.render "ToolUse" { props: { title: "a \"b\" \\ c\n\u0001 é <x>", $weird: 1 } }""");
    }

    [Fact]
    public void A_column_missing_from_a_positioned_problem_reads_as_1()
    {
        var report = Report("""
            {"ok":false,"name":"m","version":"1","description":"d","lines":4,"sha256":"","hooks":[],"calls":[],"state":[],"pages":[],
             "errors":[{"line":7,"code":"parse","message":"Unexpected token"}],"warnings":[]}
            """);

        ModCheckText.Format(report).Split('\n')[^1].ShouldBe("error   7:1  parse  Unexpected token");
    }
}
