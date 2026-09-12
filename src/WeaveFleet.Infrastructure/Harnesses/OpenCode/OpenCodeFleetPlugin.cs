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
    /// there, and returns its <c>file://</c> URI. The write goes through a temp file, so a starting process
    /// never reads half a file.
    /// </summary>
    public static string Install(string dataDirectory)
    {
        var content = ReadEmbedded();
        var directory = Path.Combine(dataDirectory, "opencode");
        var path = Path.Combine(directory, FileName);

        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
        {
            Directory.CreateDirectory(directory);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }

        return new Uri(path).AbsoluteUri;
    }

    internal static byte[] ReadEmbedded()
    {
        using var stream = typeof(OpenCodeFleetPlugin).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
