using Shouldly;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>The text search behind the Files tab's "File contents" mode: through git in a repository, by walking outside one.</summary>
public sealed class GitGrepSearchTests
{
    private static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Slice(TextMatch match, TextRange range) => match.Text[range.Start..range.End];

    [Fact]
    public async Task Git_FindsLinesGroupedByFile_WithColumnsAndMarkedRanges()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "src/Retry.cs", "class Retry\n{\n    const int MaxAttempts = 5;\n    int again = MaxAttempts + 1;\n}\n");
        Write(repository.Path, "docs/retries.md", "Up to `MaxAttempts` times.\n");
        Write(repository.Path, "other.txt", "nothing here\n");

        var result = await GitGrepSearch.SearchAsync(repository.Path, "MaxAttempts", regex: false, matchCase: false);

        result.Engine.ShouldBe("git");
        result.Stopped.ShouldBeNull();
        result.Matches.ShouldBe(3);
        result.Files.Select(f => f.Path).ShouldBe(["docs/retries.md", "src/Retry.cs"]);
        var retry = result.Files[1];
        retry.Total.ShouldBe(2);
        retry.Matches.Select(m => m.Line).ShouldBe([3, 4]);
        retry.Matches[0].Text.ShouldBe("const int MaxAttempts = 5;");
        retry.Matches[0].Column.ShouldBe(15); // in the original line, indent included
        Slice(retry.Matches[0], retry.Matches[0].Ranges.Single()).ShouldBe("MaxAttempts");
    }

    [Fact]
    public async Task Git_SmartCase_IgnoresCaseUnlessTheQueryHasACapital_OrMatchCaseIsOn()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "a.txt", "Retry\nretry\n");

        (await GitGrepSearch.SearchAsync(repository.Path, "retry", false, false)).Matches.ShouldBe(2);
        (await GitGrepSearch.SearchAsync(repository.Path, "Retry", false, false)).Matches.ShouldBe(1);
        (await GitGrepSearch.SearchAsync(repository.Path, "retry", false, matchCase: true)).Matches.ShouldBe(1);
    }

    [Fact]
    public async Task Git_Regex_MatchesAPattern_AndPlainTextTakesSpecialCharactersLiterally()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "a.txt", "attempt 1\nattempt 22\na.b\n");

        var regex = await GitGrepSearch.SearchAsync(repository.Path, @"attempt [0-9]+", regex: true, matchCase: false);
        regex.Files.Single().Matches.Select(m => Slice(m, m.Ranges.Single())).ShouldBe(["attempt 1", "attempt 22"]);

        var plain = await GitGrepSearch.SearchAsync(repository.Path, "a.b", regex: false, matchCase: false);
        plain.Matches.ShouldBe(1);
        (await GitGrepSearch.SearchAsync(repository.Path, "-e", false, false)).Matches.ShouldBe(0);
    }

    [Fact]
    public async Task Git_BadRegex_ThrowsWithOneSentenceNamingTheFlavour()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "a.txt", "x\n");

        var error = await Should.ThrowAsync<InvalidSearchPatternException>(
            () => GitGrepSearch.SearchAsync(repository.Path, "a[", regex: true, matchCase: false));

        error.Message.ShouldEndWith("(git extended regex)");
        error.Message.ShouldNotContain("fatal");
        error.Message.ShouldNotContain("a[");
        error.Message.ShouldNotContain('\n');
    }

    [Fact]
    public async Task Git_LeavesOutIgnoredFiles_AndBinaryFiles_ButFindsUntrackedOnes()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, ".gitignore", "generated/\n");
        Write(repository.Path, "generated/out.js", "needle\n");
        Write(repository.Path, "untracked.txt", "needle\n");
        File.WriteAllBytes(Path.Combine(repository.Path, "image.bin"), [0, 1, 2, .. "needle"u8.ToArray()]);

        var result = await GitGrepSearch.SearchAsync(repository.Path, "needle", false, false);

        result.Files.Select(f => f.Path).ShouldBe(["untracked.txt"]);
    }

    [Fact]
    public async Task Git_StopsAtTheCap_SaysSo_AndStillCountsEachFilesLines()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "a.txt", string.Join('\n', Enumerable.Repeat("hit", 150)));
        Write(repository.Path, "b.txt", string.Join('\n', Enumerable.Repeat("hit", 150)));

        var result = await GitGrepSearch.SearchAsync(repository.Path, "hit", false, false, limit: 200);

        result.Stopped.ShouldBe("limit");
        result.Matches.ShouldBe(200);
        result.Files.Select(f => f.Total).ShouldBe([150, 150]);
        result.Files.Sum(f => f.Matches.Count).ShouldBe(200);
    }

    [Fact]
    public async Task Git_NoMatches_IsEmpty_NotAnError()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "a.txt", "x\n");

        var result = await GitGrepSearch.SearchAsync(repository.Path, "absent", false, false);

        result.Files.ShouldBeEmpty();
        result.Stopped.ShouldBeNull();
    }

    [Fact]
    public async Task LongLines_AreCutAroundTheMatch_AndTheIndentIsDropped()
    {
        using var repository = new RealGitRepository();
        Write(repository.Path, "a.txt", "        " + new string('x', 300) + " needle " + new string('y', 300) + "\n");

        var match = (await GitGrepSearch.SearchAsync(repository.Path, "needle", false, false)).Files.Single().Matches.Single();

        match.Text.Length.ShouldBeLessThan(200);
        match.Text.ShouldStartWith("…");
        match.Text.ShouldEndWith("…");
        Slice(match, match.Ranges.Single()).ShouldBe("needle");
    }

    [Fact]
    public async Task Walk_OutsideGit_FindsTextSkippingNoiseFoldersBigFilesAndBinaries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fleet-walk-{Guid.NewGuid():N}");
        try
        {
            Write(root, "src/app.ts", "const Needle = 1;\n");
            Write(root, "node_modules/dep/index.js", "needle\n");
            Write(root, "big.txt", "needle " + new string('x', 1_100_000));
            File.WriteAllBytes(Path.Combine(root, "image.bin"), [0, 0, .. "needle"u8.ToArray()]);

            var result = await GitGrepSearch.SearchAsync(root, "needle", false, false);

            result.Engine.ShouldBe("walk");
            result.Files.Select(f => f.Path).ShouldBe(["src/app.ts"]);
            Slice(result.Files[0].Matches[0], result.Files[0].Matches[0].Ranges.Single()).ShouldBe("Needle");
            (await GitGrepSearch.SearchAsync(root, "needle", false, matchCase: true)).Files.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Walk_BadRegex_Throws_AndTheCapApplies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fleet-walk-{Guid.NewGuid():N}");
        try
        {
            Write(root, "a.txt", string.Join('\n', Enumerable.Repeat("hit", 30)));

            await Should.ThrowAsync<InvalidSearchPatternException>(() => GitGrepSearch.SearchAsync(root, "a[", true, false));
            var result = await GitGrepSearch.SearchAsync(root, "hit", false, false, limit: 10);
            result.Stopped.ShouldBe("limit");
            result.Matches.ShouldBe(10);
            result.Files.Single().Total.ShouldBe(30);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyQuery_FindsNothing()
    {
        using var repository = new RealGitRepository();

        (await GitGrepSearch.SearchAsync(repository.Path, "", false, false)).Files.ShouldBeEmpty();
    }
}
