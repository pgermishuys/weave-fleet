using NuCode.Configuration;
using NuCode.Lsp;

namespace NuCode;

public sealed class LspServerPresetsTests
{
    [Fact]
    public void all_presets_have_valid_command()
    {
        foreach (var preset in LspServerPresets.All)
        {
            preset.Command.ShouldNotBeEmpty($"Preset '{preset.Name}' must have a non-empty command");
            preset.Command[0].ShouldNotBeNullOrWhiteSpace($"Preset '{preset.Name}' primary command must not be blank");
        }
    }

    [Fact]
    public void all_presets_have_valid_extensions()
    {
        foreach (var preset in LspServerPresets.All)
        {
            preset.Extensions.ShouldNotBeEmpty($"Preset '{preset.Name}' must have extensions");
            foreach (var ext in preset.Extensions)
            {
                ext.ShouldStartWith(".");
            }
        }
    }

    [Fact]
    public void all_presets_have_unique_names()
    {
        var names = LspServerPresets.All.Select(p => p.Name).ToList();
        names.ShouldBeUnique();
    }

    [Fact]
    public void preset_ToConfig_maps_correctly()
    {
        var preset = new LspServerPreset
        {
            Name = "test-server",
            Command = ["test-cmd", "--stdio"],
            Extensions = [".test"],
            Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            Initialization = new Dictionary<string, object?> { ["key"] = "value" },
        };

        var config = preset.ToConfig();

        config.Command.ShouldBe(preset.Command);
        config.Extensions.ShouldBe(preset.Extensions);
        config.Env.ShouldBe(preset.Env);
        config.Initialization.ShouldBe(preset.Initialization);
        config.Disabled.ShouldBeNull();
    }

    [Fact]
    public void all_presets_count_is_ten()
    {
        LspServerPresets.All.Count.ShouldBe(10);
    }

    [Fact]
    public void MergeWithConfig_explicit_config_overrides_preset()
    {
        var presets = new List<LspServerPreset>
        {
            new() { Name = "gopls", Command = ["gopls"], Extensions = [".go"] },
        };

        var explicitConfig = new Dictionary<string, LspServerConfig>
        {
            ["gopls"] = new LspServerConfig
            {
                Command = ["custom-gopls", "--special"],
                Extensions = [".go", ".go2"],
            },
        };

        var merged = LspServerPresets.MergeWithConfig(presets, explicitConfig);

        merged.ShouldContainKey("gopls");
        merged["gopls"].Command.ShouldBe(["custom-gopls", "--special"]);
        merged["gopls"].Extensions.ShouldBe([".go", ".go2"]);
    }

    [Fact]
    public void MergeWithConfig_disabled_preset_is_removed()
    {
        var presets = new List<LspServerPreset>
        {
            new() { Name = "gopls", Command = ["gopls"], Extensions = [".go"] },
            new() { Name = "rust-analyzer", Command = ["rust-analyzer"], Extensions = [".rs"] },
        };

        var explicitConfig = new Dictionary<string, LspServerConfig>
        {
            ["gopls"] = new LspServerConfig { Disabled = true },
        };

        var merged = LspServerPresets.MergeWithConfig(presets, explicitConfig);

        merged.ShouldNotContainKey("gopls");
        merged.ShouldContainKey("rust-analyzer");
    }

    [Fact]
    public void MergeWithConfig_presets_added_when_no_explicit_config()
    {
        var presets = new List<LspServerPreset>
        {
            new() { Name = "gopls", Command = ["gopls"], Extensions = [".go"] },
            new() { Name = "clangd", Command = ["clangd"], Extensions = [".c"] },
        };

        var merged = LspServerPresets.MergeWithConfig(presets, null);

        merged.Count.ShouldBe(2);
        merged.ShouldContainKey("gopls");
        merged.ShouldContainKey("clangd");
    }

    [Fact]
    public void MergeWithConfig_explicit_only_entries_are_included()
    {
        var presets = new List<LspServerPreset>
        {
            new() { Name = "gopls", Command = ["gopls"], Extensions = [".go"] },
        };

        var explicitConfig = new Dictionary<string, LspServerConfig>
        {
            ["custom-server"] = new LspServerConfig
            {
                Command = ["my-lsp"],
                Extensions = [".xyz"],
            },
        };

        var merged = LspServerPresets.MergeWithConfig(presets, explicitConfig);

        merged.Count.ShouldBe(2);
        merged.ShouldContainKey("gopls");
        merged.ShouldContainKey("custom-server");
    }

    [Fact]
    public void LspAutoDetect_defaults_to_null_treated_as_true()
    {
        var config = new NuCodeConfig();
        config.LspAutoDetect.ShouldBeNull();
        var autoDetect = config.LspAutoDetect ?? true;
        autoDetect.ShouldBeTrue();
    }

    [Fact]
    public void LspAutoDetect_false_is_respected()
    {
        var config = new NuCodeConfig { LspAutoDetect = false };
        var autoDetect = config.LspAutoDetect ?? true;
        autoDetect.ShouldBeFalse();
    }
}
