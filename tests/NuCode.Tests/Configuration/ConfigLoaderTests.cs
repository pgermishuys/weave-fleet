using NuCode.Configuration;

namespace NuCode;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nucode-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // --- JSONC comment stripping tests ---

    [Fact]
    public void JsoncStripsSingleLineComments()
    {
        const string jsonc = """
            {
                // This is a comment
                "model": "claude-sonnet-4"
            }
            """;

        var stripped = JsoncParser.StripComments(jsonc);

        stripped.ShouldNotContain("//");
        stripped.ShouldContain("\"model\"");
        stripped.ShouldContain("\"claude-sonnet-4\"");
    }

    [Fact]
    public void JsoncStripsMultiLineComments()
    {
        const string jsonc = """
            {
                /* This is a
                   multi-line comment */
                "model": "claude-sonnet-4"
            }
            """;

        var stripped = JsoncParser.StripComments(jsonc);

        stripped.ShouldNotContain("/*");
        stripped.ShouldNotContain("*/");
        stripped.ShouldNotContain("multi-line");
        stripped.ShouldContain("\"model\"");
    }

    [Fact]
    public void JsoncPreservesCommentLikeCharsInsideStrings()
    {
        const string jsonc = """
            {
                "url": "https://example.com",
                "pattern": "src/**/test.cs"
            }
            """;

        var stripped = JsoncParser.StripComments(jsonc);

        stripped.ShouldContain("https://example.com");
        stripped.ShouldContain("src/**/test.cs");
    }

    [Fact]
    public void JsoncHandlesSlashSlashInsideString()
    {
        const string jsonc = """
            {
                "url": "https://example.com/path//to//resource"
            }
            """;

        var stripped = JsoncParser.StripComments(jsonc);

        stripped.ShouldContain("https://example.com/path//to//resource");
    }

    [Fact]
    public void JsoncHandlesEscapedQuotesInsideStrings()
    {
        const string jsonc = """
            {
                "value": "He said \"hello\" // not a comment"
            }
            """;

        var stripped = JsoncParser.StripComments(jsonc);

        // The // inside the string should NOT be treated as a comment
        stripped.ShouldContain("not a comment");
    }

    [Fact]
    public void JsoncStripsMixedComments()
    {
        const string jsonc = """
            {
                // Top-level comment
                "model": "claude-sonnet-4", /* inline comment */
                "logLevel": "info" // trailing comment
            }
            """;

        var stripped = JsoncParser.StripComments(jsonc);

        var config = System.Text.Json.JsonSerializer.Deserialize<NuCodeConfig>(stripped);
        config.ShouldNotBeNull();
        config.Model.ShouldBe("claude-sonnet-4");
        config.LogLevel.ShouldBe("info");
    }

    [Fact]
    public void JsoncHandlesEmptyInput()
    {
        JsoncParser.StripComments("").ShouldBe("");
    }

    [Fact]
    public void JsoncHandlesNoComments()
    {
        const string json = """{"model":"test"}""";
        JsoncParser.StripComments(json).ShouldBe(json);
    }

    [Fact]
    public void JsoncDeserializesFullConfig()
    {
        const string jsonc = """
            {
                // Main model selection
                "model": "claude-sonnet-4",
                "smallModel": "claude-haiku-3",
                /* Agent overrides */
                "agents": {
                    "coder": {
                        "temperature": 0.5
                    }
                }
            }
            """;

        var config = JsoncParser.Deserialize<NuCodeConfig>(jsonc, null);

        config.ShouldNotBeNull();
        config.Model.ShouldBe("claude-sonnet-4");
        config.SmallModel.ShouldBe("claude-haiku-3");
        config.Agents.ShouldNotBeNull();
        config.Agents["coder"].Temperature.ShouldBe(0.5f);
    }

    // --- Config merge tests ---

    [Fact]
    public void MergeReturnsEmptyConfigWhenBothNull()
    {
        var result = ConfigLoader.Merge(null, null);

        result.ShouldNotBeNull();
        result.Model.ShouldBeNull();
        result.Agents.ShouldBeNull();
    }

    [Fact]
    public void MergeReturnsBaseWhenOverlayNull()
    {
        var baseConfig = new NuCodeConfig { Model = "base-model" };

        var result = ConfigLoader.Merge(baseConfig, null);

        result.Model.ShouldBe("base-model");
    }

    [Fact]
    public void MergeReturnsOverlayWhenBaseNull()
    {
        var overlay = new NuCodeConfig { Model = "overlay-model" };

        var result = ConfigLoader.Merge(null, overlay);

        result.Model.ShouldBe("overlay-model");
    }

    [Fact]
    public void MergeOverlayScalarsWin()
    {
        var baseConfig = new NuCodeConfig { Model = "base", SmallModel = "base-small", LogLevel = "debug" };
        var overlay = new NuCodeConfig { Model = "overlay" };

        var result = ConfigLoader.Merge(baseConfig, overlay);

        result.Model.ShouldBe("overlay");
        result.SmallModel.ShouldBe("base-small"); // not overridden
        result.LogLevel.ShouldBe("debug"); // not overridden
    }

    [Fact]
    public void MergeDictionariesDeepMerge()
    {
        var baseConfig = new NuCodeConfig
        {
            Agents = new Dictionary<string, AgentConfigOverride>
            {
                ["coder"] = new() { Model = "base-model", Temperature = 0.5f },
                ["planner"] = new() { Model = "plan-model" },
            },
        };
        var overlay = new NuCodeConfig
        {
            Agents = new Dictionary<string, AgentConfigOverride>
            {
                ["coder"] = new() { Model = "overlay-model" },
                ["explorer"] = new() { Model = "explore-model" },
            },
        };

        var result = ConfigLoader.Merge(baseConfig, overlay);

        result.Agents.ShouldNotBeNull();
        result.Agents.Count.ShouldBe(3);
        // coder is replaced entirely by overlay entry (dict key-level merge, not deep per-agent merge)
        result.Agents["coder"].Model.ShouldBe("overlay-model");
        result.Agents["planner"].Model.ShouldBe("plan-model");
        result.Agents["explorer"].Model.ShouldBe("explore-model");
    }

    [Fact]
    public void MergeInstructionsConcatenate()
    {
        var baseConfig = new NuCodeConfig { Instructions = ["Be concise"] };
        var overlay = new NuCodeConfig { Instructions = ["Follow standards"] };

        var result = ConfigLoader.Merge(baseConfig, overlay);

        result.Instructions.ShouldNotBeNull();
        result.Instructions.Count.ShouldBe(2);
        result.Instructions[0].ShouldBe("Be concise");
        result.Instructions[1].ShouldBe("Follow standards");
    }

    [Fact]
    public void MergePluginsDeduplicate()
    {
        var baseConfig = new NuCodeConfig { Plugins = ["plugin-a", "plugin-b"] };
        var overlay = new NuCodeConfig { Plugins = ["plugin-b", "plugin-c"] };

        var result = ConfigLoader.Merge(baseConfig, overlay);

        result.Plugins.ShouldNotBeNull();
        result.Plugins.Count.ShouldBe(3);
        // overlay entries first, then base entries not already present
        result.Plugins[0].ShouldBe("plugin-b");
        result.Plugins[1].ShouldBe("plugin-c");
        result.Plugins[2].ShouldBe("plugin-a");
    }

    [Fact]
    public void MergePermissionRulesMerge()
    {
        var baseConfig = new NuCodeConfig
        {
            Permission = new PermissionConfig
            {
                Rules = new Dictionary<string, PermissionRuleConfig>
                {
                    ["bash"] = new() { Action = "ask" },
                    ["read"] = new() { Action = "allow" },
                },
            },
        };
        var overlay = new NuCodeConfig
        {
            Permission = new PermissionConfig
            {
                Rules = new Dictionary<string, PermissionRuleConfig>
                {
                    ["bash"] = new() { Action = "allow" },
                    ["edit"] = new() { Action = "deny" },
                },
            },
        };

        var result = ConfigLoader.Merge(baseConfig, overlay);

        result.Permission?.Rules.ShouldNotBeNull();
        result.Permission!.Rules!.Count.ShouldBe(3);
        result.Permission.Rules["bash"].Action.ShouldBe("allow"); // overlay wins
        result.Permission.Rules["read"].Action.ShouldBe("allow"); // from base
        result.Permission.Rules["edit"].Action.ShouldBe("deny"); // from overlay
    }

    // --- File-based config loading tests ---

    [Fact]
    public void LoadReturnsEmptyConfigWhenNoFiles()
    {
        var loader = new ConfigLoader(_tempDir, null);

        var config = loader.Load();

        config.ShouldNotBeNull();
        config.Model.ShouldBeNull();
    }

    [Fact]
    public void LoadReadsProjectConfigJsonc()
    {
        const string jsonc = """
            {
                // Project config
                "model": "claude-sonnet-4",
                "logLevel": "debug"
            }
            """;
        File.WriteAllText(Path.Combine(_tempDir, "nucode.jsonc"), jsonc);

        var loader = new ConfigLoader(_tempDir, null);
        var config = loader.Load();

        config.Model.ShouldBe("claude-sonnet-4");
        config.LogLevel.ShouldBe("debug");
    }

    [Fact]
    public void LoadReadsProjectConfigFromDotNuCodeDir()
    {
        var nuCodeDir = Path.Combine(_tempDir, ".nucode");
        Directory.CreateDirectory(nuCodeDir);

        const string jsonc = """
            {
                "model": "from-dot-nucode"
            }
            """;
        File.WriteAllText(Path.Combine(nuCodeDir, "config.jsonc"), jsonc);

        var loader = new ConfigLoader(_tempDir, null);
        var config = loader.Load();

        config.Model.ShouldBe("from-dot-nucode");
    }

    [Fact]
    public void LoadPrefersNuCodeJsoncOverDotDir()
    {
        // Both nucode.jsonc and .nucode/config.jsonc exist — nucode.jsonc wins
        File.WriteAllText(
            Path.Combine(_tempDir, "nucode.jsonc"),
            """{"model": "from-root"}""");

        var nuCodeDir = Path.Combine(_tempDir, ".nucode");
        Directory.CreateDirectory(nuCodeDir);
        File.WriteAllText(
            Path.Combine(nuCodeDir, "config.jsonc"),
            """{"model": "from-dot-dir"}""");

        var loader = new ConfigLoader(_tempDir, null);
        var config = loader.Load();

        config.Model.ShouldBe("from-root");
    }

    [Fact]
    public void LoadProgrammaticConfigOverridesProject()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "nucode.jsonc"),
            """{"model": "from-file", "logLevel": "debug"}""");

        var programmatic = new NuCodeConfig { Model = "programmatic" };

        var loader = new ConfigLoader(_tempDir, programmatic);
        var config = loader.Load();

        config.Model.ShouldBe("programmatic"); // programmatic wins
        config.LogLevel.ShouldBe("debug"); // file value preserved
    }

    [Fact]
    public void LoadHandlesMalformedJsonGracefully()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "nucode.jsonc"),
            "{ not valid json at all }}}");

        var loader = new ConfigLoader(_tempDir, null);
        var config = loader.Load();

        // Malformed file is skipped, returns empty config
        config.ShouldNotBeNull();
        config.Model.ShouldBeNull();
    }

    [Fact]
    public void FindProjectConfigPathReturnsNullWhenNoConfigExists()
    {
        var result = ConfigLoader.FindProjectConfigPath(_tempDir);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindProjectConfigPathFindsNuCodeJsonc()
    {
        var path = Path.Combine(_tempDir, "nucode.jsonc");
        File.WriteAllText(path, "{}");

        var result = ConfigLoader.FindProjectConfigPath(_tempDir);

        result.ShouldBe(path);
    }

    [Fact]
    public void FindProjectConfigPathFindsDotNuCodeConfig()
    {
        var dir = Path.Combine(_tempDir, ".nucode");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.jsonc");
        File.WriteAllText(path, "{}");

        var result = ConfigLoader.FindProjectConfigPath(_tempDir);

        result.ShouldBe(path);
    }

    [Fact]
    public void GetGlobalConfigPathReturnsValidPath()
    {
        var path = ConfigLoader.GetGlobalConfigPath();

        path.ShouldNotBeNull();
        path.ShouldEndWith("config.jsonc");
        path.ShouldContain("nucode", Case.Insensitive);
    }

    // --- Three-layer integration test ---

    [Fact]
    public void ThreeLayerMergeAppliesCorrectPrecedence()
    {
        // Simulate project config (layer 2)
        const string projectJsonc = """
            {
                // Project config
                "model": "project-model",
                "smallModel": "project-small",
                "logLevel": "info",
                "instructions": ["project instruction"]
            }
            """;
        File.WriteAllText(Path.Combine(_tempDir, "nucode.jsonc"), projectJsonc);

        // Programmatic config (layer 3 — highest)
        var programmatic = new NuCodeConfig
        {
            Model = "programmatic-model",
            Instructions = ["programmatic instruction"],
        };

        var loader = new ConfigLoader(_tempDir, programmatic);
        var config = loader.Load();

        // Programmatic wins for scalar
        config.Model.ShouldBe("programmatic-model");
        // Project value preserved for unset scalar
        config.SmallModel.ShouldBe("project-small");
        config.LogLevel.ShouldBe("info");
        // Instructions concatenate
        config.Instructions.ShouldNotBeNull();
        config.Instructions.Count.ShouldBe(2);
        config.Instructions[0].ShouldBe("project instruction");
        config.Instructions[1].ShouldBe("programmatic instruction");
    }
}
