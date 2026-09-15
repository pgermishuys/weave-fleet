namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// The OpenCode plugin that gives pooled sessions the <c>fleet_canvas_*</c> tools. Its source is
/// <c>opencode/fleet/fleet-canvas.ts</c> in the repo, embedded here. Fleet writes it into its data folder
/// and pooled processes load it by file URI through <c>OPENCODE_CONFIG_CONTENT</c>.
/// </summary>
internal static class OpenCodeFleetPlugin
{
    internal const string FileName = "fleet-canvas.ts";
    private const string ResourceName = "opencode/fleet-canvas.ts";

    /// <summary>
    /// Writes the plugin to <c>{dataDirectory}/opencode/fleet-canvas.ts</c> unless an identical copy is already
    /// there, and returns its <c>file://</c> URI.
    /// </summary>
    public static string Install(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "opencode", FileName);
        EmbeddedFiles.WriteIfChanged(path, ReadEmbedded());
        return new Uri(path).AbsoluteUri;
    }

    internal static byte[] ReadEmbedded() => EmbeddedFiles.Read(ResourceName);
}
