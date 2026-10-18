using System.Text;
using WeaveFleet.Application.Pages;

namespace WeaveFleet.Application.Tests.Pages;

public sealed class PageThemeTests
{
    private static string Inject(string html) => Encoding.UTF8.GetString(PageTheme.Inject(Encoding.UTF8.GetBytes(html)));

    [Fact]
    public void The_theme_goes_at_the_top_of_the_head_so_the_page_s_own_styles_come_after_it()
    {
        var page = Inject("<!doctype html><html lang=\"en\"><HEAD data-x><title>T</title><style>p{}</style></head><body><p>Hi</p></body></html>");

        page.ShouldStartWith("<!doctype html><html lang=\"en\"><HEAD data-x>" + PageTheme.Bootstrap + "<title>T</title>");
    }

    [Fact]
    public void Without_a_head_it_goes_after_the_html_tag_and_never_before_the_doctype()
    {
        Inject("<!DOCTYPE html>\n<html><body><p>Hi</p></body></html>")
            .ShouldStartWith("<!DOCTYPE html>\n<html>" + PageTheme.Bootstrap + "<body>");
        Inject("<!doctype html><title>T</title><p>Hi</p>")
            .ShouldBe("<!doctype html>" + PageTheme.Bootstrap + "<title>T</title><p>Hi</p>");
        Inject("<p>Hi</p>").ShouldBe(PageTheme.Bootstrap + "<p>Hi</p>");
    }

    [Fact]
    public void Tags_that_only_start_like_head_or_a_head_in_the_body_are_not_the_head()
    {
        Inject("<!doctype html><html><header>x</header></html>")
            .ShouldStartWith("<!doctype html><html>" + PageTheme.Bootstrap + "<header>");
        Inject("<!doctype html><body><pre>&lt;head&gt; <head></pre></body>")
            .ShouldStartWith("<!doctype html>" + PageTheme.Bootstrap + "<body>");
    }

    [Fact]
    public void A_byte_order_mark_stays_first_and_utf16_pages_are_served_as_they_are()
    {
        var withBom = PageTheme.Inject([0xEF, 0xBB, 0xBF, .. "<p>Hi</p>"u8]);
        withBom[..3].ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(withBom[3..]).ShouldBe(PageTheme.Bootstrap + "<p>Hi</p>");

        byte[] utf16 = [0xFF, 0xFE, (byte)'<', 0, (byte)'p', 0, (byte)'>', 0];
        PageTheme.Inject(utf16).ShouldBe(utf16);
    }

    [Fact]
    public void The_page_s_own_bytes_are_kept_whatever_its_encoding()
    {
        byte[] latin1 = [.. "<html><head></head><body>caf"u8, 0xE9, .. "</body></html>"u8];

        var page = PageTheme.Inject(latin1);

        page.Length.ShouldBe(latin1.Length + Encoding.UTF8.GetByteCount(PageTheme.Bootstrap));
        page[^15].ShouldBe((byte)0xE9);
    }

    [Fact]
    public void The_defaults_are_every_variable_the_tool_tells_agents_about()
    {
        var description = WeaveFleet.Application.FleetTools.FleetToolCatalog.Find("fleet_page_show")!.Description;

        foreach (var (name, _) in PageTheme.Defaults.Where(pair => !pair.Key.StartsWith("--fleet-chart-", StringComparison.Ordinal)))
            description.ShouldContain(name);
        description.ShouldContain("--fleet-chart-1 to --fleet-chart-5");
        PageTheme.Bootstrap.ShouldContain(":root{--fleet-bg:#141418;");
    }

    [Fact]
    public void A_page_that_uses_fleet_s_variables_gets_fleet_s_ground_in_a_tab_too_and_others_only_the_variables()
    {
        Inject("<style>h1{color:var(--fleet-text)}</style><h1>Times</h1>").ShouldStartWith(PageTheme.ThemedBootstrap);
        Inject("<style>h1{color:#222}</style><h1>Times</h1>").ShouldStartWith(PageTheme.Bootstrap);
        PageTheme.ThemedBootstrap.ShouldContain("const themed = true;");
        PageTheme.Bootstrap.ShouldContain("const themed = false;");
    }

    [Theory]
    [InlineData("index.html", true)]
    [InlineData("old.HTM", true)]
    [InlineData("styles.css", false)]
    [InlineData("chart.js", false)]
    public void Only_html_files_get_the_theme(string file, bool themed) => PageTheme.Themes(file).ShouldBe(themed);

    private const string ArchifyHead = "<meta charset=\"utf-8\"><meta name=\"generator\" content=\"archify 3.1.0\"><style>:root{--bg:#000}</style>";

    [Fact]
    public void An_archify_page_gets_the_themed_bootstrap_and_the_layer_after_its_own_styles()
    {
        var page = Inject("<!doctype html><html><head>" + ArchifyHead + "</head><body><svg></svg></body></html>");

        page.ShouldStartWith("<!doctype html><html><head>" + PageTheme.ThemedBootstrap + "<meta charset");
        page.ShouldContain("<style>:root{--bg:#000}</style>" + ArchifyTheme.Layer + "</head><body>");
        page.Split("id=\"fleet-archify\"").Length.ShouldBe(2);
        ArchifyTheme.Layer.ShouldContain("--bg: var(--fleet-bg);");
        ArchifyTheme.Layer.ShouldContain(".toolbar");
    }

    [Fact]
    public void A_page_that_is_not_archify_s_is_served_without_the_layer_even_if_it_mentions_archify()
    {
        var page = Inject("<html><head><title>T</title></head><body><p>&lt;meta name=\"generator\" content=\"archify\"&gt;</p></body></html>");

        page.ShouldNotContain("fleet-archify");
        page.ShouldStartWith("<html><head>" + PageTheme.Bootstrap + "<title>T");
        Inject("<html><head><meta name=\"generator\" content=\"hugo\"></head><body></body></html>").ShouldNotContain("fleet-archify");
        Inject("<html><head></head><body><meta name=\"generator\" content=\"archify 3\"></body></html>").ShouldNotContain("fleet-archify");
    }

    [Fact]
    public void An_archify_page_without_a_closing_head_gets_the_layer_before_the_body_and_without_a_body_none()
    {
        Inject("<html><head>" + ArchifyHead + "<body><p>Hi</p></body></html>")
            .ShouldContain("--bg:#000}</style>" + ArchifyTheme.Layer + "<body>");

        var bare = Inject("<html><head>" + ArchifyHead);
        bare.ShouldNotContain("fleet-archify");
        bare.ShouldStartWith("<html><head>" + PageTheme.ThemedBootstrap);
    }

    [Fact]
    public void The_layer_goes_before_the_real_head_end_and_keeps_the_bytes_of_the_page()
    {
        byte[] latin1 = [.. "<html><head>"u8, .. Encoding.UTF8.GetBytes(ArchifyHead), .. "<script>x='</head>'</script></head><body>caf"u8, 0xE9, .. "</body></html>"u8];

        var page = Encoding.Latin1.GetString(PageTheme.Inject(latin1));

        page.ShouldContain("x='</head>'</script>" + ArchifyTheme.Layer + "</head><body>caf\u00e9");
    }
}
