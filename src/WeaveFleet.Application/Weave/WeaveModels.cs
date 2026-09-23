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

/// <summary>A Weave plugin a harness loaded.</summary>
/// <param name="Package">The npm package it came from, e.g. <c>@weaveio/weave-adapter-opencode</c>.</param>
/// <param name="Entry">The plugin as the harness listed it: a package with its version, or a <c>file://</c> path.</param>
/// <param name="AcceptsFleetConfig">
/// Whether this version reads the folder Fleet points it at. Fleet tries it rather than reading version numbers, so
/// local builds count too.
/// </param>
public sealed record WeaveInstall(WeaveFlavor Flavor, string Package, string Entry, bool AcceptsFleetConfig);

/// <summary>What Fleet found in one harness.</summary>
/// <param name="Checked">False when Fleet can't hand this harness a Weave config yet; <paramref name="Note"/> says why.</param>
public sealed record WeaveHarnessDetection(
    string HarnessType,
    string HarnessName,
    bool Checked,
    IReadOnlyList<WeaveInstall> Installs,
    string? Note = null);

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
