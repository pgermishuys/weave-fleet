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
}
