using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;
using YamlDotNet.RepresentationModel;

namespace WeaveFleet.Application.Workflows;

/// <summary>What the parser says about a workflow file as it's edited.</summary>
/// <param name="Text">The file: as typed in the File view, or as the designer's draft is written.</param>
/// <param name="Draft">
/// The workflow the designer shows, or null when it can't show this text without losing some of it (see
/// <see cref="WorkflowCheck.Text"/>).
/// </param>
public sealed record WorkflowCheckResult(
    string Text,
    IReadOnlyList<WorkflowProblem> Errors,
    WorkflowDefinition? Draft,
    IReadOnlyList<WorkflowComment> Comments)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>An error at a line, and the step whose lines hold it (its index in the file), if any.</summary>
public sealed record WorkflowProblem(int Line, string Message, int? Step);

/// <summary>A comment in a workflow file: the text after the <c>#</c>, and its line.</summary>
public sealed record WorkflowComment(int Line, string Text);

/// <summary>
/// Checks a workflow file with the parser runs use, for the designer and the File view: its errors with their lines
/// and steps, the workflow the designer can show, and the comments a designer save would remove.
/// </summary>
public static class WorkflowCheck
{
    /// <summary>
    /// Checks text from the File view or a file on disk. The draft comes back only when the designer can show all of
    /// it: written out and read again, it gives the same errors. A step the parser had to drop (no id) or a key it
    /// doesn't know would change them, so that text stays in the File view.
    /// </summary>
    public static WorkflowCheckResult Text(string text, string file)
    {
        var parsed = WorkflowYaml.Parse(text, file);
        var draft = parsed.Draft;
        if (draft is not null && parsed.Errors.Count > 0)
        {
            var again = WorkflowYaml.Parse(WorkflowYamlWriter.Write(draft), file);
            if (!SameErrors(parsed.Errors, again.Errors))
                draft = null;
        }

        return new WorkflowCheckResult(text, Problems(text, parsed.Errors), draft, WorkflowComments.Find(text));
    }

    /// <summary>Writes the designer's draft in Fleet's layout, then checks what was written.</summary>
    public static WorkflowCheckResult Draft(WorkflowDefinition draft, string file)
    {
        var text = WorkflowYamlWriter.Write(draft);
        var parsed = WorkflowYaml.Parse(text, file);
        return new WorkflowCheckResult(text, Problems(text, parsed.Errors), draft, []);
    }

    private static bool SameErrors(IReadOnlyList<WorkflowFileError> first, IReadOnlyList<WorkflowFileError> second)
        => first.Select(e => e.Message).Order(StringComparer.Ordinal)
            .SequenceEqual(second.Select(e => e.Message).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Each error with the step whose lines hold it: from its first line to the next step's.</summary>
    private static List<WorkflowProblem> Problems(string text, IReadOnlyList<WorkflowFileError> errors)
    {
        var starts = StepStarts(text);
        return errors.Select(error =>
        {
            int? step = null;
            for (var i = 0; i < starts.Count && error.Line > 0; i++)
            {
                if (starts[i] <= error.Line)
                    step = i;
            }

            return new WorkflowProblem(error.Line, error.Message, step);
        }).ToList();
    }

    /// <summary>The line each step starts on, in order; empty when the text doesn't read.</summary>
    private static List<int> StepStarts(string text)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0
                || stream.Documents[0].RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("steps"), out var steps)
                || steps is not YamlSequenceNode sequence)
            {
                return [];
            }

            return sequence.Children.Select(node => (int)node.Start.Line).ToList();
        }
        catch (YamlException)
        {
            return [];
        }
    }
}

/// <summary>Finds a workflow file's comments with YamlDotNet's scanner, which knows a <c>#</c> in a string from one that starts a comment.</summary>
public static class WorkflowComments
{
    public static IReadOnlyList<WorkflowComment> Find(string text)
    {
        var comments = new List<WorkflowComment>();
        try
        {
            var scanner = new Scanner(new StringReader(text), skipComments: false);
            while (scanner.MoveNext())
            {
                if (scanner.Current is Comment comment)
                    comments.Add(new WorkflowComment((int)comment.Start.Line, comment.Value.Trim()));
            }
        }
        catch (YamlException)
        {
            // The parser reports it; the comments before it are still what a save would remove.
        }

        return comments;
    }
}
