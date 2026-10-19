using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>The <c>session_permission_rules</c> table (migration 065): what each session's user said not to ask again about.</summary>
public interface ISessionPermissionRuleRepository
{
    /// <summary>The session's rules, oldest first.</summary>
    Task<IReadOnlyList<SessionPermissionRule>> ListAsync(string sessionId);

    /// <summary>Saves a rule; false when the session doesn't exist.</summary>
    Task<bool> AddAsync(SessionPermissionRule rule);

    /// <summary>Removes one rule; false when the session has no such rule.</summary>
    Task<bool> DeleteAsync(string sessionId, string ruleId);

    /// <summary>Removes every rule of the session, returning how many there were.</summary>
    Task<int> DeleteAllAsync(string sessionId);

    /// <summary>Copies the rules of <paramref name="fromSessionId"/> to <paramref name="toSessionId"/> under new ids.</summary>
    Task CopyAsync(string fromSessionId, string toSessionId);

    /// <summary>The user who owns the session, or <see langword="null"/>; used to address the change event.</summary>
    Task<string?> OwnerAsync(string sessionId);
}
