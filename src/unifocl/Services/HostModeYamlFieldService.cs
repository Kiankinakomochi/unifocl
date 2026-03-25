using System.Text.RegularExpressions;

internal static class HostModeYamlFieldService
{
    private static readonly Regex DocSeparatorRegex = new(
        @"^--- !u!(\d+) &(\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex GuidLineRegex = new(
        @"^guid:\s*([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static bool TrySetField(
        string? targetPath,
        int? componentIndex,
        string? componentName,
        string? fieldName,
        string? rawValue,
        string projectPath,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            error = "set-field: targetPath is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            error = "set-field: fieldName is required";
            return false;
        }

        // Strip leading slash (host mode hierarchy path format)
        var normalizedTarget = targetPath.TrimStart('/');
        string absoluteTarget;
        if (normalizedTarget.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Equals("Assets", StringComparison.OrdinalIgnoreCase))
        {
            absoluteTarget = Path.GetFullPath(Path.Combine(projectPath, normalizedTarget));
        }
        else
        {
            absoluteTarget = Path.GetFullPath(normalizedTarget);
        }

        if (!File.Exists(absoluteTarget))
        {
            error = $"set-field: target file not found: {absoluteTarget}";
            return false;
        }

        string fileContent;
        try
        {
            fileContent = File.ReadAllText(absoluteTarget, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            error = $"set-field: failed to read target file: {ex.Message}";
            return false;
        }

        // Detect line ending
        var lineEnding = fileContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Split into lines preserving content
        var lines = fileContent.Split('\n');
        // Normalize: remove trailing \r from each line if CRLF
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        // Find the script GUID for componentName matching
        string? scriptGuid = null;
        if (!string.IsNullOrWhiteSpace(componentName))
        {
            scriptGuid = FindScriptGuid(componentName!, projectPath);
        }

        // Parse document blocks from lines
        // Each block is identified by a --- !u!TYPE &FILEID header line
        var blocks = ParseDocumentBlocks(lines);

        // Filter to MonoBehaviour blocks (type 114)
        var mbBlocks = blocks.Where(b => b.TypeId == 114).ToList();

        if (mbBlocks.Count == 0)
        {
            error = "set-field: no MonoBehaviour components found in target file";
            return false;
        }

        // Find the target MonoBehaviour block
        DocumentBlock? targetBlock = null;

        if (scriptGuid != null)
        {
            // Match by script GUID
            foreach (var block in mbBlocks)
            {
                var blockLines = lines[block.StartLine..block.EndLine];
                if (BlockContainsScriptGuid(blockLines, scriptGuid))
                {
                    targetBlock = block;
                    break;
                }
            }
        }

        if (targetBlock == null && componentIndex.HasValue)
        {
            if (componentIndex.Value >= 0 && componentIndex.Value < mbBlocks.Count)
            {
                targetBlock = mbBlocks[componentIndex.Value];
            }
        }

        if (targetBlock == null && scriptGuid == null && !componentIndex.HasValue)
        {
            // Fallback: find first MonoBehaviour that contains the field
            foreach (var block in mbBlocks)
            {
                var blockLines = lines[block.StartLine..block.EndLine];
                if (BlockContainsField(blockLines, fieldName!))
                {
                    targetBlock = block;
                    break;
                }
            }

            // If still not found, use first
            if (targetBlock == null)
            {
                targetBlock = mbBlocks[0];
            }
        }

        if (targetBlock == null)
        {
            // Fallback: try field-name match across all MB blocks
            foreach (var block in mbBlocks)
            {
                var blockLines = lines[block.StartLine..block.EndLine];
                if (BlockContainsField(blockLines, fieldName!))
                {
                    targetBlock = block;
                    break;
                }
            }

            if (targetBlock == null)
            {
                error = $"set-field: component not found (componentName={componentName}, componentIndex={componentIndex})";
                return false;
            }
        }

        // Resolve new YAML value string (pass lines for scene: path resolution)
        if (!TryResolveYamlValue(rawValue, projectPath, lines, out var yamlValue, out var resolveError))
        {
            error = $"set-field: {resolveError}";
            return false;
        }

        // Find and replace (or add) the field line in the target block
        var newFieldLine = $"  {fieldName}: {yamlValue}";
        var fieldPattern = new Regex($@"^  {Regex.Escape(fieldName!)}:\s", RegexOptions.Compiled);
        var fieldLineIndex = -1;
        for (var i = targetBlock.StartLine; i < targetBlock.EndLine; i++)
        {
            if (fieldPattern.IsMatch(lines[i]))
            {
                fieldLineIndex = i;
                break;
            }
        }

        List<string> newLines;
        if (fieldLineIndex >= 0)
        {
            // Replace existing line
            newLines = new List<string>(lines);
            newLines[fieldLineIndex] = newFieldLine;
        }
        else
        {
            // Add the field before the next document separator (i.e., at end of block)
            // Find the last non-empty field line in the block to insert after
            var insertAfterIndex = targetBlock.EndLine - 1;
            for (var i = targetBlock.EndLine - 1; i >= targetBlock.StartLine; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    insertAfterIndex = i;
                    break;
                }
            }

            newLines = new List<string>(lines);
            newLines.Insert(insertAfterIndex + 1, newFieldLine);
        }

