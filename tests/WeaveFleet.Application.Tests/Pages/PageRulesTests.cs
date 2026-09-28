using WeaveFleet.Application.Pages;

namespace WeaveFleet.Application.Tests.Pages;

public sealed class PageRulesTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("fleet-page-rules-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Theory]
    [InlineData("python3 -m http.server 8000")]
    [InlineData("python -m http.server $PORT --bind 127.0.0.1 --directory /tmp/mockups")]
    [InlineData("py -m http.server %PORT%")]
    [InlineData("python3 -u -m http.server")]
    [InlineData("cd site && python3 -m http.server $PORT")]
    [InlineData("npx serve -l $PORT")]
    [InlineData("npx --yes serve /tmp/mockups")]
    [InlineData("npx serve@14 -l 3000")]
    [InlineData("bunx serve .")]
    [InlineData("pnpm dlx serve")]
    [InlineData("npx http-server -p $PORT")]
    [InlineData("http-server .")]
    [InlineData("live-server --port=$PORT")]
    [InlineData("serve -s dist")]
    public void Commands_that_only_serve_files_are_file_servers(string command)
        => PageRules.IsStaticFileServer(command).ShouldBeTrue();

    [Theory]
    [InlineData("npm run dev")]
    [InlineData("npm run serve")]
    [InlineData("bun --hot server.ts")]
    [InlineData("dotnet watch --project src/Web")]
    [InlineData("python manage.py runserver $PORT")]
    [InlineData("python3 server.py")]
    [InlineData("uvicorn app:app --port $PORT")]
    [InlineData("npx vite")]
    [InlineData("vite preview --port $PORT")]
    [InlineData("npx serve-handler")]
    [InlineData("php -S localhost:$PORT")]
    public void App_commands_are_not_file_servers(string command)
        => PageRules.IsStaticFileServer(command).ShouldBeFalse();

    [Theory]
    [InlineData("""<script type="module" src="/src/main.ts"></script>""", "/src/main.ts")]
    [InlineData("""<script type=module src='./app.tsx?v=2'></script>""", "./app.tsx")]
    [InlineData("""<script src="components/App.vue"></script>""", "components/App.vue")]
    public void A_page_that_loads_source_needs_a_build(string html, string script)
        => PageRules.FindSourceScript(html).ShouldBe(script);

    [Fact]
    public void A_page_that_loads_javascript_needs_nothing()
        => PageRules.FindSourceScript("""<script src="app.js"></script><script type="module" src="https://cdn.jsdelivr.net/npm/x/+esm"></script>""").ShouldBeNull();

    [Fact]
    public void Links_from_the_root_or_out_of_the_folder_are_found_once_each()
    {
        const string html = """
            <link rel="stylesheet" href="/styles.css">
            <link rel="stylesheet" href="styles.css">
            <script src="../shared/app.js"></script>
            <img src="/styles.css">
            <script src="//cdn.example.com/lib.js"></script>
            <a href="https://example.com/">site</a>
            <a href="option-b.html">B</a>
            <a href="#top">top</a>
            """;

        PageRules.FindBrokenLinks(html).ShouldBe(["/styles.css", "../shared/app.js"]);
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("Cargo.toml")]
    [InlineData("Web.csproj")]
    [InlineData("App.slnx")]
    public void A_folder_with_a_project_file_is_a_project(string name)
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "index.html"), "<p>hi</p>");
        File.WriteAllText(Path.Combine(_folder.FullName, name), "");

        PageRules.FindProjectFile(_folder.FullName).ShouldBe(name);
    }

    [Fact]
    public void A_folder_of_web_files_is_not_a_project()
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "index.html"), "<p>hi</p>");
        File.WriteAllText(Path.Combine(_folder.FullName, "data.json"), "{}");

        PageRules.FindProjectFile(_folder.FullName).ShouldBeNull();
    }

    [Fact]
    public void Page_ids_are_random_and_checked()
    {
        var id = PageIds.New();

        PageIds.IsValid(id).ShouldBeTrue();
        id.ShouldNotBe(PageIds.New());
        PageIds.IsValid("pg_../../etc").ShouldBeFalse();
        PageIds.IsValid(id.ToUpperInvariant()).ShouldBeFalse();
    }
}
