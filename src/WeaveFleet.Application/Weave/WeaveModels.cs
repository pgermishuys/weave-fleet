using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Weave;

/// <summary>Which Weave a harness loaded. They read different files, so Fleet keeps a different file for each.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeaveFlavor>))]
public enum WeaveFlavor
{
    /// <summary><c>@weaveio/weave-adapter-*</c>: reads <c>config.weave</c> and <c>prompts/</c>.</summary>
    [JsonStringEnumMemberName("weave")]
    Weave,

    /// <summary><c>@opencode_weave/weave</c>: reads <c>weave-opencode.jsonc</c>.</summary>
    [JsonStringEnumMemberName("legacy")]
    Legacy,
}

/// <summary>A Weave plugin a harness loaded, or tried to.</summary>
/// <param name="Package">The npm package it came from, e.g. <c>@weaveio/weave-adapter-opencode</c>.</param>
/// <param name="Entry">The plugin as the harness listed it: a package with its version, or a <c>file://</c> path.</param>
/// <param name="AcceptsFleetConfig">
/// Whether this version reads the folder Fleet points it at. Fleet tries it rather than reading version numbers, so
/// local builds count too.
/// </param>
/// <param name="Error">Why the harness couldn't load it, when it's in the plugin list but didn't load.</param>
/// <param name="AddedByFleet">Fleet put this entry in the harness's config (Add Weave), so it may take it out again.</param>
public sealed record WeaveInstall(
    WeaveFlavor Flavor,
    string Package,
    string Entry,
    bool AcceptsFleetConfig,
    string? Error = null,
    bool AddedByFleet = false);

/// <summary>What Fleet found in one harness.</summary>
/// <param name="Checked">False when Fleet can't hand this harness a Weave config yet; <paramref name="Note"/> says why.</param>
/// <param name="AddTo">
/// The config file Add Weave would write, when Fleet can add Weave to this harness: it has none, and Fleet runs
/// without sign-in.
/// </param>
public sealed record WeaveHarnessDetection(
    string HarnessType,
    string HarnessName,
    bool Checked,
    IReadOnlyList<WeaveInstall> Installs,
    string? Note = null,
    string? AddTo = null);

/// <summary>Where Weave goes in a harness's own config, for Add Weave.</summary>
/// <param name="ConfigFolder">The harness's user config folder, e.g. <c>~/.config/opencode</c>.</param>
/// <param name="ConfigFiles">
/// The config files the harness reads there, in the order Fleet looks for them. Fleet edits the one that exists and
/// creates the last when none does; with more than one there, it doesn't pick.
/// </param>
/// <param name="ListKey">The root key of the plugin list: <c>plugin</c> for OpenCode, <c>plugins</c> for OpenCode 2.</param>
/// <param name="Package">The Weave adapter for this harness.</param>
public sealed record WeavePluginHome(string ConfigFolder, IReadOnlyList<string> ConfigFiles, string ListKey, string Package);

/// <summary>What Add Weave or Remove did.</summary>
/// <param name="ConfigPath">The config file Fleet edited.</param>
/// <param name="Entry">The plugin entry it added or took out, e.g. <c>@weaveio/weave-adapter-opencode2@0.2.0-next.3</c>.</param>
/// <param name="Loaded">After an add: whether the harness loaded Weave. After a remove: whether it no longer does.</param>
/// <param name="Message">What happened, in a sentence for Settings.</param>
public sealed record WeavePluginChange(string ConfigPath, string Entry, bool Loaded, string Message, WeaveConfigView Config);

/// <summary>What a harness loaded when it tried a draft Weave config.</summary>
/// <param name="Agents">The Weave agents it loaded, by name.</param>
/// <param name="Error">What's wrong, when Weave loaded none. Null when the draft works.</param>
/// <param name="Details">One line per problem, with the file, line and column when Weave gave them.</param>
public sealed record WeaveCheck(bool Ok, IReadOnlyList<string> Agents, string? Error = null, IReadOnlyList<string>? Details = null);

/// <summary>A folder whose OpenCode instance needs to re-read the saved config.</summary>
/// <param name="Reloaded">False while a session in the folder is busy; Fleet reloads it when that turn ends.</param>
public sealed record WeaveApplyFolder(string Directory, bool Reloaded);

/// <summary>How far the last save has got in the harness's running processes.</summary>
public sealed record WeaveApplyStatus(IReadOnlyList<WeaveApplyFolder> Folders, string? Error = null);
