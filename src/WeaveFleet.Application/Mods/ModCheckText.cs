using System.Text.Json;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// A check report (<c>CheckReport</c>) as the text Keep and <c>fleet_mod_check</c> show, the same text the host's
/// <c>formatReport</c> (<c>mods/host/src/check/format.ts</c>) writes: the contract's "Its check report" for test-chips.
/// </summary>
public static class ModCheckText
{
    public static string Format(JsonElement report) => throw new NotImplementedException();
}
