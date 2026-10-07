namespace Rexo.Ui;

using Spectre.Console;
using Rexo.Core.Models;

public static class ConsoleRenderer
{
    public static void ConfigureColors(bool? enabled)
    {
        var colorSystem = ColorSystemSupport.Detect;
        if (enabled.HasValue)
        {
            colorSystem = enabled.Value
                ? ColorSystemSupport.TrueColor
                : ColorSystemSupport.NoColors;
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            colorSystem = ColorSystemSupport.NoColors;
        }

        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            ColorSystem = colorSystem,
        });
    }

    public static void RenderCommandResult(CommandResult result)
    {
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(result.Message ?? "Success")}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Message ?? "Failed")}");
        }
    }

    public static void RenderStepResults(IReadOnlyList<StepResult> steps)
    {
        if (steps.Count == 0) return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Step")
            .AddColumn("Status")
            .AddColumn("Exit Code")
            .AddColumn("Duration");

        foreach (var step in steps)
        {
            var status = step.Success
                ? "[green]passed[/]"
                : step.Outputs.ContainsKey("skipped") ? "[grey]skipped[/]" : "[red]failed[/]";

            table.AddRow(
                Markup.Escape(step.StepId),
                status,
                step.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{step.Duration.TotalMilliseconds:F0}ms");
        }

        AnsiConsole.Write(table);
    }

    public static void RenderVersion(string version)
    {
        AnsiConsole.Write(new FigletText("rexo")
            .Color(Color.Blue));

        AnsiConsole.MarkupLine($"[bold]Version:[/] [cyan]{Markup.Escape(version)}[/]");
    }

    public static void RenderDoctorResult(CommandResult result)
    {
        var lines = (result.Message ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("[OK]", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(line)}[/]");
            }
            else if (line.Contains("[FAIL]", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(line)}[/]");
            }
            else
            {
                AnsiConsole.WriteLine(line);
            }
        }
    }

    public static void RenderList(CommandResult result)
    {
        var lines = (result.Message ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Built-in", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Config-defined", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Aliases", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[bold yellow]{Markup.Escape(line)}[/]");
            }
            else if (line.TrimStart().StartsWith("->", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
            }
            else
            {
                AnsiConsole.WriteLine(line);
            }
        }
    }

    public static void RenderExplain(CommandResult result)
    {
        var lines = (result.Message ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Command:", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[bold cyan]{Markup.Escape(line)}[/]");
            }
            else if (line.TrimStart().StartsWith("run:", StringComparison.OrdinalIgnoreCase) ||
                     line.TrimStart().StartsWith("uses:", StringComparison.OrdinalIgnoreCase) ||
                     line.TrimStart().StartsWith("command:", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"  [green]{Markup.Escape(line.TrimStart())}[/]");
            }
            else
            {
                AnsiConsole.WriteLine(line);
            }
        }
    }

    public static void RenderError(string message)
    {
        AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(message)}");
    }

    public static void RenderInfo(string message)
    {
        AnsiConsole.MarkupLine($"[blue]Info:[/] {Markup.Escape(message)}");
    }

    public static void RenderRunManifest(RunManifest manifest)
    {
        var status = manifest.Success ? "[green]SUCCESS[/]" : "[red]FAILED[/]";
        AnsiConsole.MarkupLine($"[bold]Run complete:[/] {status}");
        AnsiConsole.MarkupLine($"  Command: [cyan]{Markup.Escape(manifest.CommandExecuted ?? "unknown")}[/]");
        AnsiConsole.MarkupLine($"  Duration: {manifest.Duration.TotalSeconds:F1}s");

        if (manifest.Version is not null)
        {
            AnsiConsole.MarkupLine($"  Version: [cyan]{Markup.Escape(manifest.Version.SemVer)}[/]");
        }

        if (manifest.Steps.Count > 0)
        {
            RenderStepResults(manifest.Steps.Select(s =>
                new StepResult(s.StepId, s.Success, s.ExitCode,
                    TimeSpan.FromMilliseconds(s.DurationMs),
                    new Dictionary<string, object?>())).ToArray());
        }
    }

    /// <summary>
    /// Presents an interactive selection prompt and returns the chosen command name,
    /// or <c>null</c> if the user cancelled or no commands are available.
    /// </summary>
    public static string? PromptCommandPicker(IReadOnlyList<string> commandNames)
    {
        if (commandNames.Count == 0) return null;

        var choices = new List<string>(commandNames) { "(cancel)" };

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold yellow]Select a command to run:[/]")
                .PageSize(20)
                .HighlightStyle(new Style(foreground: Color.Cyan1))
                .AddChoices(choices));

        return selection == "(cancel)" ? null : selection;
    }

    /// <summary>
    /// Presents an enhanced interactive command picker that shows command descriptions.
    /// Returns the chosen command name, or <c>null</c> if cancelled.
    /// </summary>
    public static string? PromptCommandPickerWithDescriptions(
        IReadOnlyList<(string Name, string? Description)> commands)
    {
        if (commands.Count == 0) return null;

        const string cancelEntry = "(cancel)";
        const int nameColumnWidth = 24;

        var choices = commands
            .Select(c =>
            {
                var paddedName = c.Name.PadRight(nameColumnWidth);
                var desc = c.Description is not null
                    ? $"[grey]— {Markup.Escape(c.Description)}[/]"
                    : string.Empty;
                return $"[cyan]{Markup.Escape(paddedName)}[/] {desc}";
            })
            .ToList();

        choices.Add(cancelEntry);

        // Use a mapping so we can return the original (unformatted) name
        var displayToName = commands
            .Zip(choices, (cmd, display) => (display, cmd.Name))
            .ToDictionary(x => x.display, x => x.Name);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold yellow]Select a command to run:[/]")
                .PageSize(20)
                .HighlightStyle(new Style(foreground: Color.Yellow))
                .AddChoices(choices));

        if (selection == cancelEntry) return null;
        return displayToName.TryGetValue(selection, out var name) ? name : null;
    }

    /// <summary>
    /// Presents an interactive project picker when multiple config roots are detected.
    /// Returns the selected project directory, or <c>null</c> if cancelled.
    /// </summary>
    public static string? PromptProjectPicker(IReadOnlyList<string> projectRoots, string currentWorkingDir)
    {
        if (projectRoots.Count <= 1) return null;

        const string cancelEntry = "(cancel)";
        var ordered = projectRoots
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var currentName = Path.GetFileName(currentWorkingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var choices = ordered
            .Select(path =>
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var isCurrent = string.Equals(path, currentWorkingDir, StringComparison.OrdinalIgnoreCase);
                var suffix = isCurrent ? " [grey](current)[/]" : string.Empty;
                return $"[cyan]{Markup.Escape(name)}[/] [grey]- {Markup.Escape(path)}[/]{suffix}";
            })
            .ToList();
        choices.Add(cancelEntry);

        var displayToPath = ordered
            .Zip(choices.Take(ordered.Length), (path, display) => (display, path))
            .ToDictionary(x => x.display, x => x.path, StringComparer.Ordinal);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold yellow]Select a project root (current: {Markup.Escape(currentName)}):[/]")
                .PageSize(12)
                .HighlightStyle(new Style(foreground: Color.Cyan1))
                .AddChoices(choices));

        if (selection == cancelEntry) return null;
        return displayToPath.TryGetValue(selection, out var selectedPath) ? selectedPath : null;
    }

    /// <summary>Renders a live execution dashboard row during command execution.</summary>
    public static void RenderExecutionProgress(string commandName, int totalSteps, int completedSteps, bool success)
    {
        var bar = new string('█', completedSteps) + new string('░', totalSteps - completedSteps);
        var pct = totalSteps == 0
            ? 100
            : completedSteps * 100 / totalSteps;

        var statusColor = success ? "green" : "red";
        AnsiConsole.MarkupLine(
            $"  [{statusColor}]{bar}[/] {pct}% ({completedSteps}/{totalSteps} steps)  [dim]{Markup.Escape(commandName)}[/]");
    }
}
