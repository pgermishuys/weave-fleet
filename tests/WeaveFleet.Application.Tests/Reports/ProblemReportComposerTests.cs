using Shouldly;
using WeaveFleet.Application.Reports;

namespace WeaveFleet.Application.Tests.Reports;

public sealed class ProblemReportComposerTests
{
    private static PrepareReportRequest Request(string description) => new("bug", description, null, null, null, null);

    [Fact]
    public void Harness_names_are_Fleets_own_text_and_survive_a_folder_or_session_named_like_them()
    {
        var redactor = new ReportRedactor();
        redactor.AddFolder("/work/OpenCode");
        redactor.AddSessionTitle("Claude Code");
        redactor.AddSessionTitle("GitHub Copilot");
        var facts = new ReportFacts
        {
            Harnesses =
            [
                new ReportFact("OpenCode", "1.18.33"),
                new ReportFact("Claude Code", "2.1.312"),
                new ReportFact("GitHub Copilot", "0.1.0"),
            ],
        };

        var report = ProblemReportComposer.Compose(Request("it broke"), facts, redactor, canSend: true);

        report.Body.ShouldContain("- **OpenCode:** 1.18.33");
        report.Body.ShouldContain("- **Claude Code:** 2.1.312");
        report.Body.ShouldContain("- **GitHub Copilot:** 0.1.0");
    }

    [Fact]
    public void A_private_title_with_a_harness_name_is_still_redacted_in_the_description()
    {
        var redactor = new ReportRedactor();
        redactor.AddSessionTitle("Claude Code");

        var report = ProblemReportComposer.Compose(Request("see Claude Code here"), new ReportFacts(), redactor, canSend: true);

        report.Body.ShouldNotContain("Claude Code");
    }
}
