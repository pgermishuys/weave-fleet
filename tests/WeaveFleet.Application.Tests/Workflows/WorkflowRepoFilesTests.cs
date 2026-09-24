using System.Text;
using Shouldly;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowRepoFilesTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("fleet-workflow-files-").FullName;
    private readonly string _folder;

    public WorkflowRepoFilesTests()
    {
        _folder = Path.Combine(_repo, ".weave", "workflows");
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private static IReadOnlyList<WorkflowEntry> Library(string? repo = null)
        => WorkflowCatalog.ListAsync(repo).GetAwaiter().GetResult();

    [Fact]
    public async Task new_creates_the_smallest_valid_workflow_uncommitted_in_the_repo()
    {
        var created = (await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Tidy up a flaky test"), Library(), default)).Value;

        created.WorkflowId.ShouldBe("repo:tidy-up-a-flaky-test");
        created.File.ShouldBe(".weave/workflows/tidy-up-a-flaky-test.yaml");
        created.Check.Errors.ShouldBeEmpty();
        var text = await File.ReadAllTextAsync(Path.Combine(_folder, "tidy-up-a-flaky-test.yaml"));
        text.ShouldBe("""
            name: Tidy up a flaky test
            starts-from: sentence
            runs-in: new-worktree
            steps:
              - id: work
                title: Do the work
                agent: build
                model: standard
                prompt: |
                  The request: {{request}}
                outcomes: [done]

            """.Replace("\r\n", "\n"));
        created.Hash.ShouldBe(WorkflowRepoFiles.Hash(Encoding.UTF8.GetBytes(text)));
        (await WorkflowCatalog.FindAsync(created.WorkflowId, _repo)).ShouldNotBeNull().Definition.ShouldNotBeNull();
    }

    [Fact]
    public async Task duplicate_copies_a_built_in_with_its_new_name_and_without_its_comment()
    {
        var builtIn = WorkflowCatalog.BuiltIns.Single(e => e.Id == "builtin:build-a-feature").Definition!;

        var created = (await WorkflowRepoFiles.CreateAsync(_repo, builtIn with { Name = "Build a feature, our way" }, Library(), default)).Value;

        created.File.ShouldBe(".weave/workflows/build-a-feature-our-way.yaml");
        created.Check.Comments.ShouldBeEmpty();
        var copy = created.Check.Draft.ShouldNotBeNull();
        copy.Name.ShouldBe("Build a feature, our way");
        WorkflowYamlWriterTests.Same(copy, builtIn with { Name = "Build a feature, our way" });
    }

    [Theory]
    [InlineData("Build a feature", "There's already a workflow called Build a feature. Pick another name.")]
    [InlineData("build A FEATURE", "There's already a workflow called Build a feature. Pick another name.")]
    public async Task a_name_that_is_taken_is_refused(string name, string message)
    {
        var result = await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank(name), Library(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
        result.Error.Description.ShouldBe(message);
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact]
    public async Task a_file_name_that_is_taken_is_refused_and_left_alone()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "deps.yml"), "not even yaml: [");

        var result = await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps!"), Library(), default);

        result.Error.Description.ShouldBe("There's already a file called deps.yaml in .weave/workflows. Pick another name.");
        (await File.ReadAllTextAsync(Path.Combine(_folder, "deps.yml"))).ShouldBe("not even yaml: [");
    }

    [Fact]
    public async Task a_workflow_named_in_the_repo_is_taken_too()
    {
        await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps"), Library(), default);
        await File.WriteAllTextAsync(Path.Combine(_folder, "other.yaml"), "name: Weekly bump\nsteps: []\n");

        var result = await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("weekly bump"), Library(_repo), default);

        result.Error.Description.ShouldBe("There's already a workflow called Weekly bump. Pick another name.");
    }

    [Fact]
    public async Task saving_writes_the_text_as_it_is_and_gives_the_new_hash()
    {
        var created = (await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps"), Library(), default)).Value;
        var text = created.Check.Text.Replace("title: Do the work", "title: Do the work # with a comment") + "# the end\n";

        var saved = (await WorkflowRepoFiles.SaveAsync(_repo, created.WorkflowId, text, created.Hash, force: false, default)).Value;

        (await File.ReadAllTextAsync(Path.Combine(_folder, "deps.yaml"))).ShouldBe(text);
        saved.Hash.ShouldNotBe(created.Hash);
        saved.Check.Comments.Count.ShouldBe(2);
        Directory.GetFiles(_folder).ShouldBe([Path.Combine(_folder, "deps.yaml")]);
    }

    [Fact]
    public async Task a_save_is_refused_when_the_file_changed_on_disk_unless_you_keep_yours()
    {
        var created = (await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps"), Library(), default)).Value;
        var path = Path.Combine(_folder, "deps.yaml");
        var theirs = created.Check.Text.Replace("Do the work", "Their title");
        await File.WriteAllTextAsync(path, theirs);
        var mine = created.Check.Text.Replace("Do the work", "My title");

        var refused = await WorkflowRepoFiles.SaveAsync(_repo, created.WorkflowId, mine, created.Hash, force: false, default);

        refused.Error.Code.ShouldBe("General.Conflict");
        refused.Error.Description.ShouldBe(WorkflowRepoFiles.ChangedOnDiskMessage);
        (await File.ReadAllTextAsync(path)).ShouldBe(theirs);

        (await WorkflowRepoFiles.SaveAsync(_repo, created.WorkflowId, mine, created.Hash, force: true, default)).IsSuccess.ShouldBeTrue();
        (await File.ReadAllTextAsync(path)).ShouldBe(mine);
    }

    [Fact]
    public async Task a_file_deleted_on_disk_is_a_change_too()
    {
        var created = (await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps"), Library(), default)).Value;
        File.Delete(Path.Combine(_folder, "deps.yaml"));

        var refused = await WorkflowRepoFiles.SaveAsync(_repo, created.WorkflowId, created.Check.Text, created.Hash, force: false, default);

        refused.Error.Code.ShouldBe("General.Conflict");
    }

    [Fact]
    public async Task a_save_with_errors_is_refused()
    {
        var created = (await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps"), Library(), default)).Value;

        var refused = await WorkflowRepoFiles.SaveAsync(_repo, created.WorkflowId, created.Check.Text.Replace("model: standard", "model: best"), created.Hash, force: false, default);

        refused.Error.Code.ShouldStartWith("Validation.");
        refused.Error.Description.ShouldBe("Line 8: work's model is \"best\": use strong, standard, fast, or an exact provider/model.");
    }

    [Fact]
    public async Task a_built_in_is_refused()
    {
        var result = await WorkflowRepoFiles.SaveAsync(_repo, "builtin:build-a-feature", WorkflowCatalog.BuiltIns.Single(e => e.Id == "builtin:build-a-feature").Text, null, force: true, default);

        result.Error.Description.ShouldBe("Built-in workflows can't be edited. Duplicate it into this repo to change it.");
    }

    [Theory]
    [InlineData("repo:../../etc/passwd")]
    [InlineData("repo:..")]
    [InlineData("repo:a/b")]
    [InlineData("repo:.hidden")]
    [InlineData("deps")]
    public async Task only_files_in_weave_workflows_can_be_saved(string id)
    {
        var result = await WorkflowRepoFiles.SaveAsync(_repo, id, WorkflowYamlWriter.Write(WorkflowRepoFiles.Blank("X")), null, force: true, default);

        result.Error.Description.ShouldBe("That isn't a workflow file in .weave/workflows.");
        Directory.GetFiles(_repo, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_weave_workflows_folder_that_links_out_of_the_repo_is_refused()
    {
        var elsewhere = Directory.CreateTempSubdirectory("fleet-workflow-elsewhere-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(_repo, ".weave"));
            Directory.CreateSymbolicLink(_folder, elsewhere);

            var result = await WorkflowRepoFiles.SaveAsync(_repo, "repo:deps", WorkflowYamlWriter.Write(WorkflowRepoFiles.Blank("X")), null, force: true, default);

            result.Error.Description.ShouldBe(".weave/workflows is a link out of the repository, so Fleet won't write there.");
            Directory.GetFiles(elsewhere).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task a_workflow_file_that_is_a_link_is_refused()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"fleet-target-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(elsewhere, "name: X\n");
        try
        {
            Directory.CreateDirectory(_folder);
            File.CreateSymbolicLink(Path.Combine(_folder, "deps.yaml"), elsewhere);

            var result = await WorkflowRepoFiles.SaveAsync(_repo, "repo:deps", WorkflowYamlWriter.Write(WorkflowRepoFiles.Blank("X")), null, force: true, default);

            result.Error.Description.ShouldBe("deps.yaml is a link, so Fleet won't write through it.");
            (await File.ReadAllTextAsync(elsewhere)).ShouldBe("name: X\n");
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public async Task open_reads_the_file_with_its_hash_and_check()
    {
        Directory.CreateDirectory(_folder);
        var text = "# ours\nname: Deps\nsteps:\n  - id: a\n    title: A\n    model: fast\n    prompt: A.\n    outcomes: [done]\n";
        await File.WriteAllTextAsync(Path.Combine(_folder, "deps.yaml"), text);

        var opened = (await WorkflowRepoFiles.OpenAsync(_repo, "repo:deps", default)).Value;

        opened.File.ShouldBe(".weave/workflows/deps.yaml");
        opened.Hash.ShouldBe(WorkflowRepoFiles.Hash(Encoding.UTF8.GetBytes(text)));
        opened.Check.Comments.ShouldBe([new WorkflowComment(1, "ours")]);
        (await WorkflowRepoFiles.OpenAsync(_repo, "repo:missing", default)).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Tidy up a flaky test", "tidy-up-a-flaky-test")]
    [InlineData("Build a feature, our way", "build-a-feature-our-way")]
    [InlineData("  --Weird!! Name__ ", "weird-name")]
    [InlineData("!!!", "workflow")]
    public void slugs(string name, string slug) => WorkflowRepoFiles.Slug(name).ShouldBe(slug);

    private const string Drafted = """
        # Drafted from a session.
        name: Deps
        steps:
          - id: bump
            title: Bump
            model: standard
            prompt: |
              The request: {{request}}
            outcomes: [done]
        """;

    [Fact]
    public async Task saving_a_drafted_workflow_creates_its_file_with_the_text_as_it_is_named_after_its_name()
    {
        var created = (await WorkflowRepoFiles.CreateAsync(_repo, Drafted, Library(), default)).Value;

        created.WorkflowId.ShouldBe("repo:deps");
        File.ReadAllText(Path.Combine(_folder, "deps.yaml")).ShouldBe(Drafted);
        created.Check.Comments.ShouldHaveSingleItem().Text.ShouldBe("Drafted from a session.");
    }

    [Fact]
    public async Task saving_a_drafted_workflow_follows_the_new_workflow_rules_for_names()
    {
        await WorkflowRepoFiles.CreateAsync(_repo, WorkflowRepoFiles.Blank("Deps"), Library(), default);

        var result = await WorkflowRepoFiles.CreateAsync(_repo, Drafted, Library(_repo), default);

        result.Error.Code.ShouldBe("General.Conflict");
        result.Error.Description.ShouldBe("There's already a workflow called Deps. Pick another name.");
    }

    [Fact]
    public async Task a_drafted_workflow_with_errors_isnt_saved()
    {
        var result = await WorkflowRepoFiles.CreateAsync(_repo, Drafted.Replace("outcomes: [done]", "outcomes: []"), Library(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldStartWith("Line ");
        Directory.Exists(_folder).ShouldBeFalse();
    }
}
