using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// The agents and models a harness offers in a folder before a session exists there, for the new-session and
/// automation composers.
/// </summary>
public sealed partial class HarnessCatalogService(
    IHarnessRegistry registry,
    IUserContext userContext,
    FleetOptions options,
    ILogger<HarnessCatalogService> logger,
    IHarnessProfileRepository? harnessProfiles = null)
{
    /// <summary>
    /// Asks <paramref name="harnessType"/> what it offers in <paramref name="directory"/>, on the profile a new session
    /// there would use: <paramref name="profileId"/>, <see cref="HarnessProfileService.NoProfile"/> for none, or the
    /// default when null (a profile can add agents and models). No folder means a quick chat's, and so does cloud
    /// mode, where sessions never run in a folder the browser names. The value is null when the harness can't say
    /// without a session.
    /// </summary>
    public async Task<Result<HarnessCatalog?>> GetCatalogAsync(
        string harnessType,
        string? directory,
        CancellationToken ct,
        string? profileId = null)
    {
        var runtime = registry.GetRuntimeByType(harnessType);
        if (runtime is null)
            return FleetError.NotFoundFor("Harness", harnessType);

        string folder;
        if (string.IsNullOrWhiteSpace(directory) || options.Cloud.Enabled)
        {
            folder = QuickChatSessionSourceProvider.BasePath;
            Directory.CreateDirectory(folder);
        }
        else if (!Path.IsPathFullyQualified(directory))
        {
            return FleetError.ValidationError("Directory", "The folder must be a full path.");
        }
        else
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!Directory.Exists(folder))
                return FleetError.ValidationError("Directory", $"The folder '{folder}' doesn't exist.");
        }

        var profile = await ResolveProfileAsync(harnessType, profileId).ConfigureAwait(false);
        if (profile.IsFailure)
            return profile.Error;

        try
        {
            return await runtime.GetCatalogAsync(userContext.UserId, folder, profile.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatalogFailed(ex, harnessType);
            return new FleetError("Harness.CatalogUnavailable", "Couldn't get the agents and models from the harness.");
        }
    }

    /// <summary>The profile a new session would start with, the way SessionOrchestrator picks it.</summary>
    public async Task<Result<HarnessProfile?>> ResolveProfileAsync(string harnessType, string? profileId)
    {
        if (profileId == HarnessProfileService.NoProfile
            || harnessProfiles is null
            || !HarnessProfileService.Supports(registry.GetByType(harnessType), options))
        {
            return Result.Success<HarnessProfile?>(null);
        }

        if (string.IsNullOrWhiteSpace(profileId))
            return Result.Success(await harnessProfiles.GetDefaultAsync(harnessType).ConfigureAwait(false));

        var profile = await harnessProfiles.GetByIdAsync(profileId).ConfigureAwait(false);
        return profile is not null && profile.HarnessType == harnessType
            ? Result.Success<HarnessProfile?>(profile)
            : FleetError.ValidationError("Profile", $"There's no {harnessType} profile with id '{profileId}'.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't list the {HarnessType} harness's agents and models.")]
    private partial void LogCatalogFailed(Exception ex, string harnessType);
}
