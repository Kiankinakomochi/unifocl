using System.Globalization;
using System.Text.Json;
using Spectre.Console;

internal sealed partial class InspectorModeService
{
    private async Task RunInteractiveFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        if (context.SelectedComponentIndex is null)
        {
            AddStream(context, "[!] no selected component for interactive edit");
            _renderer.Render(context, null, field.Name, focusModeEnabled: true);
            return;
        }

        if (TryParseVectorValues(field.Value, out var vectorValues))
        {
            await RunVectorFieldEditAsync(context, session, field, vectorValues);
            return;
        }

        if (field.Type.Equals("String", StringComparison.OrdinalIgnoreCase))
        {
            await RunTextFieldEditAsync(context, session, field);
            return;
        }

        if (IsObjectReferenceField(field))
        {
            await RunObjectReferenceFieldEditAsync(context, session, field);
            return;
        }

        if (IsNumericFieldType(field.Type))
        {
            await RunNumericFieldEditAsync(context, session, field);
            return;
        }

        if (field.IsBoolean)
        {
            await RunEnumFieldEditAsync(context, session, field, ["false", "true"], "bool");
            return;
        }

        var enumOptions = field.EnumOptions?.Where(option => !string.IsNullOrWhiteSpace(option)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (enumOptions.Count > 0)
        {
            await RunEnumFieldEditAsync(context, session, field, enumOptions, "enum");
            return;
        }

        AddStream(context, $"[i] interactive edit not available for {field.Type}; use set/edit command");
        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
    }

    private async Task RunObjectReferenceFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        if (context.SelectedComponentIndex is null || string.IsNullOrWhiteSpace(context.SelectedComponentName))
        {
            AddStream(context, "[!] no selected component for reference edit");
            _renderer.Render(context, null, field.Name, focusModeEnabled: true);
            return;
        }

        var query = string.Empty;
        var includeScene = true;
        var includeProject = true;
        var selectedIndex = 0;
        AddStream(context, "[i] reference edit: type query, Up/Down select, Enter apply, Tab scope(scene/project/all), Esc cancel");
        BeginInteractiveEdit(context, field.Name, "reference", 0, 0);

