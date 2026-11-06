namespace WeaveFleet.Domain.Harnesses;

/// <summary>What a tool call a turn left behind says once history shows it as failed.</summary>
public static class CutOffCall
{
    /// <summary>The error a cut-off call shows.</summary>
    public const string Message = "The turn ended before this call finished.";
}
