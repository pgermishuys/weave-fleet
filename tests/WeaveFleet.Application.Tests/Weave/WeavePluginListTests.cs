using Shouldly;
using WeaveFleet.Application.Weave;

namespace WeaveFleet.Application.Tests.Weave;

public sealed class WeavePluginListTests
{
    private const string Entry = "@weaveio/weave-adapter-opencode2@0.2.0-next.3";

    [Fact]
    public void an_entry_goes_at_the_end_of_the_list_and_the_users_comments_stay()
    {
        const string text = """
            {
              // my providers
              "$schema": "https://opencode.ai/config.json",
              "plugins": [
                "opencode-wakatime", // time tracking
                "@my/plugin"
              ],
              /* the model */
              "model": "anthropic/claude"
            }
            """;

        var edited = WeavePluginList.Add(text, "plugins", Entry);

        edited.ShouldBe("""
            {
              // my providers
              "$schema": "https://opencode.ai/config.json",
              "plugins": [
                "opencode-wakatime", // time tracking
                "@my/plugin",
                "@weaveio/weave-adapter-opencode2@0.2.0-next.3"
              ],
              /* the model */
              "model": "anthropic/claude"
            }
            """);
        WeavePluginList.Read(edited, "plugins").ShouldBe(["opencode-wakatime", "@my/plugin", Entry]);
    }

    [Fact]
    public void a_list_on_one_line_stays_on_one_line()
        => WeavePluginList.Add("""{ "plugin": ["a"] }""", "plugin", "b").ShouldBe("""{ "plugin": ["a", "b"] }""");

    [Fact]
    public void an_empty_list_gets_the_entry()
        => WeavePluginList.Add("""{ "plugins": [] }""", "plugins", Entry).ShouldBe($$"""{ "plugins": ["{{Entry}}"] }""");

    [Fact]
    public void without_a_list_one_is_added_as_the_first_key_indented_like_the_others()
    {
        const string text = """
            {
              // my providers
              "model": "anthropic/claude"
            }
            """;

        WeavePluginList.Add(text, "plugins", Entry).ShouldBe($$"""
            {
              "plugins": ["{{Entry}}"],
              // my providers
              "model": "anthropic/claude"
            }
            """);
    }

    [Fact]
    public void an_empty_config_gets_a_list()
        => WeavePluginList.Add("{}\n", "plugins", Entry).ShouldBe($$"""
            {
              "plugins": ["{{Entry}}"]
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal));

    [Fact]
    public void the_other_opencodes_list_is_left_alone()
    {
        // OpenCode 1 reads "plugin", OpenCode 2 "plugins"; a config the two share has both.
        const string text = """{ "plugin": ["@weaveio/weave-adapter-opencode@0.2.0-next.1"] }""";

        var edited = WeavePluginList.Add(text, "plugins", Entry);

        WeavePluginList.Read(edited, "plugin").ShouldBe(["@weaveio/weave-adapter-opencode@0.2.0-next.1"]);
        WeavePluginList.Read(edited, "plugins").ShouldBe([Entry]);
    }

    [Fact]
    public void a_list_inside_another_key_is_not_the_one()
    {
        const string text = """{ "agent": { "plugins": ["x"] } }""";

        WeavePluginList.Read(text, "plugins").ShouldBeEmpty();
        WeavePluginList.Read(WeavePluginList.Add(text, "plugins", Entry), "plugins").ShouldBe([Entry]);
    }

    [Fact]
    public void a_byte_order_mark_and_trailing_commas_survive()
    {
        var text = "﻿{ \"plugins\": [\"a\",], }";

        var edited = WeavePluginList.Add(text, "plugins", "b");

        edited.ShouldStartWith("﻿");
        WeavePluginList.Read(edited, "plugins").ShouldBe(["a", "b"]);
    }

    [Theory]
    [InlineData("""{ "plugins": "@weaveio/weave-adapter-opencode2" }""", "\"plugins\" in the config isn't a list.")]
    [InlineData("""[]""", "The config isn't a JSON object.")]
    public void a_config_fleet_cant_edit_says_why(string text, string message)
        => Should.Throw<FormatException>(() => WeavePluginList.Add(text, "plugins", Entry)).Message.ShouldBe(message);

    [Fact]
    public void broken_json_says_it_isnt_valid()
        => Should.Throw<FormatException>(() => WeavePluginList.Add("""{ "plugins": [ }""", "plugins", Entry))
            .Message.ShouldStartWith("The config isn't valid JSON");

    [Theory]
    [InlineData("""{ "plugins": ["a", "WEAVE", "b"] }""", """{ "plugins": ["a", "b"] }""")]
    [InlineData("""{ "plugins": ["a", "WEAVE"] }""", """{ "plugins": ["a"] }""")]
    [InlineData("""{ "plugins": ["WEAVE"] }""", """{ "plugins": [] }""")]
    public void removing_takes_the_entry_and_its_comma(string text, string expected)
        => WeavePluginList.Remove(text, "plugins", "WEAVE").ShouldBe(expected);

    [Fact]
    public void removing_from_a_multi_line_list_keeps_the_rest_as_it_was()
    {
        const string text = """
            {
              "plugins": [
                "a",
                "WEAVE",
                "b" // mine
              ]
            }
            """;

        WeavePluginList.Remove(text, "plugins", "WEAVE").ShouldBe("""
            {
              "plugins": [
                "a",
                "b" // mine
              ]
            }
            """);
    }

    [Fact]
    public void removing_an_entry_that_isnt_there_changes_nothing()
    {
        WeavePluginList.Remove("""{ "plugins": ["a"] }""", "plugins", "WEAVE").ShouldBeNull();
        WeavePluginList.Remove("""{ "model": "x" }""", "plugins", "WEAVE").ShouldBeNull();
    }
}
