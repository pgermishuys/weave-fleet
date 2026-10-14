using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Application.Tests.Sessions;

/// <summary>Skills named with <c>$</c> in a message: which names count, and what the model is told.</summary>
public sealed class SkillMentionsTests
{
    private static IReadOnlyList<string> Valid(string text, params string[] skills)
        => SkillMentions.Validate(text, skills).Value;

    [Fact]
    public void Names_come_back_in_the_order_they_appear_in_the_text_without_repeats()
    {
        Valid("first $b then $a and $b again", "a", "b", "a").ShouldBe(["b", "a"]);
    }

    [Fact]
    public void A_name_whose_token_isnt_in_the_text_is_dropped()
    {
        Valid("just $a here", "a", "b").ShouldBe(["a"]);
        SkillMentions.Validate("nothing", null).Value.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("$fleet-debug", true)]
    [InlineData("use $fleet-debug.", true)]
    [InlineData("use $fleet-debug, then", true)]
    [InlineData("($fleet-debug)", true)]
    [InlineData("\"$fleet-debug\"", true)]
    [InlineData("use $fleet-debug-2", false)]
    [InlineData("use $fleet-debug.md", false)]
    [InlineData("use $fleet-debug:x", false)]
    [InlineData("cost$fleet-debug", false)]
    public void A_token_counts_where_it_starts_a_word_and_isnt_the_start_of_a_longer_name(string text, bool counts)
    {
        Valid(text, "fleet-debug").Count.ShouldBe(counts ? 1 : 0);
    }

    [Fact]
    public void A_plugin_skill_name_with_a_colon_counts()
    {
        Valid("run $plugin:skill now", "plugin:skill").ShouldBe(["plugin:skill"]);
    }

    [Theory]
    [InlineData("-a")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("")]
    public void An_invalid_name_is_an_error(string name)
    {
        var result = SkillMentions.Validate("$a", [name]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("skill name");
    }

    [Fact]
    public void More_than_ten_skills_is_an_error()
    {
        var names = Enumerable.Range(0, 11).Select(i => $"s{i}").ToList();

        SkillMentions.Validate(string.Join(' ', names.Select(n => "$" + n)), names).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void The_note_names_the_skills()
    {
        SkillMentions.Note(["a"]).ShouldBe("The user named this skill: a. Load it with your skill tool before the step it's for.");
        SkillMentions.Note(["a", "b"]).ShouldBe("The user named these skills: a, b. Load each with your skill tool before the step it's for.");
    }
}
