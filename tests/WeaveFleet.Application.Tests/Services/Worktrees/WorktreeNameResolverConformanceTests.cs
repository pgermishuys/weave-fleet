using System.Text.Json.Nodes;
using Shouldly;
using WeaveFleet.Application.Services.Worktrees;

namespace WeaveFleet.Application.Tests.Services.Worktrees;

/// <summary>
/// The shared cases in <c>tests/contracts/worktree-naming-cases.json</c>, which the composer's
/// preview resolver is asserted against too. A name the two disagree on is a name the composer
/// promised and the worktree didn't get, so the fixture is the contract between them.
/// </summary>
public sealed class WorktreeNameResolverConformanceTests
{
    private static readonly WorktreeNamingCases _cases = Load();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in _cases.Cases.Select(c => c.Name))
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ResolvesTheSharedCase(string name)
    {
        var testCase = _cases.Cases.Single(c => c.Name == name);

        var result = WorktreeNameResolver.Resolve(
            testCase.Naming.ToNaming(_cases.Context.Prefix),
            _cases.Context.ToContext(),
            testCase.Message,
            testCase.BranchOverride);

        result.Branch.ShouldBe(testCase.Expect.Branch, name);
        Normalize(result.Root).ShouldBe(testCase.Expect.Root, name);
        result.Folder.ShouldBe(testCase.Expect.Folder, name);
    }

    [Fact]
    public void ResolvesTheSharedRootPrefixes()
    {
        foreach (var prefix in _cases.RootPrefixes)
        {
            var naming = WorktreeNaming.Defaults with { Root = prefix.Root };
            var resolved = WorktreeNameResolver.ResolveRootPrefix(naming, _cases.Context.ToContext());

            (resolved is null ? null : Normalize(resolved)).ShouldBe(prefix.Expect, prefix.Root);
        }
    }

    [Fact]
    public void RefusesTheSharedInvalidTemplates()
    {
        foreach (var invalid in _cases.InvalidTemplates)
        {
            var naming = WorktreeNaming.Defaults.Overlay(new WorktreeNamingOverride
            {
                Branch = invalid.Naming.Branch,
                Root = invalid.Naming.Root,
                Folder = invalid.Naming.Folder,
                Capture = invalid.Capture ?? invalid.Naming.Capture,
                Prefix = invalid.Naming.Prefix,
            });

            var problems = WorktreeNameResolver.Validate(naming);

            problems.ShouldNotBeEmpty(invalid.Because);
            problems.Select(problem => problem.Field).ShouldContain(invalid.Field, invalid.Because);
        }
    }

    /// <summary>The fixture's paths are posix; on Windows the resolver builds them with backslashes.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/');

    private static WorktreeNamingCases Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "worktree-naming-cases.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var context = root["context"]!.AsObject();

        return new WorktreeNamingCases(
            new WorktreeNamingCaseContext(
                Text(context, "repositoryPath")!,
                Text(context, "user")!,
                Text(context, "prefix")!,
                Text(context, "date")!,
                Text(context, "shortId")!,
                Text(context, "home")!),
            [.. root["cases"]!.AsArray().Select(node => new WorktreeNamingCase(
                Text(node!.AsObject(), "name")!,
                Templates(node.AsObject()["naming"]),
                Text(node.AsObject(), "message") ?? string.Empty,
                Text(node.AsObject(), "branchOverride"),
                new WorktreeNamingCaseExpectation(
                    Text(node.AsObject()["expect"]!.AsObject(), "branch"),
                    Text(node.AsObject()["expect"]!.AsObject(), "root")!,
                    Text(node.AsObject()["expect"]!.AsObject(), "folder") ?? string.Empty)))],
            [.. root["rootPrefixes"]!.AsArray().Select(node => new WorktreeNamingRootPrefixCase(
                Text(node!.AsObject(), "root")!,
                Text(node.AsObject(), "expect")))],
            [.. root["invalidTemplates"]!.AsArray().Select(node => new WorktreeNamingInvalidCase(
                Text(node!.AsObject(), "field")!,
                Templates(node.AsObject()["naming"]),
                Captures(node.AsObject()["capture"]),
                Text(node.AsObject(), "because")!))]);

        static string? Text(JsonObject owner, string name)
            => owner[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        static WorktreeNamingCaseTemplates Templates(JsonNode? node)
        {
            var naming = node?.AsObject();
            return naming is null
                ? new WorktreeNamingCaseTemplates(null, null, null, null, null)
                : new WorktreeNamingCaseTemplates(
                    Text(naming, "branch"),
                    Text(naming, "root"),
                    Text(naming, "folder"),
                    Captures(naming["capture"]),
                    naming.ContainsKey("prefix") ? Text(naming, "prefix") ?? string.Empty : null);
        }

        static Dictionary<string, string>? Captures(JsonNode? node)
        {
            if (node?.AsObject() is not { } captures)
                return null;

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, pattern) in captures)
            {
                if (pattern is JsonValue value && value.TryGetValue<string>(out var text))
                    parsed[name] = text;
            }

            return parsed;
        }
    }

    internal sealed record WorktreeNamingCases(
        WorktreeNamingCaseContext Context,
        IReadOnlyList<WorktreeNamingCase> Cases,
        IReadOnlyList<WorktreeNamingRootPrefixCase> RootPrefixes,
        IReadOnlyList<WorktreeNamingInvalidCase> InvalidTemplates);

    internal sealed record WorktreeNamingCaseContext(
        string RepositoryPath,
        string User,
        string Prefix,
        string Date,
        string ShortId,
        string Home)
    {
        public WorktreeNamingContext ToContext() => new(
            RepositoryPath: RepositoryPath,
            UserName: User,
            Date: DateOnly.Parse(Date, System.Globalization.CultureInfo.InvariantCulture),
            ShortId: ShortId,
            HomeDirectory: Home);
    }

    internal sealed record WorktreeNamingCase(
        string Name,
        WorktreeNamingCaseTemplates Naming,
        string Message,
        string? BranchOverride,
        WorktreeNamingCaseExpectation Expect);

    internal sealed record WorktreeNamingCaseTemplates(
        string? Branch,
        string? Root,
        string? Folder,
        Dictionary<string, string>? Capture,
        string? Prefix)
    {
        /// <param name="contextPrefix">
        /// The fixture's own prefix, which a case overrides when it sets one — including to empty,
        /// which is a case in its own right.
        /// </param>
        public WorktreeNaming ToNaming(string contextPrefix) => WorktreeNaming.Defaults.Overlay(new WorktreeNamingOverride
        {
            Branch = Branch,
            Root = Root,
            Folder = Folder,
            Capture = Capture,
            Prefix = Prefix ?? contextPrefix,
        });
    }

    internal sealed record WorktreeNamingCaseExpectation(string? Branch, string Root, string Folder);

    internal sealed record WorktreeNamingRootPrefixCase(string Root, string? Expect);

    internal sealed record WorktreeNamingInvalidCase(
        string Field,
        WorktreeNamingCaseTemplates Naming,
        Dictionary<string, string>? Capture,
        string Because);
}