        var newContent = string.Join(lineEnding, newLines);
        // Preserve trailing newline if original had one
        if (fileContent.EndsWith("\n", StringComparison.Ordinal) && !newContent.EndsWith("\n", StringComparison.Ordinal))
        {
            newContent += lineEnding;
        }

        if (CliDryRunScope.IsEnabled)
        {
            return true;
        }

        try
        {
            File.WriteAllText(absoluteTarget, newContent, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            error = $"set-field: failed to write target file: {ex.Message}";
            return false;
        }

        return true;
    }

    private static bool TryResolveYamlValue(
        string? rawValue,
        string projectPath,
        out string yamlValue,
        out string? resolveError)
    {
        resolveError = null;

        if (string.IsNullOrEmpty(rawValue) || rawValue == "null")
        {
            yamlValue = "{fileID: 0}";
            return true;
        }

        if (rawValue.StartsWith("asset:", StringComparison.Ordinal))
        {
            var assetPath = rawValue["asset:".Length..];
            var normalizedAsset = assetPath.TrimStart('/');
            var metaPath = Path.Combine(projectPath, normalizedAsset + ".meta");
            if (!File.Exists(metaPath))
            {
                yamlValue = "{fileID: 0}";
                resolveError = $"meta file not found for asset: {assetPath}";
                return false;
            }

            var metaContent = File.ReadAllText(metaPath, System.Text.Encoding.UTF8);
            var guidMatch = GuidLineRegex.Match(metaContent);
            if (!guidMatch.Success)
            {
                yamlValue = "{fileID: 0}";
                resolveError = $"guid not found in meta file: {metaPath}";
                return false;
            }

            var guid = guidMatch.Groups[1].Value;
            var fileId = ResolveDefaultFileId(normalizedAsset);
            yamlValue = $"{{fileID: {fileId}, guid: {guid}, type: 3}}";
            return true;
        }

        if (rawValue.StartsWith("scene:", StringComparison.Ordinal))
        {
            // scene:/Path/To/Object or scene:/Path/To/Object#ComponentName
            // We can't resolve file IDs without the actual YAML content of the current file
            // This is handled inline in TrySetField - for now we just return an error
            // indicating that scene references require additional context.
            // The caller should use the overload that has the YAML content.
            yamlValue = "{fileID: 0}";
            resolveError = "scene: references are not yet supported in host mode set-field";
            return false;
        }

        yamlValue = "{fileID: 0}";
        resolveError = $"unsupported value format: {rawValue}";
        return false;
    }

    private static bool TryResolveYamlValue(
        string? rawValue,
        string projectPath,
        string[] allLines,
        out string yamlValue,
        out string? resolveError)
    {
        resolveError = null;

        if (string.IsNullOrEmpty(rawValue) || rawValue == "null")
        {
            yamlValue = "{fileID: 0}";
            return true;
        }

        if (rawValue.StartsWith("asset:", StringComparison.Ordinal))
        {
            return TryResolveYamlValue(rawValue, projectPath, out yamlValue, out resolveError);
        }

        if (rawValue.StartsWith("scene:", StringComparison.Ordinal))
        {
            var scenePath = rawValue["scene:".Length..];
            string? componentFilter = null;
            var hashIdx = scenePath.IndexOf('#', StringComparison.Ordinal);
            if (hashIdx >= 0)
            {
                componentFilter = scenePath[(hashIdx + 1)..];
                scenePath = scenePath[..hashIdx];
            }

            // scenePath is like /TitleText or /Canvas/TitleText - last segment is the object name
            var objectName = scenePath.TrimStart('/');
            var lastSlash = objectName.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                objectName = objectName[(lastSlash + 1)..];
            }

            if (string.IsNullOrWhiteSpace(objectName))
            {
                yamlValue = "{fileID: 0}";
                resolveError = $"invalid scene: path - could not extract object name from: {rawValue}";
                return false;
            }

            // Find the fileID of the named GameObject or component in allLines
            if (!TryFindSceneObjectFileId(allLines, objectName, componentFilter, out var fileId))
            {
                yamlValue = "{fileID: 0}";
                resolveError = $"scene object not found in target file: {objectName}";
                return false;
            }

            yamlValue = $"{{fileID: {fileId}}}";
            return true;
        }

