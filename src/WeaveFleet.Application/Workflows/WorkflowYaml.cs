using System.Globalization;
using System.Text.RegularExpressions;
using WeaveFleet.Domain.Entities;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace WeaveFleet.Application.Workflows;

/// <summary>A workflow file read: the workflow, or what's wrong with the file.</summary>
public sealed record WorkflowParseResult(WorkflowDefinition? Definition, IReadOnlyList<WorkflowFileError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;

    /// <summary>The name the file gives, even when it has errors, so the library can list it by that.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Reads a workflow file. It walks YamlDotNet's node tree by hand rather than deserializing, so nothing is found by
/// reflection (Fleet's release builds are native AOT) and every node still has its line for the errors.
/// </summary>
public static partial class WorkflowYaml
{
    private const int MaxLoopLimit = 10;

    private static readonly HashSet<string> RootKeys = ["name", "description", "placeholder", "starts-from", "runs-in", "steps"];
    private static readonly HashSet<string> AgentKeys = ["id", "title", "agent", "model", "effort", "skill", "optional", "finish", "writes", "prompt", "outcomes", "on"];
    private static readonly HashSet<string> YouKeys = ["id", "title", "you", "choices"];
    private static readonly HashSet<string> ChoiceKeys = ["to", "note"];
    private static readonly HashSet<string> Variables = ["request", "slug", "previous.summary", "previous.files", "run.branch", "run.base"];

    /// <summary>The variables a declared file's path can use: the ones a run knows before any step starts.</summary>
    private static readonly HashSet<string> PathVariables = ["slug", "run.branch"];

    /// <summary>Reads <paramref name="text"/>; <paramref name="file"/> names the file in errors.</summary>
    public static WorkflowParseResult Parse(string text, string file)
    {
        var errors = new List<WorkflowFileError>();
        void Error(YamlNode? node, string message) => errors.Add(new WorkflowFileError(file, LineOf(node), message));

        YamlNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0)
                return Fail(file, 0, "The file is empty.");
            root = stream.Documents[0].RootNode;
        }
        catch (YamlException ex)
        {
            return Fail(file, (int)ex.Start.Line, $"This isn't valid YAML: {Plain(ex)}");
        }

        if (root is not YamlMappingNode map)
            return Fail(file, LineOf(root), "A workflow file is a mapping with name, starts-from, runs-in and steps.");

        CheckKeys(map, RootKeys, "a workflow", Error);

        var name = Scalar(map, "name", Error);
        if (string.IsNullOrWhiteSpace(name))
            Error(map, "The workflow needs a name.");

        var startsFrom = Scalar(map, "starts-from", Error) ?? WorkflowStarts.Sentence;
        if (startsFrom != WorkflowStarts.Sentence)
        {
            Error(Child(map, "starts-from"), startsFrom is "issue" or "pr"
                ? $"This version of Fleet can only start a workflow from a sentence, not from {(startsFrom == "pr" ? "a pull request" : "an issue")}."
                : $"starts-from is \"{startsFrom}\"; use sentence.");
        }

        var runsIn = Scalar(map, "runs-in", Error) ?? WorkflowPlaces.NewWorktree;
        if (runsIn != WorkflowPlaces.NewWorktree)
            Error(Child(map, "runs-in"), $"runs-in is \"{runsIn}\"; this version of Fleet runs workflows in a new-worktree only.");

        var steps = new List<WorkflowStep>();
        var stepNodes = new List<(WorkflowStep Step, YamlMappingNode Node)>();
        if (Child(map, "steps") is not YamlSequenceNode sequence || sequence.Children.Count == 0)
        {
            Error(Child(map, "steps") ?? map, "The workflow needs a list of steps.");
        }
        else
        {
            foreach (var node in sequence.Children)
            {
                if (node is not YamlMappingNode stepMap)
                {
                    Error(node, "A step is a mapping with at least an id.");
                    continue;
                }

                if (ReadStep(stepMap, Error) is { } step)
                {
                    steps.Add(step);
                    stepNodes.Add((step, stepMap));
                }
            }
        }

        CheckSteps(steps, stepNodes, Error);

        var definition = new WorkflowDefinition(
            name?.Trim() ?? string.Empty,
            Scalar(map, "description", Error)?.Trim(),
            Scalar(map, "placeholder", Error)?.Trim(),
            startsFrom,
            runsIn,
            steps);
        return new WorkflowParseResult(errors.Count == 0 ? definition : null, errors) { Name = definition.Name.Length > 0 ? definition.Name : null };
    }

    private static WorkflowStep? ReadStep(YamlMappingNode map, Action<YamlNode?, string> error)
    {
        var id = Scalar(map, "id", error);
        if (string.IsNullOrWhiteSpace(id))
        {
            error(map, "Every step needs an id.");
            return null;
        }

        if (!IdPattern().IsMatch(id))
        {
            error(Child(map, "id"), $"\"{id}\" can't be a step id: use lowercase letters, digits and dashes.");
            return null;
        }

        if (id == WorkflowTargets.End)
        {
            error(Child(map, "id"), "\"end\" can't be a step id: it's where a run finishes.");
            return null;
        }

        foreach (var kind in new[] { "parallel", "wait-for" })
        {
            if (Child(map, kind) is { } unsupported)
            {
                error(unsupported, $"{(kind == "parallel" ? "Parallel" : "Wait")} steps aren't in this version of Fleet.");
                return null;
            }
        }

        var line = LineOf(map);
        var title = Scalar(map, "title", error)?.Trim();

        if (Child(map, "you") is { } youNode)
        {
            CheckKeys(map, YouKeys, $"a You step ({id})", error);
            var ask = Scalar(map, "you", error)?.Trim();
            if (string.IsNullOrEmpty(ask))
                error(youNode, $"{id}: say what to ask, e.g. you: Build it this way?");

            var choices = new List<WorkflowChoice>();
            if (Child(map, "choices") is not YamlMappingNode choiceMap || choiceMap.Children.Count == 0)
            {
                error(Child(map, "choices") ?? map, $"{id} needs choices, each leading to a step or to end.");
            }
            else
            {
                foreach (var (labelNode, valueNode) in choiceMap.Children)
                {
                    var label = (labelNode as YamlScalarNode)?.Value?.Trim();
                    if (string.IsNullOrEmpty(label))
                    {
                        error(labelNode, "A choice needs a label.");
                        continue;
                    }

                    switch (valueNode)
                    {
                        case YamlScalarNode { Value: { Length: > 0 } to }:
                            choices.Add(new WorkflowChoice(label, to.Trim(), Note: false));
                            break;
                        case YamlMappingNode choice:
                            CheckKeys(choice, ChoiceKeys, $"the choice \"{label}\"", error);
                            var target = Scalar(choice, "to", error)?.Trim();
                            if (string.IsNullOrEmpty(target))
                            {
                                error(choice, $"The choice \"{label}\" needs to: the step it leads to, or end.");
                                continue;
                            }

                            choices.Add(new WorkflowChoice(label, target, Bool(choice, "note", error) ?? false));
                            break;
                        default:
                            error(valueNode, $"The choice \"{label}\" needs the step it leads to, or end.");
                            break;
                    }
                }
            }

            return new WorkflowYouStep(id, string.IsNullOrEmpty(title) ? "You decide" : title, line, ask ?? string.Empty, choices);
        }

        CheckKeys(map, AgentKeys, $"an agent step ({id})", error);
        if (string.IsNullOrEmpty(title))
            error(map, $"{id} needs a title.");

        var model = Scalar(map, "model", error)?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            error(map, $"{id} needs a model: strong, standard, fast, or an exact provider/model.");
            model = WorkflowRoles.Standard;
        }
        else if (!WorkflowRoles.IsRole(model) && !IsExactModel(model))
        {
            error(Child(map, "model"), $"{id}'s model is \"{model}\": use strong, standard, fast, or an exact provider/model.");
        }

        var prompt = Scalar(map, "prompt", error);
        if (string.IsNullOrWhiteSpace(prompt))
            error(map, $"{id} needs a prompt: what the agent should do.");

        bool optional = false;
        string? optionalHint = null;
        if (Child(map, "optional") is YamlScalarNode { Value: { } optionalValue } optionalNode)
        {
            if (bool.TryParse(optionalValue, out var flag))
                optional = flag;
            else if (!string.IsNullOrWhiteSpace(optionalValue))
                (optional, optionalHint) = (true, optionalValue.Trim());
            else
                error(optionalNode, $"{id}: optional is true, false, or when to switch it on.");
        }
        else if (Child(map, "optional") is { } badOptional)
        {
            error(badOptional, $"{id}: optional is true, false, or when to switch it on.");
        }

        string? finish = null;
        switch (Scalar(map, "finish", error)?.Trim())
        {
            case null:
                break;
            case (WorkflowFinishers.Agent or WorkflowFinishers.You) and var said:
                finish = said;
                break;
            case var other:
                error(Child(map, "finish"), $"{id}: finish is \"{other}\"; use you (you move the step on) or agent (the agent does).");
                break;
        }

        var writes = new List<string>();
        if (Child(map, "writes") is { } writesNode)
        {
            if (writesNode is not YamlSequenceNode writeList || writeList.Children.Count == 0)
            {
                error(writesNode, $"{id}: writes is a list of files, e.g. writes: [docs/design/{{{{slug}}}}.md].");
            }
            else
            {
                foreach (var node in writeList.Children)
                {
                    var path = (node as YamlScalarNode)?.Value?.Trim();
                    if (CheckDeclaredFile(id, path) is { } problem)
                        error(node, problem);
                    else if (writes.Contains(path!))
                        error(node, $"{id} declares {path} twice.");
                    else
                        writes.Add(path!);
                }
            }
        }

        var outcomes = new List<string>();
        if (Child(map, "outcomes") is not YamlSequenceNode outcomeList || outcomeList.Children.Count == 0)
        {
            error(Child(map, "outcomes") ?? map, $"{id} needs outcomes, e.g. outcomes: [done]. The agent reports one of them when it's finished.");
        }
        else
        {
            foreach (var node in outcomeList.Children)
            {
                var outcome = (node as YamlScalarNode)?.Value?.Trim();
                if (string.IsNullOrEmpty(outcome) || !IdPattern().IsMatch(outcome))
                    error(node, $"{id}: an outcome is a word in lowercase letters, digits and dashes.");
                else if (outcomes.Contains(outcome))
                    error(node, $"{id} lists the outcome \"{outcome}\" twice.");
                else
                    outcomes.Add(outcome);
            }
        }

        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        int? max = null;
        if (Child(map, "on") is { } onNode)
        {
            if (onNode is not YamlMappingNode on)
            {
                error(onNode, $"{id}: on maps outcomes to the step they lead to, e.g. on: {{ changes: implement, max: 2 }}.");
            }
            else
            {
                foreach (var (keyNode, valueNode) in on.Children)
                {
                    var key = (keyNode as YamlScalarNode)?.Value?.Trim() ?? string.Empty;
                    var value = (valueNode as YamlScalarNode)?.Value?.Trim();
                    if (key == "max")
                    {
                        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= MaxLoopLimit)
                            max = limit;
                        else
                            error(valueNode, $"{id}: max is a number from 1 to {MaxLoopLimit}.");
                    }
                    else if (!outcomes.Contains(key))
                    {
                        error(keyNode, $"{id}: \"{key}\" isn't one of its outcomes ({string.Join(", ", outcomes)}).");
                    }
                    else if (string.IsNullOrEmpty(value))
                    {
                        error(valueNode, $"{id}: say which step {key} leads to, or end.");
                    }
                    else
                    {
                        routes[key] = value;
                    }
                }
            }
        }

        return new WorkflowAgentStep(
            id,
            title ?? id,
            line,
            Scalar(map, "agent", error)?.Trim() is { Length: > 0 } agent ? agent : null,
            model,
            Scalar(map, "effort", error)?.Trim() is { Length: > 0 } effort ? effort : null,
            Scalar(map, "skill", error)?.Trim() is { Length: > 0 } skill ? skill : null,
            optional,
            optionalHint,
            prompt ?? string.Empty,
            outcomes,
            routes,
            max,
            finish,
            writes);
    }

    /// <summary>What's wrong with a declared file's path, or null: it stays inside the run's worktree.</summary>
    private static string? CheckDeclaredFile(string id, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return $"{id}: a declared file is a path in the run's worktree, e.g. docs/design/{{{{slug}}}}.md.";
        if (path.Contains('\\'))
            return $"{id}: write {path} with forward slashes.";
        if (path.StartsWith('/') || path.StartsWith('~') || (path.Length > 1 && path[1] == ':'))
            return $"{id}: {path} isn't in the run's worktree. Declare it relative to the worktree, e.g. docs/design/{{{{slug}}}}.md.";
        if (path.Split('/').Any(part => part is ".." or "." || part.Length == 0) || path.EndsWith('/'))
            return $"{id}: {path} has to name a file inside the run's worktree, without . or .. in it.";

        foreach (Match match in VariablePattern().Matches(path))
        {
            var variable = match.Groups[1].Value;
            if (!PathVariables.Contains(variable))
                return $"{id} declares {path}, which uses {{{{{variable}}}}}. A declared file can use {{{{slug}}}} and {{{{run.branch}}}}.";
        }

        return null;
    }

    /// <summary>Checks what depends on the whole list: ids, where outcomes and choices lead, loops and variables.</summary>
    private static void CheckSteps(
        List<WorkflowStep> steps,
        List<(WorkflowStep Step, YamlMappingNode Node)> nodes,
        Action<YamlNode?, string> error)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (step, node) in nodes)
        {
            if (!ids.Add(step.Id))
                error(Child(node, "id"), $"Two steps have the id \"{step.Id}\".");
        }

        if (steps.Count > 0 && !steps.OfType<WorkflowAgentStep>().Any())
            error(null, "A workflow needs at least one agent step.");

        int IndexOf(string id) => steps.FindIndex(s => s.Id == id);

        for (var i = 0; i < nodes.Count; i++)
        {
            var (step, node) = nodes[i];
            var index = IndexOf(step.Id);

            switch (step)
            {
                case WorkflowAgentStep agent:
                {
                    var loops = false;
                    foreach (var (outcome, target) in agent.Routes)
                    {
                        if (target == WorkflowTargets.End)
                            continue;
                        var to = IndexOf(target);
                        if (to < 0)
                            error(Child(node, "on"), $"{step.Id}: \"{target}\" isn't a step in this workflow.");
                        else if (to <= index)
                            loops = true;
                    }

                    if (loops && agent.MaxLoops is null)
                        error(Child(node, "on"), $"{step.Id} sends work back to an earlier step, so it needs a max: how many times it may do that in a run.");
                    else if (!loops && agent.MaxLoops is not null)
                        error(Child(node, "on"), $"{step.Id}: max only applies to an outcome that goes back to an earlier step.");

                    foreach (Match match in VariablePattern().Matches(agent.Prompt))
                    {
                        var variable = match.Groups[1].Value;
                        var stepVariable = StepVariablePattern().Match(variable);
                        if (stepVariable.Success)
                        {
                            if (IndexOf(stepVariable.Groups[1].Value) < 0)
                                error(Child(node, "prompt"), $"{step.Id}'s prompt uses {{{{{variable}}}}}, but there's no step \"{stepVariable.Groups[1].Value}\".");
                        }
                        else if (!Variables.Contains(variable))
                        {
                            error(Child(node, "prompt"), $"{step.Id}'s prompt uses {{{{{variable}}}}}. Fleet fills in {{{{request}}}}, {{{{slug}}}}, {{{{previous.summary}}}}, {{{{previous.files}}}}, {{{{steps.<id>.summary}}}}, {{{{steps.<id>.files}}}}, {{{{run.branch}}}} and {{{{run.base}}}}.");
                        }
                    }

                    break;
                }

                case WorkflowYouStep you:
                    foreach (var choice in you.Choices)
                    {
                        if (choice.To != WorkflowTargets.End && IndexOf(choice.To) < 0)
                            error(Child(node, "choices"), $"{step.Id}: \"{choice.To}\" isn't a step in this workflow.");
                    }

                    break;
            }
        }
    }

    /// <summary>The variables a prompt may use, e.g. <c>request</c> or <c>steps.review.summary</c>.</summary>
    public static IEnumerable<string> VariablesIn(string prompt)
        => VariablePattern().Matches(prompt).Select(match => match.Groups[1].Value);

    /// <summary>
    /// Fills in a prompt's variables; <paramref name="lookup"/> gives each one's value, or null for none. A line whose
    /// variables are all empty is left out, so "Read {{previous.files}} first." goes when there are none; a line with
    /// no variables always stays.
    /// </summary>
    public static string Fill(string prompt, Func<string, string?> lookup)
    {
        var lines = new List<string>();
        foreach (var line in prompt.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = VariablePattern().Matches(line);
            if (matches.Count == 0)
            {
                lines.Add(line);
                continue;
            }

            var values = matches.Select(match => lookup(match.Groups[1].Value)).ToList();
            if (values.All(string.IsNullOrEmpty))
                continue;

            var index = 0;
            lines.Add(VariablePattern().Replace(line, _ => values[index++] ?? string.Empty));
        }

        // A line left out leaves blank lines behind; keep at most one in a row.
        return ExtraBlankLines().Replace(string.Join('\n', lines).Trim(), "\n\n");
    }

    /// <summary>The <c>id</c> in <c>steps.id.summary</c>, or null for any other variable.</summary>
    public static string? StepOfSummaryVariable(string variable)
        => StepVariablePattern().Match(variable) is { Success: true } match && match.Groups[2].Value == "summary" ? match.Groups[1].Value : null;

    /// <summary>The <c>id</c> in <c>steps.id.files</c>, or null for any other variable.</summary>
    public static string? StepOfFilesVariable(string variable)
        => StepVariablePattern().Match(variable) is { Success: true } match && match.Groups[2].Value == "files" ? match.Groups[1].Value : null;

    /// <summary>A step's declared files with the run filled in, e.g. docs/design/keyboard-sheet.md.</summary>
    public static IReadOnlyList<string> FilesOf(WorkflowAgentStep step, Func<string, string?> lookup)
        => step.Writes.Select(path => VariablePattern().Replace(path, match => lookup(match.Groups[1].Value) ?? string.Empty)).ToList();

    /// <summary>"a.md", "a.md and b.html", "a.md, b.html and c.css".</summary>
    public static string ListFiles(IReadOnlyList<string> files) => files.Count switch
    {
        0 => string.Empty,
        1 => files[0],
        _ => $"{string.Join(", ", files.Take(files.Count - 1))} and {files[^1]}",
    };

    internal static bool IsExactModel(string model)
    {
        var slash = model.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < model.Length - 1 && !model.Any(char.IsWhiteSpace);
    }

    private static void CheckKeys(YamlMappingNode map, HashSet<string> known, string what, Action<YamlNode?, string> error)
    {
        foreach (var key in map.Children.Keys)
        {
            var name = (key as YamlScalarNode)?.Value;
            if (name is null || !known.Contains(name))
                error(key, $"\"{name}\" isn't something {what} can have.");
        }
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
        => map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string? Scalar(YamlMappingNode map, string key, Action<YamlNode?, string> error)
    {
        switch (Child(map, key))
        {
            case null:
                return null;
            case YamlScalarNode scalar:
                return scalar.Value;
            case var other:
                error(other, $"{key} should be text.");
                return null;
        }
    }

    private static bool? Bool(YamlMappingNode map, string key, Action<YamlNode?, string> error)
    {
        var value = Scalar(map, key, error);
        if (value is null)
            return null;
        if (bool.TryParse(value, out var flag))
            return flag;
        error(Child(map, key), $"{key} is true or false.");
        return null;
    }

    private static int LineOf(YamlNode? node) => node is null ? 0 : (int)node.Start.Line;

    private static WorkflowParseResult Fail(string file, int line, string message)
        => new(null, [new WorkflowFileError(file, line, message)]);

    /// <summary>YamlDotNet's message without its "(Line: …)" position, which the error already names.</summary>
    private static string Plain(YamlException ex)
    {
        var message = (ex.InnerException as YamlException)?.Message ?? ex.Message;
        return PositionSuffix().Replace(message, string.Empty).Trim();
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.-]+)\s*\}\}")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^steps\.([a-z0-9][a-z0-9-]*)\.(summary|files)$")]
    private static partial Regex StepVariablePattern();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"^\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\):\s*")]
    private static partial Regex PositionSuffix();
}
