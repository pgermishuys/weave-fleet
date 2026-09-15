using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>A profile as the API shows it, with how many sessions that aren't archived use it.</summary>
public sealed record HarnessProfileView(
    string Id,
    string HarnessType,
    string Name,
    string Content,
    bool IsDefault,
    int OpenSessions,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Keeps the user's harness profiles: config they pick per session. Every save is checked by the harness first,
/// so a profile that would stop sessions from starting is never stored.
/// </summary>
public sealed class HarnessProfileService(
    IHarnessProfileRepository profiles,
    IHarnessRegistry harnessRegistry,
    IUserContext userContext,
    FleetOptions options,
    TimeProvider timeProvider)
{
    /// <summary>Asks for a session without a profile, even when the harness has a default.</summary>
    public const string NoProfile = "none";

    public const int MaxNameLength = 60;
    public const int MaxContentLength = 64 * 1024;

    /// <summary>
    /// Whether sessions on this harness can use profiles. Not with auth on: a profile can load plugins, which
    /// would run anyone's code on the machine that hosts Fleet.
    /// </summary>
    public static bool Supports(IHarness? harness, FleetOptions options) =>
        harness is { Capabilities.SupportsProfiles: true } && !options.Auth.Enabled;

    public async Task<Result<IReadOnlyList<HarnessProfileView>>> ListAsync(string harnessType)
    {
        if (RequireSupport(harnessType) is { } unsupported)
            return unsupported;

        var list = await profiles.ListAsync(harnessType).ConfigureAwait(false);
        var counts = await profiles.CountOpenSessionsAsync(harnessType).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<HarnessProfileView>>(list.Select(profile => ToView(profile, counts)).ToList());
    }

    public async Task<Result<HarnessProfileView>> CreateAsync(string harnessType, string? name, string? content, CancellationToken ct)
    {
        if (RequireSupport(harnessType) is { } unsupported)
            return unsupported;
        if (Validate(name, content) is { } invalid)
            return invalid;
        if (await CheckContentAsync(harnessType, content!, ct).ConfigureAwait(false) is { } broken)
            return broken;

        var now = timeProvider.GetUtcNow().ToString("O");
        var profile = new HarnessProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            HarnessType = harnessType,
            Name = name!.Trim(),
            Content = content!,
            CreatedAt = now,
            UpdatedAt = now,
            UserId = userContext.UserId,
        };
        await profiles.InsertAsync(profile).ConfigureAwait(false);
        return ToView(profile, openSessions: 0);
    }

    public async Task<Result<HarnessProfileView>> UpdateAsync(string harnessType, string id, string? name, string? content, CancellationToken ct)
    {
        if (RequireSupport(harnessType) is { } unsupported)
            return unsupported;
        var profile = await profiles.GetByIdAsync(id).ConfigureAwait(false);
        if (profile is null || profile.HarnessType != harnessType)
            return FleetError.NotFoundFor("Profile", id);
        if (Validate(name, content) is { } invalid)
            return invalid;
        if (content != profile.Content && await CheckContentAsync(harnessType, content!, ct).ConfigureAwait(false) is { } broken)
            return broken;

        profile.Name = name!.Trim();
        profile.Content = content!;
        profile.UpdatedAt = timeProvider.GetUtcNow().ToString("O");
        await profiles.UpdateAsync(profile).ConfigureAwait(false);
        var counts = await profiles.CountOpenSessionsAsync(harnessType).ConfigureAwait(false);
        return ToView(profile, counts);
    }

    public async Task<Result<Unit>> DeleteAsync(string harnessType, string id)
    {
        if (RequireSupport(harnessType) is { } unsupported)
            return unsupported;
        var profile = await profiles.GetByIdAsync(id).ConfigureAwait(false);
        if (profile is null || profile.HarnessType != harnessType)
            return FleetError.NotFoundFor("Profile", id);

        var counts = await profiles.CountOpenSessionsAsync(harnessType).ConfigureAwait(false);
        if (counts.TryGetValue(id, out var open) && open > 0)
        {
            return new FleetError("General.Conflict",
                $"{open} {(open == 1 ? "session uses" : "sessions use")} {profile.Name}. Archive {(open == 1 ? "it" : "them")} before deleting the profile.");
        }

        await profiles.DeleteAsync(id).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>Makes a profile the harness's default, or clears the default when <paramref name="id"/> is null.</summary>
    public async Task<Result<Unit>> SetDefaultAsync(string harnessType, string? id)
    {
        if (RequireSupport(harnessType) is { } unsupported)
            return unsupported;
        if (id is not null)
        {
            var profile = await profiles.GetByIdAsync(id).ConfigureAwait(false);
            if (profile is null || profile.HarnessType != harnessType)
                return FleetError.NotFoundFor("Profile", id);
        }

        await profiles.SetDefaultAsync(harnessType, id).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>Asks the harness to try the content, without saving anything.</summary>
    public async Task<Result<HarnessProfileCheck>> CheckAsync(string harnessType, string? content, CancellationToken ct)
    {
        if (RequireSupport(harnessType) is { } unsupported)
            return unsupported;
        if (Validate("check", content) is { } invalid)
            return invalid;
        return await RunCheckAsync(harnessType, content!, ct).ConfigureAwait(false);
    }

    private FleetError? RequireSupport(string harnessType)
    {
        var harness = harnessRegistry.GetByType(harnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", harnessType);
        if (!Supports(harness, options))
        {
            return FleetError.ValidationError("Profile.Unsupported", options.Auth.Enabled
                ? "Profiles aren't available when Fleet runs with sign-in."
                : $"{harness.DisplayName} doesn't support profiles.");
        }
        return null;
    }

    private static FleetError? Validate(string? name, string? content)
    {
        if (string.IsNullOrWhiteSpace(name))
            return FleetError.ValidationError("Profile.Name", "Give the profile a name.");
        if (name.Trim().Length > MaxNameLength)
            return FleetError.ValidationError("Profile.Name", $"Keep the name to {MaxNameLength} characters.");
        if (string.IsNullOrWhiteSpace(content))
            return FleetError.ValidationError("Profile.Content", "The profile is empty. Add the config it should use.");
        if (content.Length > MaxContentLength)
            return FleetError.ValidationError("Profile.Content", "The profile is too long. Keep it under 64 KB.");
        return null;
    }

    private async Task<FleetError?> CheckContentAsync(string harnessType, string content, CancellationToken ct)
    {
        var check = await RunCheckAsync(harnessType, content, ct).ConfigureAwait(false);
        if (check.Ok)
            return null;

        var details = check.Details is { Count: > 0 } ? " " + string.Join(" ", check.Details) : string.Empty;
        return FleetError.ValidationError("Profile.Content", $"{check.Error}{details}");
    }

    private async Task<HarnessProfileCheck> RunCheckAsync(string harnessType, string content, CancellationToken ct)
    {
        var runtime = harnessRegistry.GetRuntimeByType(harnessType);
        return runtime is null
            ? HarnessProfileCheck.Passed
            : await runtime.CheckProfileAsync(userContext.UserId, content, ct).ConfigureAwait(false);
    }

    private static HarnessProfileView ToView(HarnessProfile profile, IReadOnlyDictionary<string, int> counts) =>
        ToView(profile, counts.TryGetValue(profile.Id, out var open) ? open : 0);

    private static HarnessProfileView ToView(HarnessProfile profile, int openSessions) => new(
        profile.Id, profile.HarnessType, profile.Name, profile.Content, profile.IsDefault, openSessions,
        profile.CreatedAt, profile.UpdatedAt);
}
