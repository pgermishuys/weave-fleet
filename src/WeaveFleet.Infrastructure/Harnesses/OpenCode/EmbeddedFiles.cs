namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>Files Fleet embeds and writes into its data folder for the OpenCode processes it starts.</summary>
internal static class EmbeddedFiles
{
    public static byte[] Read(string resourceName)
    {
        using var stream = typeof(EmbeddedFiles).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource {resourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> unless an identical copy is already there. The write
    /// goes through a temp file, so a starting process never reads half a file.
    /// </summary>
    public static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
