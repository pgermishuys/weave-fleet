namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>Path helpers shared by the Bun finder and installer.</summary>
internal static class BunPaths
{
    /// <summary>
    /// The path with every link followed, in the file and in each folder above it, so two paths to one file compare equal.
    /// A path that can't be resolved (a loop, a vanished link) is returned as it is.
    /// </summary>
    public static string Canonical(string path, int depth = 0)
    {
        const int MaxDepth = 16;
        var full = Path.GetFullPath(path);
        if (depth > MaxDepth)
            return full;

        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                var info = new FileInfo(current);
                if (info.LinkTarget is null)
                    continue;

                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Canonical(target.FullName, depth + 1);
            }
            catch (IOException)
            {
                // Can't follow it; keep the path as it is.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return current;
    }

    /// <summary>
    /// Why Fleet shouldn't run the Bun at <paramref name="path"/>, as a sentence; <see langword="null"/> when it's fine.
    /// On Linux and macOS a Bun is refused when the file, or any folder from its parent up to <c>/</c>, can be written by
    /// others, or by a group that has anyone besides this user in it (a folder with the sticky bit, like <c>/tmp</c>, is allowed; the file never is), or belongs to
    /// someone other than this user or root: another local user could have planted it. Windows is not checked here:
    /// its folder permissions are ACLs, which this doesn't read.
    /// </summary>
    public static string? WhyNotSafeToRun(string path)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var canonical = Canonical(path);
        var me = UnixFileStatus.EffectiveUserId();
        if (me is null)
            return $"Fleet didn't run it: Fleet couldn't check who can change {canonical}.";

        var current = canonical;
        var isFile = true;
        while (current is not null)
        {
            var problem = Problem(current, isFile, me.Value);
            if (problem is not null)
                return $"Fleet didn't run it: {problem}";

            isFile = false;
            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static string? Problem(string path, bool isFile, uint me)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return null;

            var mode = File.GetUnixFileMode(path);
            var sticky = !isFile && (mode & UnixFileMode.StickyBit) != 0;
            if ((mode & UnixFileMode.OtherWrite) != 0 && !sticky)
                return $"{path} can be changed by other users.";

            if (UnixFileStatus.Stat(path) is not { } identity)
                return $"Fleet couldn't check who owns {path}.";

            // Group write is fine when the group is only this user's: Ubuntu-style private groups with umask 002
            // make every folder the user creates (such as ~/.bun/bin) group-writable.
            if ((mode & UnixFileMode.GroupWrite) != 0 && !sticky && !UnixFileStatus.IsPrivateGroup(identity.Gid))
                return $"{path} can be changed by other users.";

            if (identity.Uid != me && identity.Uid != 0)
                return $"{path} is owned by another user.";

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return $"Fleet couldn't check {path}.";
        }
    }
}
