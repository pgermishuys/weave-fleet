using System.Text.Json;
using WeaveFleet.Infrastructure.Harnesses.NuCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.NuCode;

public sealed class ModelsDevCatalogClientTests
{
    private static JsonDocument ParseJson(string json) =>
        JsonDocument.Parse(json);

    // ── ParseCatalog ──────────────────────────────────────────────────────────

    [Fact]
    public void parse_catalog_returns_models_for_known_provider()
    {
        const string json = """
            {
              "github-copilot": {
                "id": "github-copilot",
                "name": "GitHub Copilot",
                "models": {
                  "gpt-4o": { "id": "gpt-4o", "name": "GPT-4o" },
                  "claude-sonnet-4": { "id": "claude-sonnet-4", "name": "Claude Sonnet 4" }
                }
              }
            }
            """;

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        catalog.TryGetValue("github-copilot", out var models).ShouldBeTrue();
        models!.Count.ShouldBe(2);
        models.ShouldContain(m => m.Id == "gpt-4o" && m.Name == "GPT-4o");
        models.ShouldContain(m => m.Id == "claude-sonnet-4" && m.Name == "Claude Sonnet 4");
    }

    [Fact]
    public void parse_catalog_excludes_deprecated_models()
    {
        const string json = """
            {
              "github-copilot": {
                "id": "github-copilot",
                "models": {
                  "gpt-4o": { "id": "gpt-4o", "name": "GPT-4o" },
                  "gpt-4-turbo": { "id": "gpt-4-turbo", "name": "GPT-4 Turbo", "status": "deprecated" }
                }
              }
            }
            """;

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        catalog.TryGetValue("github-copilot", out var models).ShouldBeTrue();
        models!.Count.ShouldBe(1);
        models.ShouldContain(m => m.Id == "gpt-4o");
        models.ShouldNotContain(m => m.Id == "gpt-4-turbo");
    }

    [Fact]
    public void parse_catalog_omits_provider_with_no_models_property()
    {
        const string json = """
            {
              "github-copilot": {
                "id": "github-copilot",
                "name": "GitHub Copilot"
              }
            }
            """;

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        catalog.TryGetValue("github-copilot", out _).ShouldBeFalse();
    }

    [Fact]
    public void parse_catalog_returns_empty_dict_for_non_object_root()
    {
        const string json = "[]";

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        catalog.ShouldBeEmpty();
    }

    [Fact]
    public void parse_catalog_returns_models_sorted_by_name()
    {
        const string json = """
            {
              "github-copilot": {
                "models": {
                  "z-model": { "id": "z-model", "name": "Z Model" },
                  "a-model": { "id": "a-model", "name": "A Model" },
                  "m-model": { "id": "m-model", "name": "M Model" }
                }
              }
            }
            """;

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        var models = catalog["github-copilot"].ToList();
        models[0].Name.ShouldBe("A Model");
        models[1].Name.ShouldBe("M Model");
        models[2].Name.ShouldBe("Z Model");
    }

    [Fact]
    public void parse_catalog_handles_model_without_name_field()
    {
        const string json = """
            {
              "github-copilot": {
                "models": {
                  "nameless-model": { "id": "nameless-model" }
                }
              }
            }
            """;

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        var models = catalog["github-copilot"];
        models.Count.ShouldBe(1);
        models[0].Id.ShouldBe("nameless-model");
        models[0].Name.ShouldBeNull();
    }

    [Fact]
    public void parse_catalog_lookup_is_case_insensitive()
    {
        const string json = """
            {
              "github-copilot": {
                "models": {
                  "gpt-4o": { "id": "gpt-4o", "name": "GPT-4o" }
                }
              }
            }
            """;

        using var doc = ParseJson(json);
        var catalog = ModelsDevCatalogClient.ParseCatalog(doc);

        catalog.TryGetValue("GitHub-Copilot", out _).ShouldBeTrue();
        catalog.TryGetValue("GITHUB-COPILOT", out _).ShouldBeTrue();
    }
}