        try
        {
            while (true)
            {
                var results = await FetchReferenceSearchEntriesAsync(
                    context,
                    session,
                    context.SelectedComponentIndex.Value,
                    context.SelectedComponentName,
                    field.Name,
                    query,
                    includeScene,
                    includeProject);

                context.LastReferenceSearchResults.Clear();
                context.LastReferenceSearchResults.AddRange(results);
                selectedIndex = results.Count == 0
                    ? 0
                    : Math.Clamp(selectedIndex, 0, Math.Min(7, results.Count - 1));
                context.InteractiveSearchPreviewRows.Clear();
                context.InteractiveSearchPreviewRows.AddRange(
                    BuildReferencePreviewRows(query, includeScene, includeProject, results, selectedIndex));
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        context.InteractiveSearchPreviewRows.Clear();
                        AddStream(context, "[i] reference edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;

                    case ConsoleKey.Enter:
                        if (results.Count == 0)
                        {
                            AddStream(context, "[!] no reference result selected");
                            continue;
                        }

                        var selected = results[Math.Clamp(selectedIndex, 0, results.Count - 1)];
                        if (await ApplyInteractiveFieldValueAsync(context, session, field, selected.ValueToken))
                        {
                            context.InteractiveSearchPreviewRows.Clear();
                            return;
                        }

                        context.InteractiveSearchPreviewRows.Clear();
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;

                    case ConsoleKey.UpArrow:
                        if (results.Count > 0)
                        {
                            var cap = Math.Min(8, results.Count);
                            selectedIndex = selectedIndex <= 0 ? cap - 1 : selectedIndex - 1;
                        }

                        break;

                    case ConsoleKey.DownArrow:
                        if (results.Count > 0)
                        {
                            var cap = Math.Min(8, results.Count);
                            selectedIndex = (selectedIndex + 1) % cap;
                        }

                        break;

                    case ConsoleKey.Tab:
                        if (includeScene && includeProject)
                        {
                            includeScene = true;
                            includeProject = false;
                        }
                        else if (includeScene)
                        {
                            includeScene = false;
                            includeProject = true;
                        }
                        else
                        {
                            includeScene = true;
                            includeProject = true;
                        }

                        selectedIndex = 0;
                        break;

                    case ConsoleKey.Backspace:
                        if (query.Length > 0)
                        {
                            query = query[..^1];
                            selectedIndex = 0;
                        }

                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            query += key.KeyChar;
                            selectedIndex = 0;
                        }

                        break;
                }
            }
        }
        finally
        {
            context.InteractiveSearchPreviewRows.Clear();
            EndInteractiveEdit(context);
        }
    }

    private async Task<List<InspectorReferenceSearchEntry>> FetchReferenceSearchEntriesAsync(
        InspectorContext context,
        CliSessionState session,
        int componentIndex,
        string componentName,
        string fieldName,
        string query,
        bool includeScene,
        bool includeProject)
    {
        var payload = new InspectorBridgeRequest(
            "find-reference",
            context.TargetPath,
            componentIndex,
            componentName,
            fieldName,
            null,
            query,
            includeScene,
            includeProject);
        var responseJson = await SendBridgeRequestAsync(payload, session);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return [];
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBridgeSearchResponse>(responseJson, _jsonOptions);
            if (response?.Ok != true || response.Results is null)
            {
                return [];
            }

            return response.Results
                .Where(result => !string.IsNullOrWhiteSpace(result.Path) && !string.IsNullOrWhiteSpace(result.ValueToken))
                .Select(result => new InspectorReferenceSearchEntry(
                    string.IsNullOrWhiteSpace(result.Scope) ? "reference" : result.Scope,
                    result.Path,
                    result.ValueToken!))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> BuildReferencePreviewRows(
        string query,
        bool includeScene,
        bool includeProject,
        IReadOnlyList<InspectorReferenceSearchEntry> results,
        int selectedIndex)
    {
        var scopeLabel = includeScene && includeProject
            ? "all"
            : includeScene
                ? "scene"
                : "project";
        var lines = new List<string>
        {
            $"[grey]reference fuzzy[/] | query: {Markup.Escape(query)} | scope: {Markup.Escape(scopeLabel)}"
        };

        if (results.Count == 0)
        {
            lines.Add("no matches");
            lines.Add("[grey]type to search references[/]");
            return lines;
        }

        var shown = Math.Min(8, results.Count);
        for (var i = 0; i < shown; i++)
        {
            var result = results[i];
            var indexCell = Markup.Escape($"[{i}]");
            var scopeCell = Markup.Escape($"[{result.Scope}]");
            var escapedScope = Markup.Escape(result.Scope);
            var escapedPath = Markup.Escape(result.Path);
            if (i == selectedIndex)
            {
                lines.Add(
                    $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]▸ {indexCell} {scopeCell} {escapedPath}[/]");
                continue;
            }

            lines.Add($"[grey] {indexCell} {scopeCell} {escapedPath}[/]");
        }

        if (results.Count > shown)
        {
            lines.Add($"... {results.Count - shown} more");
        }

        lines.Add("[grey]Enter apply | Tab scope | Esc cancel[/]");
        return lines;
    }

    private async Task RunVectorFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field,
        List<float> values)
    {
        var original = field;
        var selectedIndex = 0;
        var componentTexts = values
            .Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))
            .ToList();
        AddStream(context, "[i] vector edit: type number, Backspace/Delete edit, Tab next component, Left/Right adjust, Enter apply, Esc cancel");
        BeginInteractiveEdit(context, field.Name, "vector", selectedIndex, values.Count);

        try
        {
            while (true)
            {
                UpdateInteractiveEditCursor(context, selectedIndex, componentTexts.Count);
                var preview = FormatVector(componentTexts);
                ReplaceField(context, field with { Value = preview });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Tab:
                        selectedIndex = (selectedIndex + 1) % componentTexts.Count;
                        break;
                    case ConsoleKey.LeftArrow:
                        if (TryParseFloatInvariant(componentTexts[selectedIndex], out var leftValue))
                        {
                            leftValue -= key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 1f : 0.1f;
                            componentTexts[selectedIndex] = leftValue.ToString("0.###", CultureInfo.InvariantCulture);
                        }

                        break;
                    case ConsoleKey.RightArrow:
                        if (TryParseFloatInvariant(componentTexts[selectedIndex], out var rightValue))
                        {
                            rightValue += key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 1f : 0.1f;
                            componentTexts[selectedIndex] = rightValue.ToString("0.###", CultureInfo.InvariantCulture);
                        }

                        break;
                    case ConsoleKey.Backspace:
                        if (componentTexts[selectedIndex].Length > 0)
                        {
                            componentTexts[selectedIndex] = componentTexts[selectedIndex][..^1];
                        }

                        break;
                    case ConsoleKey.Delete:
                        componentTexts[selectedIndex] = string.Empty;
                        break;
                    case ConsoleKey.Enter:
                        if (!TryValidateVectorComponentTexts(componentTexts))
                        {
                            AddStream(context, "[!] invalid numeric input in vector components");
                            break;
                        }

                        if (await ApplyInteractiveFieldValueAsync(context, session, field, preview))
                        {
                            return;
                        }

                        ReplaceField(context, original);
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.Escape:
                        ReplaceField(context, original);
                        AddStream(context, "[i] vector edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    default:
                        if (IsAllowedNumericInputChar(key.KeyChar, "Float"))
                        {
                            componentTexts[selectedIndex] += key.KeyChar;
                        }

                        break;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private async Task RunEnumFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field,
        IReadOnlyList<string> enumOptions,
        string mode)
    {
        var original = field;
        var selectedIndex = Math.Max(0, enumOptions.ToList().FindIndex(option => option.Equals(field.Value, StringComparison.OrdinalIgnoreCase)));
        AddStream(context, mode == "bool"
            ? "[i] bool edit: Enter apply, Esc cancel, Left/Right/Tab cycle values"
            : "[i] enum edit: Enter apply, Esc cancel, Left/Right/Tab cycle options");
        AddStream(context, $"[i] {mode} options: {string.Join(" | ", enumOptions)}");
        BeginInteractiveEdit(context, field.Name, mode, selectedIndex, enumOptions.Count);

        try
        {
            while (true)
            {
                UpdateInteractiveEditCursor(context, selectedIndex, enumOptions.Count);
                var preview = enumOptions[selectedIndex];
                ReplaceField(context, field with { Value = preview });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Tab:
                    case ConsoleKey.RightArrow:
                        selectedIndex = (selectedIndex + 1) % enumOptions.Count;
                        break;
                    case ConsoleKey.LeftArrow:
                        selectedIndex = selectedIndex <= 0 ? enumOptions.Count - 1 : selectedIndex - 1;
                        break;
                    case ConsoleKey.Enter:
                        if (await ApplyInteractiveFieldValueAsync(context, session, field, preview))
                        {
                            return;
                        }

                        ReplaceField(context, original);
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.Escape:
                        ReplaceField(context, original);
                        AddStream(context, "[i] enum edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    default:
                        break;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private async Task RunNumericFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        var original = field;
        var buffer = field.Value;
        AddStream(context, "[i] numeric edit: type value, Enter apply, Esc cancel, Backspace delete");
        BeginInteractiveEdit(context, field.Name, "number", 0, 1);

        try
        {
            while (true)
            {
                ReplaceField(context, field with { Value = buffer });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape)
                {
                    ReplaceField(context, original);
                    AddStream(context, "[i] numeric edit cancelled");
                    _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                    return;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (!IsValidNumericText(field.Type, buffer))
                    {
                        AddStream(context, $"[!] invalid {field.Type.ToLowerInvariant()} value: {buffer}");
                        continue;
                    }

                    if (await ApplyInteractiveFieldValueAsync(context, session, field, buffer))
                    {
                        return;
                    }

                    ReplaceField(context, original);
                    _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                    return;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer = buffer[..^1];
                    }

                    continue;
                }

                if (IsAllowedNumericInputChar(key.KeyChar, field.Type))
                {
                    buffer += key.KeyChar;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private async Task RunTextFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        var original = field;
        var buffer = field.Value;
        var cursor = buffer.Length;
        AddStream(context, "[i] text edit: type value, Left/Right move cursor, Enter apply, Esc cancel, Backspace/Delete edit");
        BeginInteractiveEdit(context, field.Name, "text", 0, 1);

        try
        {
            while (true)
            {
                SetInteractiveOverlay(context, field.Name, BuildTextOverlayValue(buffer, cursor));
                ReplaceField(context, field with { Value = buffer });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        ReplaceField(context, original);
                        AddStream(context, "[i] text edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.Enter:
                        if (await ApplyInteractiveFieldValueAsync(context, session, field, buffer))
                        {
                            return;
                        }

                        ReplaceField(context, original);
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.LeftArrow:
                        if (cursor > 0)
                        {
                            cursor--;
                        }

                        break;
                    case ConsoleKey.RightArrow:
                        if (cursor < buffer.Length)
                        {
                            cursor++;
                        }

                        break;
                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            buffer = buffer.Remove(cursor - 1, 1);
                            cursor--;
                        }

                        break;
                    case ConsoleKey.Delete:
                        if (cursor < buffer.Length)
                        {
                            buffer = buffer.Remove(cursor, 1);
                        }

                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            buffer = buffer.Insert(cursor, key.KeyChar.ToString());
                            cursor++;
                        }

                        break;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private static void BeginInteractiveEdit(
        InspectorContext context,
        string fieldName,
        string mode,
        int partIndex,
        int partCount)
    {
        context.InteractiveEditActive = true;
        context.InteractiveEditFieldName = fieldName;
        context.InteractiveEditMode = mode;
        context.InteractiveEditPartIndex = partIndex;
        context.InteractiveEditPartCount = partCount;
        context.InteractiveOverlayActive = false;
        context.InteractiveOverlayTitle = null;
        context.InteractiveOverlayValue = null;
    }

    private static void UpdateInteractiveEditCursor(InspectorContext context, int partIndex, int partCount)
    {
        context.InteractiveEditPartIndex = partIndex;
        context.InteractiveEditPartCount = partCount;
    }

    private static void EndInteractiveEdit(InspectorContext context)
    {
        context.InteractiveEditActive = false;
        context.InteractiveEditFieldName = null;
        context.InteractiveEditMode = null;
        context.InteractiveEditPartIndex = 0;
        context.InteractiveEditPartCount = 0;
        context.InteractiveOverlayActive = false;
        context.InteractiveOverlayTitle = null;
        context.InteractiveOverlayValue = null;
        context.InteractiveSearchPreviewRows.Clear();
    }

    private static void SetInteractiveOverlay(InspectorContext context, string title, string value)
    {
        context.InteractiveOverlayActive = true;
        context.InteractiveOverlayTitle = title;
        context.InteractiveOverlayValue = value;
    }

    private static string BuildTextOverlayValue(string text, int cursorIndex)
    {
        var clamped = Math.Clamp(cursorIndex, 0, text.Length);
        var withCursor = text.Insert(clamped, "|");
        return withCursor.Length == 0 ? "|" : withCursor;
    }

    private async Task<bool> ApplyInteractiveFieldValueAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field,
        string value)
    {
        var mutation = await TrySendInspectorMutationWithMessageAsync(
            session,
            new InspectorBridgeRequest(
                "set-field",
                context.TargetPath,
                context.SelectedComponentIndex,
                context.SelectedComponentName,
                field.Name,
                value,
                null));
        if (!mutation.Ok)
        {
            AddStream(context, $"[!] {DescribeInspectorMutationFailure(mutation.Message)}: {field.Name}");
            return false;
        }

        await PopulateFieldsAsync(context, session, context.SelectedComponentIndex!.Value, forceRefresh: true);
        var appliedValue = context.Fields
            .FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? value;
        AddStream(context, $"[=] ok: {field.Name} updated to {appliedValue}");
        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
        return true;
    }

    private static bool TryParseVectorValues(string value, out List<float> parsed)
    {
        parsed = [];
        var normalized = value.Trim();
        if (!normalized.StartsWith("(", StringComparison.Ordinal)
            || !normalized.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        normalized = normalized[1..^1];
        var tokens = normalized
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        if (tokens.Length is < 2 or > 4)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            parsed.Add(number);
        }

        return true;
    }

    private static string FormatVector(IReadOnlyList<float> values)
    {
        var formatted = values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture));
        return $"({string.Join(", ", formatted)})";
    }

    private static string FormatVector(IReadOnlyList<string> values)
    {
        var normalized = values.Select(value => value.Trim());
        return $"({string.Join(", ", normalized)})";
    }

    private static bool IsNumericFieldType(string type)
    {
        return type.Equals("Integer", StringComparison.OrdinalIgnoreCase)
               || type.Equals("Float", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidNumericText(string type, string text)
    {
        if (type.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        if (type.Equals("Float", StringComparison.OrdinalIgnoreCase))
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        return false;
    }

    private static bool IsAllowedNumericInputChar(char keyChar, string type)
    {
        if (char.IsControl(keyChar))
        {
            return false;
        }

        if (char.IsDigit(keyChar))
        {
            return true;
        }

        if (type.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            return keyChar is '-' or '+';
        }

        if (type.Equals("Float", StringComparison.OrdinalIgnoreCase))
        {
            return keyChar is '-' or '+' or '.' or 'e' or 'E';
        }

        return false;
    }

    private static bool TryValidateVectorComponentTexts(IReadOnlyList<string> values)
    {
        if (values.Count is < 2 or > 4)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!TryParseFloatInvariant(value, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseFloatInvariant(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
