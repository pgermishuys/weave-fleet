using System.Globalization;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Whether a group is a user's own private group, the kind Ubuntu and others give every user. Read from <c>/etc/group</c>
/// (<c>name:passwd:gid:member,member</c>), so it is a Linux-only answer: callers don't ask on macOS, which never relaxes.
/// A group that isn't in the file (one from LDAP, say) counts as not private.
/// </summary>
internal static class PrivateGroup
{
    /// <summary>Where the groups are listed.</summary>
    internal const string SystemGroupFile = "/etc/group";

    /// <summary>
    /// True when <paramref name="gid"/> is the process's effective group, is named <paramref name="userName"/>, and lists
    /// no member but that user, in <paramref name="groupFile"/> (a test passes a fake one). Anything else, or any failure to read the file, is <see langword="false"/>.
    /// </summary>
    public static bool Is(uint gid, uint effectiveGid, string userName, string groupFile = SystemGroupFile)
    {
        if (gid != effectiveGid || userName.Length == 0)
            return false;

        try
        {
            foreach (var line in File.ReadLines(groupFile))
            {
                var fields = line.Split(':');
                if (fields.Length != 4 || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != gid)
                    continue;

                return fields[0] == userName
                    && fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(member => member == userName);
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
