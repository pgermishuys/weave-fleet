namespace WeaveFleet.Domain.Entities;

/// <summary>Whose Weave config Weave reads in the user's Fleet sessions.</summary>
public enum WeaveConfigSource
{
    /// <summary>The user's own files (<c>~/.weave/config.weave</c>, <c>~/.config/opencode/weave-opencode.jsonc</c>). Fleet sets nothing.</summary>
    Own,

    /// <summary>The files Fleet keeps for the user, handed to Weave through its config-folder variables.</summary>
    Fleet,
}

/// <summary>
/// The Weave config a user keeps in Fleet: the files Fleet writes into a folder of its own and points Weave at, by
/// their path in that folder (<c>config.weave</c>, <c>prompts/team.md</c>, <c>weave-opencode.jsonc</c>).
/// </summary>
public sealed class WeaveConfig
{
    public WeaveConfigSource Source { get; set; } = WeaveConfigSource.Own;
    public IReadOnlyDictionary<string, string> Files { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>When it was last saved; null when the user never saved one.</summary>
    public string? UpdatedAt { get; set; }
}
