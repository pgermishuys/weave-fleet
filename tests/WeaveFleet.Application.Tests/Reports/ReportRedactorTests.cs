using Shouldly;
using WeaveFleet.Application.Reports;

namespace WeaveFleet.Application.Tests.Reports;

public sealed class ReportRedactorTests
{
    [Fact]
    public void A_folder_gets_one_label_wherever_it_appears_and_its_files_keep_their_names()
    {
        var redactor = new ReportRedactor();
        redactor.AddFolder("/home/sam/source/acme-payments");

        redactor.Redact("It broke in ~/x and /home/sam/source/acme-payments/Invoices/Totals.cs, see acme-payments.")
            .ShouldBe("It broke in ~/x and ‹folder-1›/Invoices/Totals.cs, see ‹folder-1›.");
        redactor.Redact("cwd /home/sam/source/acme-payments").ShouldBe("cwd ‹folder-1›");
        redactor.Replacements.ShouldBe([new ReportRedactor.Replacement("‹folder-1›", "folder", "/home/sam/source/acme-payments")]);
    }

    [Fact]
    public void The_longest_known_value_wins_so_a_folder_beats_its_parent_and_the_home_folder()
    {
        var redactor = new ReportRedactor();
        redactor.AddUser("sam", "/home/sam");
        redactor.AddFolder("/home/sam/source");
        redactor.AddFolder("/home/sam/source/acme-payments");

        redactor.Redact("/home/sam/source/acme-payments /home/sam/source/other /home/sam/.config sam")
            .ShouldBe("‹folder-2› ‹folder-1›/other ‹home›/.config ‹user›");
    }

    [Fact]
    public void Windows_paths_are_found_with_either_slash_and_with_doubled_backslashes()
    {
        var redactor = new ReportRedactor();
        redactor.AddFolder(@"C:\Users\sam\src\acme");

        redactor.Redact(@"a C:\Users\sam\src\acme\x b c:/users/sam/src/acme/y c {""cwd"":""C:\\Users\\sam\\src\\acme""}")
            .ShouldBe(@"a ‹folder-1›\x b ‹folder-1›/y c {""cwd"":""‹folder-1›""}");
    }

    [Fact]
    public void Names_are_replaced_only_as_whole_words()
    {
        var redactor = new ReportRedactor();
        redactor.AddSessionTitle("Fix invoice rounding");
        redactor.AddBranch("fix/eu-rounding");
        redactor.AddMachine("sams-laptop");

        redactor.Redact("Session Fix invoice rounding on fix/eu-rounding at sams-laptop; Fix invoice roundings stays")
            .ShouldBe("Session ‹session-1› on ‹branch-1› at ‹machine-1›; Fix invoice roundings stays");
    }

    [Fact]
    public void Generic_names_are_left_alone()
    {
        var redactor = new ReportRedactor();
        redactor.AddFolder("/home/sam/src");
        redactor.AddBranch("main");
        redactor.AddSessionTitle("Untitled");
        redactor.AddUser("root", null);

        redactor.Redact("src main Untitled root").ShouldBe("src main Untitled root");
        redactor.Redact("/home/sam/src/x").ShouldBe("‹folder-1›/x");
    }

    [Fact]
    public void Known_secrets_are_replaced_and_shown_cut_short()
    {
        var redactor = new ReportRedactor();
        redactor.AddSecret("abcdef0123456789");

        redactor.Redact("token abcdef0123456789 used").ShouldBe("token ‹secret-1› used");
        redactor.Replacements.Single().Shown.ShouldBe("abcd…");
    }

    [Theory]
    [InlineData("auth ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa done", "auth ‹secret-1› done")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "Authorization: Bearer ‹secret-1›")]
    [InlineData("password=hunter2hunter2 next", "password=‹secret-1› next")]
    [InlineData("token: 48210", "token: 48210")]
    [InlineData("mail sam@example.com now", "mail ‹email-1› now")]
    [InlineData("host 192.168.1.20:6262 and 127.0.0.1", "host ‹address-1›:6262 and 127.0.0.1")]
    [InlineData("tailnet 100.101.2.3", "tailnet ‹address-1›")]
    public void Patterns_catch_what_Fleet_cannot_know(string text, string expected)
    {
        new ReportRedactor().Redact(text).ShouldBe(expected);
    }

    [Fact]
    public void The_same_found_value_keeps_its_label_across_texts()
    {
        var redactor = new ReportRedactor();

        redactor.Redact("a@b.io and c@d.io").ShouldBe("‹email-1› and ‹email-2›");
        redactor.Redact("again a@b.io").ShouldBe("again ‹email-1›");
        redactor.Counts["email"].ShouldBe(2);
    }

    [Fact]
    public void A_url_and_its_host_share_a_label_and_loopback_is_ignored()
    {
        var redactor = new ReportRedactor();
        redactor.AddUrl("https://fleet.example.net:6262/");
        redactor.AddUrl("http://127.0.0.1:6262");

        redactor.Redact("at https://fleet.example.net:6262/api or fleet.example.net, local http://127.0.0.1:6262")
            .ShouldBe("at ‹url-1›/api or ‹url-1›, local http://127.0.0.1:6262");
    }

    [Fact]
    public void Labels_already_in_the_text_are_not_replaced_again()
    {
        var redactor = new ReportRedactor();
        redactor.AddSessionTitle("session");
        redactor.AddFolder("/srv/folder");

        var once = redactor.Redact("/srv/folder session");
        once.ShouldBe("‹folder-1› ‹session-1›");
        redactor.Redact(once).ShouldBe(once);
    }

    [Fact]
    public void Folders_under_the_home_folder_are_found_written_with_a_tilde()
    {
        var redactor = new ReportRedactor();
        redactor.AddUser("sam", "/home/sam");
        redactor.AddFolder("/home/sam/work");
        redactor.AddFolder("/home/sam/work/alpha");

        redactor.Redact("in ~/work/alpha/src and ~/work/beta and ~/.config")
            .ShouldBe("in ‹folder-2›/src and ‹folder-1›/beta and ~/.config");
    }

    [Fact]
    public void Select_keeps_only_the_newest_entries_of_a_busy_session()
    {
        var entries = Enumerable.Range(0, FleetLogExcerpt.MaxEntries + 50)
            .Select(i => new FleetLogExcerpt.Entry(DateTime.UtcNow, "DBG", $"{i:D4} session s-123456"))
            .ToList();

        var excerpt = FleetLogExcerpt.Select(entries, ["s-123456"]);

        excerpt.Included.ShouldBe(FleetLogExcerpt.MaxEntries);
        excerpt.Text.ShouldStartWith("0050 ");
        excerpt.Text.ShouldEndWith("0449 session s-123456\n");
    }
}
