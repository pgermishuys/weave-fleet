namespace NuCode;

/// <summary>
/// Represents a file attachment produced by a tool execution.
/// </summary>
/// <param name="Name">The filename or identifier for this attachment.</param>
/// <param name="MimeType">The MIME type of the attachment (e.g., "image/png").</param>
/// <param name="Data">The raw binary content of the attachment.</param>
public sealed record ToolAttachment(string Name, string MimeType, ReadOnlyMemory<byte> Data);
