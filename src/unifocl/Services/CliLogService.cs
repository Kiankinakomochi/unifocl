using Spectre.Console;

internal static class CliLogService
{
    public static void RenderInitialLog(List<string> streamLog)
    {
        foreach (var line in streamLog)
        {
            CliTheme.MarkupLine(line);
        }
    }

    public static void AppendLog(List<string> streamLog, string line)
    {
        streamLog.Add(line);
        if (!CliRuntimeState.SuppressConsoleOutput)
        {
            CliTheme.MarkupLine(line);
        }
    }

    public static void AppendMarkdown(List<string> streamLog, string markdown)
    {
        MarkdownRenderer.RenderToLog(markdown, line => AppendLog(streamLog, line));
    }

    public static void LogUnhandledException(List<string> streamLog, Exception ex, string phase)
    {
        var typeName = ex.GetType().Name;
        AppendLog(streamLog, $"[red]error[/]: unhandled {Markup.Escape(phase)} exception <{Markup.Escape(typeName)}>");
        AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(ex.Message)}");
        AppendLog(streamLog, "[yellow]system[/]: recovered and continuing session");
    }

    public static void WriteKeybindsHelp(List<string> streamLog, CliSessionState session)
    {
        AppendLog(streamLog, "[bold deepskyblue1]unifocl[/] [grey]>[/] [white]/keybinds[/]");
        AppendLog(streamLog, "[grey]keybinds[/]: global");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] enter/exit hierarchy focus mode (inside /hierarchy)");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]F7/F8[/] enter/exit project focus mode (project context), or recent selection mode after /recent");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] enter/exit inspector focus mode (inspector context)");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc[/] dismiss intellisense (or clear input if already dismissed)");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] fuzzy candidate selection in composer");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] insert selected intellisense suggestion, or commit input when none selected");

        AppendLog(streamLog, "[grey]keybinds[/]: hierarchy focus");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted GameObject");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]0-9 (or multi-digit)[/] jump to visible object index");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] expand selected node");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] enter inspector for selected node");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] collapse selected node");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F7[/] exit focus mode");

        AppendLog(streamLog, "[grey]keybinds[/]: project focus");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted file/folder");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]0-9 (or multi-digit)[/] jump to visible entry index");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] reveal/open selected entry");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] move to parent folder");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc/F7[/] exit focus mode");

        AppendLog(streamLog, "[grey]keybinds[/]: inspector focus");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]↑/↓[/] move highlighted component/field (auto-scrolls long lists)");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]0-9 (or multi-digit)[/] jump to component/field index");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Tab[/] inspect selected component");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Enter[/] edit selected field (component inspection)");
        AppendLog(streamLog, "[grey]keybinds[/]: edit mode -> [white]Tab[/] next vector component / enum-bool option, [white]←/→[/] adjust/cycle, number keys edit vector/color component, [white]Enter[/] apply, [white]Esc[/] cancel");
        AppendLog(streamLog, "[grey]keybinds[/]: numeric edit -> type value directly, [white]Backspace[/] delete, [white]Enter[/] apply");
        AppendLog(streamLog, "[grey]keybinds[/]: text edit -> full-value overlay with cursor, [white]←/→[/] move cursor, [white]Backspace/Delete[/] edit");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Shift+Tab[/] back to component list");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] toggle between inspector interactive selection and command input (component inspection)");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]Esc[/] fields -> component list, component list -> hierarchy");
        AppendLog(streamLog, "[grey]keybinds[/]: [white]F7[/] exit inspector focus mode");

        if (session.ContextMode == CliContextMode.Project && session.Inspector is null)
        {
            AppendLog(streamLog, "[grey]keybinds[/]: current context -> project (F7 available now)");
        }
        else if (session.ContextMode == CliContextMode.Inspector && session.Inspector is not null)
        {
            AppendLog(streamLog, "[grey]keybinds[/]: current context -> inspector (F7 available now)");
        }
        else if (session.ContextMode == CliContextMode.Hierarchy)
        {
            AppendLog(streamLog, "[grey]keybinds[/]: current context -> hierarchy (F7 available now)");
        }
        else
        {
            AppendLog(streamLog, "[grey]keybinds[/]: current context -> boot/general");
        }

        AppendLog(streamLog, "[yellow]note[/]: if your terminal does not emit [white]Shift+Tab[/] distinctly, use typed command [white]up[/] as fallback.");
    }
}
