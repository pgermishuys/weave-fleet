using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>Where this machine sends notifications (<see cref="PushSubscriptionRecord"/>), one row per endpoint.</summary>
public interface IPushSubscriptionRepository
{
    Task<IReadOnlyList<PushSubscriptionRecord>> ListAsync();

    Task<PushSubscriptionRecord?> GetByEndpointAsync(string endpoint);

    /// <summary>Adds the subscription, or replaces the one with the same endpoint (keeping its id and creation time).</summary>
    Task<PushSubscriptionRecord> UpsertAsync(PushSubscriptionRecord subscription);

    Task<bool> DeleteByEndpointAsync(string endpoint);

    /// <summary>Deletes every subscription a device made; returns how many.</summary>
    Task<int> DeleteByDeviceAsync(string deviceId);

    Task RecordSuccessAsync(string id, DateTimeOffset at);

    /// <summary>Counts a failed send and returns the failures in a row.</summary>
    Task<int> RecordFailureAsync(string id);
}