        yamlValue = "{fileID: 0}";
        resolveError = $"unsupported value format: {rawValue}";
        return false;
    }

    private static bool TryFindSceneObjectFileId(
        string[] lines,
        string objectName,
        string? componentName,
        out long fileId)
    {
        fileId = 0;
        var blocks = ParseDocumentBlocks(lines);

        // Find all GameObject blocks with matching m_Name
        var matchingGameObjectFileIds = new List<long>();
        foreach (var block in blocks.Where(b => b.TypeId == 1))
        {
            var blockLines = lines[block.StartLine..block.EndLine];
            var hasName = false;
            foreach (var line in blockLines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("m_Name:", StringComparison.Ordinal))
                {
                    var namePart = trimmed["m_Name:".Length..].Trim();
                    if (string.Equals(namePart, objectName, StringComparison.Ordinal))
                    {
                        hasName = true;
                        break;
                    }
                }
            }

            if (hasName)
            {
                matchingGameObjectFileIds.Add(block.FileId);
            }
        }

        if (matchingGameObjectFileIds.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(componentName))
        {
            // Return fileID of first matching GameObject
            fileId = matchingGameObjectFileIds[0];
            return true;
        }

        // Find the script GUID for the component name - search all blocks
        // componentName is the MonoBehaviour class name
        // We need to find the MonoBehaviour that is on one of the matched GameObjects
        // m_GameObject: {fileID: GO_FILE_ID}
        foreach (var block in blocks.Where(b => b.TypeId == 114))
        {
            var blockLines = lines[block.StartLine..block.EndLine];
            long? goFileId = null;
            foreach (var line in blockLines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("m_GameObject:", StringComparison.Ordinal))
                {
                    var match = Regex.Match(trimmed, @"fileID:\s*(\d+)");
                    if (match.Success && long.TryParse(match.Groups[1].Value, out var parsedId))
                    {
                        goFileId = parsedId;
                    }

                    break;
                }
            }

            if (goFileId == null || !matchingGameObjectFileIds.Contains(goFileId.Value))
            {
                continue;
            }

            // Check script class identifier
            var classIdentifierLine = blockLines
                .FirstOrDefault(l => l.TrimStart().StartsWith("m_EditorClassIdentifier:", StringComparison.Ordinal));
            if (classIdentifierLine != null)
            {
                var idPart = classIdentifierLine
                    .TrimStart()["m_EditorClassIdentifier:".Length..]
                    .Trim();
                if (idPart.EndsWith(componentName, StringComparison.Ordinal))
                {
                    fileId = block.FileId;
                    return true;
                }
            }
        }

        // Fallback: return the GameObject fileID
        fileId = matchingGameObjectFileIds[0];
        return true;
    }

    private static string? FindScriptGuid(string componentName, string projectPath)
    {
        var assetsRoot = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            return null;
        }

        // Search for Assets/**/<componentName>.cs.meta
        try
        {
            var metaFiles = Directory.GetFiles(assetsRoot, $"{componentName}.cs.meta", SearchOption.AllDirectories);
            foreach (var metaFile in metaFiles)
            {
                var content = File.ReadAllText(metaFile, System.Text.Encoding.UTF8);
                var match = GuidLineRegex.Match(content);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Ignore file system errors
        }

        return null;
    }

    private static bool BlockContainsScriptGuid(string[] blockLines, string scriptGuid)
    {
        foreach (var line in blockLines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("m_Script:", StringComparison.Ordinal))
            {
                return line.Contains(scriptGuid, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static bool BlockContainsField(string[] blockLines, string fieldName)
    {
        var prefix = $"  {fieldName}:";
        foreach (var line in blockLines)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<DocumentBlock> ParseDocumentBlocks(string[] lines)
    {
        var blocks = new List<DocumentBlock>();
        var separatorPattern = new Regex(@"^--- !u!(\d+) &(\d+)", RegexOptions.Compiled);

        var blockStart = -1;
        var blockTypeId = 0;
        var blockFileId = 0L;

        for (var i = 0; i < lines.Length; i++)
        {
            var match = separatorPattern.Match(lines[i]);
            if (match.Success)
            {
                if (blockStart >= 0)
                {
                    blocks.Add(new DocumentBlock(blockTypeId, blockFileId, blockStart, i));
                }

                blockStart = i + 1;
                blockTypeId = int.Parse(match.Groups[1].Value);
                blockFileId = long.Parse(match.Groups[2].Value);
            }
        }

        if (blockStart >= 0)
        {
            blocks.Add(new DocumentBlock(blockTypeId, blockFileId, blockStart, lines.Length));
        }

        return blocks;
    }

    private static long ResolveDefaultFileId(string assetPath)
    {
        var ext = Path.GetExtension(assetPath).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".tga" or ".tiff" or ".psd" or ".bmp" or ".gif" or ".exr" or ".hdr" => 2800000,
            ".mat" => 2100000,
            ".anim" => 74000000,
            ".controller" => 9100000,
            ".cs" => 11500000,
            ".prefab" => 100100000,
            ".ttf" or ".otf" or ".fnt" => 12800000,
            ".mp3" or ".wav" or ".ogg" or ".aiff" or ".m4a" => 83000000,
            ".shader" => 4800000,
            ".rendertexture" => 84000000,
            ".asset" => 11400000,
            _ => 2800000
        };
    }

    private sealed record DocumentBlock(int TypeId, long FileId, int StartLine, int EndLine);
}
