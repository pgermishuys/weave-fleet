using System.Text.Json;
using NuCode.Configuration;

namespace NuCode;

public sealed class ConfigurationModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void NuCodeConfigDeserializesFullJson()
    {
        const string json = """
            {
                "model": "claude-sonnet-4",
                "smallModel": "claude-haiku-3",
                "defaultAgent": "coder",
                "logLevel": "debug",
                "snapshot": true,
                "enabledProviders": ["anthropic", "openai"],
                "disabledProviders": ["google"],
                "plugin": ["my-plugin"],
                "instructions": ["Be concise"],
                "agents": {
                    "coder": {
                        "model": "claude-opus-4",
                        "temperature": 0.7,
                        "steps": 10
                    }
                },
                "permission": {
                    "rules": {
                        "bash": "allow"
                    }
                },
                "mcp": {
                    "my-server": {
                        "type": "local",
                        "command": ["node", "server.js"],
                        "enabled": true
                    }
                },
                "provider": {
                    "anthropic": {
                        "options": {
                            "apiKey": "sk-ant-123"
                        }
                    }
                },
                "compaction": {
                    "auto": true,
                    "reserved": 4096
                },
                "experimental": {
                    "batchTool": true,
                    "openTelemetry": false
                }
            }
            """;

        var config = JsonSerializer.Deserialize<NuCodeConfig>(json, JsonOptions);

        config.ShouldNotBeNull();
        config.Model.ShouldBe("claude-sonnet-4");
        config.SmallModel.ShouldBe("claude-haiku-3");
        config.DefaultAgent.ShouldBe("coder");
        config.LogLevel.ShouldBe("debug");
        config.Snapshot.ShouldBe(true);
        config.EnabledProviders.ShouldBe(["anthropic", "openai"]);
        config.DisabledProviders.ShouldBe(["google"]);
        config.Plugins.ShouldBe(["my-plugin"]);
        config.Instructions.ShouldBe(["Be concise"]);

        config.Agents.ShouldNotBeNull();
        config.Agents.ContainsKey("coder").ShouldBeTrue();
        var coder = config.Agents["coder"];
        coder.Model.ShouldBe("claude-opus-4");
        coder.Temperature.ShouldBe(0.7f);
        coder.Steps.ShouldBe(10);

        config.Permission.ShouldNotBeNull();
        config.Permission.Rules.ShouldNotBeNull();
        config.Permission.Rules.ContainsKey("bash").ShouldBeTrue();
        config.Permission.Rules["bash"].IsSimple.ShouldBeTrue();
        config.Permission.Rules["bash"].Action.ShouldBe("allow");

        config.Mcp.ShouldNotBeNull();
        var mcpServer = config.Mcp["my-server"];
        mcpServer.Type.ShouldBe("local");
        mcpServer.Command.ShouldBe(["node", "server.js"]);
        mcpServer.Enabled.ShouldBe(true);

        config.Provider.ShouldNotBeNull();
        var anthropic = config.Provider["anthropic"];
        anthropic.Options.ShouldNotBeNull();
        anthropic.Options.ApiKey.ShouldBe("sk-ant-123");

        config.Compaction.ShouldNotBeNull();
        config.Compaction.Auto.ShouldBe(true);
        config.Compaction.Reserved.ShouldBe(4096);

        config.Experimental.ShouldNotBeNull();
        config.Experimental.BatchTool.ShouldBe(true);
        config.Experimental.OpenTelemetry.ShouldBe(false);
    }

    [Fact]
    public void NuCodeConfigDeserializesEmptyObject()
    {
        var config = JsonSerializer.Deserialize<NuCodeConfig>("{}", JsonOptions);

        config.ShouldNotBeNull();
        config.Model.ShouldBeNull();
        config.Agents.ShouldBeNull();
        config.Permission.ShouldBeNull();
        config.Mcp.ShouldBeNull();
        config.Provider.ShouldBeNull();
        config.EnabledProviders.ShouldBeNull();
        config.DisabledProviders.ShouldBeNull();
        config.Compaction.ShouldBeNull();
        config.Experimental.ShouldBeNull();
        config.Snapshot.ShouldBeNull();
        config.LogLevel.ShouldBeNull();
    }

    [Fact]
    public void AgentConfigOverrideDeserializesPartialProperties()
    {
        const string json = """
            {
                "model": "gpt-4o",
                "temperature": 0.5,
                "hidden": true
            }
            """;

        var agent = JsonSerializer.Deserialize<AgentConfigOverride>(json, JsonOptions);

        agent.ShouldNotBeNull();
        agent.Model.ShouldBe("gpt-4o");
        agent.Temperature.ShouldBe(0.5f);
        agent.Hidden.ShouldBe(true);
        agent.Name.ShouldBeNull();
        agent.Description.ShouldBeNull();
        agent.Prompt.ShouldBeNull();
        agent.TopP.ShouldBeNull();
        agent.Steps.ShouldBeNull();
        agent.Disable.ShouldBeNull();
        agent.Mode.ShouldBeNull();
        agent.Permission.ShouldBeNull();
        agent.Options.ShouldBeNull();
    }

    [Fact]
    public void PermissionRuleConfigDeserializesFromSimpleString()
    {
        const string json = "\"allow\"";

        var rule = JsonSerializer.Deserialize<PermissionRuleConfig>(json, JsonOptions);

        rule.ShouldNotBeNull();
        rule.IsSimple.ShouldBeTrue();
        rule.Action.ShouldBe("allow");
        rule.PatternRules.ShouldBeNull();
    }

    [Fact]
    public void PermissionRuleConfigDeserializesFromDenyString()
    {
        const string json = "\"deny\"";

        var rule = JsonSerializer.Deserialize<PermissionRuleConfig>(json, JsonOptions);

        rule.ShouldNotBeNull();
        rule.IsSimple.ShouldBeTrue();
        rule.Action.ShouldBe("deny");
    }

    [Fact]
    public void PermissionRuleConfigDeserializesFromAskString()
    {
        const string json = "\"ask\"";

        var rule = JsonSerializer.Deserialize<PermissionRuleConfig>(json, JsonOptions);

        rule.ShouldNotBeNull();
        rule.IsSimple.ShouldBeTrue();
        rule.Action.ShouldBe("ask");
    }

    [Fact]
    public void PermissionRuleConfigDeserializesFromPatternDictionary()
    {
        const string json = """
            {
                "*.cs": "allow",
                "*.sh": "deny",
                "src/**": "ask"
            }
            """;

        var rule = JsonSerializer.Deserialize<PermissionRuleConfig>(json, JsonOptions);

        rule.ShouldNotBeNull();
        rule.IsSimple.ShouldBeFalse();
        rule.Action.ShouldBeNull();
        rule.PatternRules.ShouldNotBeNull();
        rule.PatternRules["*.cs"].ShouldBe("allow");
        rule.PatternRules["*.sh"].ShouldBe("deny");
        rule.PatternRules["src/**"].ShouldBe("ask");
    }

    [Fact]
    public void PermissionConfigDeserializesMixedSimpleAndPatternRules()
    {
        const string json = """
            {
                "rules": {
                    "bash": "allow",
                    "edit": {
                        "*.cs": "allow",
                        "*.sh": "deny"
                    },
                    "read": "ask"
                }
            }
            """;

        var config = JsonSerializer.Deserialize<PermissionConfig>(json, JsonOptions);

        config.ShouldNotBeNull();
        config.Rules.ShouldNotBeNull();
        config.Rules.Count.ShouldBe(3);

        var bashRule = config.Rules["bash"];
        bashRule.IsSimple.ShouldBeTrue();
        bashRule.Action.ShouldBe("allow");

        var editRule = config.Rules["edit"];
        editRule.IsSimple.ShouldBeFalse();
        editRule.PatternRules.ShouldNotBeNull();
        editRule.PatternRules["*.cs"].ShouldBe("allow");
        editRule.PatternRules["*.sh"].ShouldBe("deny");

        var readRule = config.Rules["read"];
        readRule.IsSimple.ShouldBeTrue();
        readRule.Action.ShouldBe("ask");
    }

    [Fact]
    public void McpServerConfigEntryDeserializesLocalType()
    {
        const string json = """
            {
                "type": "local",
                "command": ["npx", "-y", "@modelcontextprotocol/server-filesystem"],
                "environment": {
                    "NODE_ENV": "production"
                },
                "enabled": true,
                "timeout": 30000
            }
            """;

        var entry = JsonSerializer.Deserialize<McpServerConfigEntry>(json, JsonOptions);

        entry.ShouldNotBeNull();
        entry.Type.ShouldBe("local");
        entry.Command.ShouldBe(["npx", "-y", "@modelcontextprotocol/server-filesystem"]);
        entry.Environment.ShouldNotBeNull();
        entry.Environment["NODE_ENV"].ShouldBe("production");
        entry.Enabled.ShouldBe(true);
        entry.Timeout.ShouldBe(30000);
        entry.Url.ShouldBeNull();
    }

    [Fact]
    public void McpServerConfigEntryDeserializesRemoteType()
    {
        const string json = """
            {
                "type": "remote",
                "url": "https://api.example.com/mcp",
                "headers": {
                    "Authorization": "Bearer token123"
                },
                "enabled": false
            }
            """;

        var entry = JsonSerializer.Deserialize<McpServerConfigEntry>(json, JsonOptions);

        entry.ShouldNotBeNull();
        entry.Type.ShouldBe("remote");
        entry.Url.ShouldBe("https://api.example.com/mcp");
        entry.Headers.ShouldNotBeNull();
        entry.Headers["Authorization"].ShouldBe("Bearer token123");
        entry.Enabled.ShouldBe(false);
        entry.Command.ShouldBeNull();
    }

    [Fact]
    public void ProviderConfigWithExtensionDataRoundTrips()
    {
        const string json = """
            {
                "options": {
                    "apiKey": "sk-123",
                    "baseUrl": "https://custom.api.com",
                    "timeout": 60000,
                    "customField": "customValue",
                    "numericExtra": 42
                },
                "whitelist": ["model-a", "model-b"],
                "blacklist": ["model-c"]
            }
            """;

        var provider = JsonSerializer.Deserialize<ProviderConfig>(json, JsonOptions);

        provider.ShouldNotBeNull();
        provider.Options.ShouldNotBeNull();
        provider.Options.ApiKey.ShouldBe("sk-123");
        provider.Options.BaseUrl.ShouldBe("https://custom.api.com");
        provider.Options.Timeout.ShouldBe(60000);
        provider.Options.ExtensionData.ShouldNotBeNull();
        provider.Options.ExtensionData.ContainsKey("customField").ShouldBeTrue();
        provider.Options.ExtensionData["customField"].GetString().ShouldBe("customValue");
        provider.Options.ExtensionData["numericExtra"].GetInt32().ShouldBe(42);
        provider.Whitelist.ShouldBe(["model-a", "model-b"]);
        provider.Blacklist.ShouldBe(["model-c"]);

        // Round-trip: serialize and deserialize again
        var serialized = JsonSerializer.Serialize(provider, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<ProviderConfig>(serialized, JsonOptions);

        roundTripped.ShouldNotBeNull();
        roundTripped.Options.ShouldNotBeNull();
        roundTripped.Options.ApiKey.ShouldBe("sk-123");
        roundTripped.Options.BaseUrl.ShouldBe("https://custom.api.com");
        roundTripped.Options.ExtensionData.ShouldNotBeNull();
        roundTripped.Options.ExtensionData["customField"].GetString().ShouldBe("customValue");
    }

    [Fact]
    public void FullConfigJsonRoundTrip()
    {
        const string json = """
            {
                "model": "claude-sonnet-4",
                "smallModel": "claude-haiku-3",
                "defaultAgent": "coder",
                "logLevel": "info",
                "snapshot": false,
                "enabledProviders": ["anthropic"],
                "disabledProviders": [],
                "plugin": ["plugin-a"],
                "instructions": ["Follow coding standards"],
                "agents": {
                    "coder": {
                        "model": "claude-opus-4",
                        "steps": 5,
                        "hidden": false,
                        "disable": false
                    }
                },
                "permission": {
                    "rules": {
                        "bash": "allow",
                        "edit": {
                            "*.cs": "allow"
                        }
                    }
                },
                "mcp": {
                    "server-a": {
                        "type": "local",
                        "command": ["node", "index.js"],
                        "enabled": true
                    }
                },
                "provider": {
                    "anthropic": {
                        "options": {
                            "apiKey": "sk-ant-abc"
                        },
                        "whitelist": ["claude-sonnet-4"]
                    }
                },
                "compaction": {
                    "auto": true,
                    "prune": false,
                    "reserved": 2048
                },
                "experimental": {
                    "batchTool": false,
                    "openTelemetry": true,
                    "mcpTimeout": 15000
                }
            }
            """;

        var first = JsonSerializer.Deserialize<NuCodeConfig>(json, JsonOptions);
        first.ShouldNotBeNull();

        var serialized = JsonSerializer.Serialize(first, JsonOptions);
        var second = JsonSerializer.Deserialize<NuCodeConfig>(serialized, JsonOptions);
        second.ShouldNotBeNull();

        // Structural equivalence after round-trip
        second.Model.ShouldBe(first.Model);
        second.SmallModel.ShouldBe(first.SmallModel);
        second.DefaultAgent.ShouldBe(first.DefaultAgent);
        second.LogLevel.ShouldBe(first.LogLevel);
        second.Snapshot.ShouldBe(first.Snapshot);
        second.EnabledProviders.ShouldBe(first.EnabledProviders);
        second.DisabledProviders.ShouldBe(first.DisabledProviders);
        second.Plugins.ShouldBe(first.Plugins);
        second.Instructions.ShouldBe(first.Instructions);

        second.Agents.ShouldNotBeNull();
        second.Agents.ContainsKey("coder").ShouldBeTrue();
        second.Agents["coder"].Model.ShouldBe(first.Agents!["coder"].Model);
        second.Agents["coder"].Steps.ShouldBe(first.Agents["coder"].Steps);

        second.Permission?.Rules.ShouldNotBeNull();
        second.Permission!.Rules!["bash"].IsSimple.ShouldBeTrue();
        second.Permission.Rules["bash"].Action.ShouldBe("allow");
        second.Permission.Rules["edit"].IsSimple.ShouldBeFalse();
        second.Permission.Rules["edit"].PatternRules!["*.cs"].ShouldBe("allow");

        second.Mcp.ShouldNotBeNull();
        second.Mcp.ContainsKey("server-a").ShouldBeTrue();
        second.Mcp["server-a"].Type.ShouldBe("local");

        second.Provider.ShouldNotBeNull();
        second.Provider["anthropic"].Options!.ApiKey.ShouldBe("sk-ant-abc");
        second.Provider["anthropic"].Whitelist.ShouldBe(["claude-sonnet-4"]);

        second.Compaction.ShouldNotBeNull();
        second.Compaction.Auto.ShouldBe(true);
        second.Compaction.Prune.ShouldBe(false);
        second.Compaction.Reserved.ShouldBe(2048);

        second.Experimental.ShouldNotBeNull();
        second.Experimental.BatchTool.ShouldBe(false);
        second.Experimental.OpenTelemetry.ShouldBe(true);
        second.Experimental.McpTimeout.ShouldBe(15000);
    }
}
