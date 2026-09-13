using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Infrastructure.Tests.Tools;

public sealed class JsoncEditorTests
{
    private static readonly JsonDocumentOptions StrictJsonc = new() { CommentHandling = JsonCommentHandling.Skip };

    private const string UserConfig = """
        {
          "$schema": "https://opencode.ai/config.json",
          "permission": "allow",
          "provider": {
            "github-copilot": {
              "options": {
                //"baseURL": "http://127.0.0.1:5080/copilot"
              }
            }
          }
        }

        """;

    private static JsonObject Server(string command) => new() { ["type"] = "local", ["command"] = new JsonArray(command) };

    [Fact]
    public void SetProperty_AddsTheSectionAfterTheLastProperty_KeepingCommentsAndFormatting()
    {
        var result = JsoncEditor.SetProperty(UserConfig, ["mcp", "filesystem"], Server("npx"));

        result.ShouldBe("""
            {
              "$schema": "https://opencode.ai/config.json",
              "permission": "allow",
              "provider": {
                "github-copilot": {
                  "options": {
                    //"baseURL": "http://127.0.0.1:5080/copilot"
                  }
                }
              },
              "mcp": {
                "filesystem": {
                  "type": "local",
                  "command": [
                    "npx"
                  ]
                }
              }
            }

            """);
    }

    [Fact]
    public void SetProperty_AddsToAnExistingSection()
    {
        var text = """
            {
              "mcp": {
                // my server
                "mine": { "type": "local", "command": ["mine"] }
              }
            }
            """;

        var result = JsoncEditor.SetProperty(text, ["mcp", "theirs"], Server("theirs"));

        result.ShouldContain("// my server");
        result.ShouldContain("""
                "mine": { "type": "local", "command": ["mine"] },
                "theirs": {
                  "type": "local",
            """);
        var mcp = JsonNode.Parse(result, documentOptions: StrictJsonc)!["mcp"]!.AsObject();
        mcp.Select(p => p.Key).ShouldBe(["mine", "theirs"]);
    }

    [Fact]
    public void SetProperty_ReplacesAnExistingValue()
    {
        var text = """
            {
              "mcp": {
                "fs": { "type": "local", "command": ["old"] } // keep me
              }
            }
            """;

        var result = JsoncEditor.SetProperty(text, ["mcp", "fs"], Server("new"));

        result.ShouldContain("// keep me");
        result.ShouldNotContain("old");
        JsonNode.Parse(result, documentOptions: StrictJsonc)!["mcp"]!["fs"]!["command"]![0]!.GetValue<string>().ShouldBe("new");
    }

    [Fact]
    public void SetProperty_IntoAnEmptyObject()
    {
        var result = JsoncEditor.SetProperty("{}", ["mcp", "fs"], Server("npx"));

        result.ShouldBe("""
            {
              "mcp": {
                "fs": {
                  "type": "local",
                  "command": [
                    "npx"
                  ]
                }
              }
            }
            """);
    }

    [Fact]
    public void SetProperty_IntoAnEmptyDocument_StartsAnObject()
    {
        var result = JsoncEditor.SetProperty("", ["mcp", "fs"], Server("npx"));

        JsonNode.Parse(result, documentOptions: StrictJsonc)!["mcp"]!["fs"]!["type"]!.GetValue<string>().ShouldBe("local");
    }

    [Fact]
    public void SetProperty_AfterATrailingComma_StaysValid()
    {
        var text = "{\n  \"a\": 1,\n}\n";

        var result = JsoncEditor.SetProperty(text, ["b"], JsonValue.Create(2));

        result.ShouldBe("{\n  \"a\": 1,\n  \"b\": 2,\n}\n");
    }

    [Fact]
    public void SetProperty_KeepsCrlfLineEndingsAndTheByteOrderMark()
    {
        var text = "\uFEFF{\r\n  \"a\": 1\r\n}\r\n";

        var result = JsoncEditor.SetProperty(text, ["b"], new JsonObject { ["c"] = 2 });

        result.ShouldBe("\uFEFF{\r\n  \"a\": 1,\r\n  \"b\": {\r\n    \"c\": 2\r\n  }\r\n}\r\n");
    }

    [Fact]
    public void SetProperty_DoesNotEscapeCharactersPeopleTypeInCommands()
    {
        var result = JsoncEditor.SetProperty("{}", ["cmd"], JsonValue.Create("a && b + c"));

        result.ShouldContain("\"a && b + c\"");
    }

    [Fact]
    public void SetProperty_WhenAParentIsNotAnObject_Throws()
    {
        Should.Throw<InvalidOperationException>(() => JsoncEditor.SetProperty("""{ "mcp": [] }""", ["mcp", "fs"], Server("npx")));
    }

    [Fact]
    public void SetProperty_WhenTheDocumentIsInvalid_Throws()
    {
        Should.Throw<JsonException>(() => JsoncEditor.SetProperty("""{ "a": }""", ["b"], JsonValue.Create(1)));
    }

    [Fact]
    public void RemoveProperty_FirstOfSeveral_TakesItsLineAndComma()
    {
        var text = """
            {
              "mcp": {
                "a": { "command": ["a"] },
                "b": { "command": ["b"] }
              }
            }
            """;

        var (result, removed) = JsoncEditor.RemoveProperty(text, ["mcp", "a"]);

        removed.ShouldBeTrue();
        result.ShouldBe("""
            {
              "mcp": {
                "b": { "command": ["b"] }
              }
            }
            """);
    }

    [Fact]
    public void RemoveProperty_LastOfSeveral_TakesTheCommaBeforeIt()
    {
        var text = """
            {
              "mcp": {
                "a": { "command": ["a"] }, // a's comment
                "b": { "command": ["b"] }
              }
            }
            """;

        var (result, _) = JsoncEditor.RemoveProperty(text, ["mcp", "b"]);

        result.ShouldBe("""
            {
              "mcp": {
                "a": { "command": ["a"] } // a's comment
              }
            }
            """);
    }

    [Fact]
    public void RemoveProperty_AnObjectHoldingComments_TakesTheCommentsWithIt()
    {
        var (result, _) = JsoncEditor.RemoveProperty(UserConfig, ["provider"]);

        result.ShouldBe("""
            {
              "$schema": "https://opencode.ai/config.json",
              "permission": "allow"
            }

            """);
    }

    [Fact]
    public void RemoveProperty_WhenMissing_ReturnsTheTextUnchanged()
    {
        var (result, removed) = JsoncEditor.RemoveProperty(UserConfig, ["mcp", "nope"]);

        removed.ShouldBeFalse();
        result.ShouldBe(UserConfig);
    }

    [Fact]
    public void SetThenRemove_RoundTripsToTheOriginalText()
    {
        var added = JsoncEditor.SetProperty(UserConfig, ["provider", "extra"], Server("npx"));

        var (result, _) = JsoncEditor.RemoveProperty(added, ["provider", "extra"]);

        result.ShouldBe(UserConfig);
    }

    [Fact]
    public void Edits_KeepMultiByteCharactersIntact()
    {
        var text = "{\n  \"note\": \"café ☕\",\n  \"a\": 1\n}";

        var result = JsoncEditor.SetProperty(text, ["b"], JsonValue.Create("naïve"));

        result.ShouldBe("{\n  \"note\": \"café ☕\",\n  \"a\": 1,\n  \"b\": \"naïve\"\n}");
    }
}
