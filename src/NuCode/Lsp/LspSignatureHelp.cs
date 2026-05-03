namespace NuCode.Lsp;

/// <summary>Signature help returned by textDocument/signatureHelp.</summary>
public sealed record LspSignatureHelp
{
    public required IReadOnlyList<LspSignatureInformation> Signatures { get; init; }
    public int? ActiveSignature { get; init; }
    public int? ActiveParameter { get; init; }
}

/// <summary>Information about a single signature.</summary>
public sealed record LspSignatureInformation
{
    public required string Label { get; init; }
    public string? Documentation { get; init; }
    public IReadOnlyList<LspParameterInformation>? Parameters { get; init; }
}

/// <summary>Information about a single parameter in a signature.</summary>
public sealed record LspParameterInformation
{
    public required string Label { get; init; }
    public string? Documentation { get; init; }
}
