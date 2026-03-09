using System.Text;

internal static class ProjectMkCatalog
{
    private static readonly Dictionary<string, string> TypeLookup = BuildTypeLookup();
    private static readonly List<string> CanonicalTypes = TypeLookup.Values
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static IReadOnlyList<string> KnownTypes => CanonicalTypes;

    public static bool TryNormalizeType(string raw, out string canonical, out string error)
    {
        canonical = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "mk type is required";
            return false;
        }

        var key = NormalizeKey(raw);
        if (!TypeLookup.TryGetValue(key, out var resolved))
        {
            error = $"unsupported mk type: {raw}";
            return false;
        }

        canonical = resolved;
        return true;
    }

    public static (string? TypeFilter, string Query) ParseFuzzyQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, string.Empty);
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? typeFilter = null;
        var remaining = new List<string>();

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
            {
                var value = token[2..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    typeFilter = value;
                    continue;
                }
            }

            if (token.Equals("--type", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Length && !string.IsNullOrWhiteSpace(tokens[i + 1]))
                {
                    typeFilter = tokens[++i];
                    continue;
                }

                remaining.Add(token);
                continue;
            }

            if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                var value = token["--type=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    typeFilter = value;
                    continue;
                }
            }

            if (token.StartsWith("-t=", StringComparison.OrdinalIgnoreCase))
            {
                var value = token["-t=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    typeFilter = value;
                    continue;
                }
            }

            remaining.Add(token);
        }

        return (typeFilter, remaining.Count == 0 ? string.Empty : string.Join(' ', remaining));
    }

    public static bool PassesFuzzyTypeFilter(string path, string? typeFilter)
    {
        if (string.IsNullOrWhiteSpace(typeFilter))
        {
            return true;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var extensions = ResolveFilterExtensions(typeFilter);
        if (extensions.Count > 0)
        {
            return extensions.Contains(ext);
        }

        return path.Contains(typeFilter, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveDefaultExtension(string type)
    {
        var key = NormalizeKey(type);
        return key switch
        {
            "scene" => ".unity",
            "prefab" => ".prefab",
            "prefabvariant" => ".prefab",
            "csharpscript" => ".cs",
            "scriptableobjectscript" => ".cs",
            "assemblydefinition" => ".asmdef",
            "assemblydefinitionreference" => ".asmref",
            "testingassemblydefinition" => ".asmdef",
            "testingassemblydefinitionreference" => ".asmref",
            "shader" => ".shader",
            "computeshader" => ".compute",
            "shaderincludefile" => ".hlsl",
            "material" => ".mat",
            "animatorcontroller" => ".controller",
            "animatoroverridecontroller" => ".overrideController",
            "animationclip" => ".anim",
            "inputactions" => ".inputactions",
            "uxmldocument" => ".uxml",
            "ussstylesheet" => ".uss",
            "shadergraph" => ".shadergraph",
            "subgraph" => ".shadersubgraph",
            "vfxgraph" => ".vfx",
            "searchindex" => ".index",
            _ => ".asset"
        };
    }

    public static string NormalizeKey(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static HashSet<string> ResolveFilterExtensions(string rawTypeFilter)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawTypeFilter))
        {
            return extensions;
        }

        if (TryNormalizeType(rawTypeFilter, out var canonicalType, out _))
        {
            AddCanonicalExtensions(canonicalType, extensions);
        }

        var filterKey = NormalizeKey(rawTypeFilter);
        if (filterKey is "animation" or "anim")
        {
            extensions.Add(".anim");
            extensions.Add(".controller");
        }

        return extensions;
    }

    private static void AddCanonicalExtensions(string canonicalType, HashSet<string> extensions)
    {
        var primaryExtension = ResolveDefaultExtension(canonicalType);
        if (!string.IsNullOrWhiteSpace(primaryExtension))
        {
            extensions.Add(primaryExtension);
        }

        if (canonicalType.Equals("AnimationClip", StringComparison.OrdinalIgnoreCase))
        {
            extensions.Add(".controller");
        }
    }

    private static Dictionary<string, string> BuildTypeLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string canonical, params string[] aliases)
        {
            lookup[NormalizeKey(canonical)] = canonical;
            foreach (var alias in aliases)
            {
                lookup[NormalizeKey(alias)] = canonical;
            }
        }

        Add("Folder", "Dir", "Directory");
        Add("Scene", "UnityScene");
        Add("SceneTemplate");
        Add("Prefab");
        Add("PrefabVariant");
        Add("CSharpScript", "Script", "CScript", "C#Script");
        Add("ScriptableObjectScript", "SOScript", "ScriptableObject");
        Add("AssemblyDefinition", "Asmdef");
        Add("AssemblyDefinitionReference", "Asmref");
        Add("RoslynAnalyzer", "Analyzer");
        Add("TestingAssemblyDefinition", "TestAsmdef");
        Add("TestingAssemblyDefinitionReference", "TestAsmref");
        Add("Shader");
        Add("ComputeShader");
        Add("ShaderVariantCollection");
        Add("ShaderIncludeFile", "ShaderInclude", "Cginc", "HlslInclude");
        Add("Material");
        Add("RenderTexture");
        Add("CustomRenderTexture");
        Add("AnimatorController", "Controller");
        Add("AnimatorOverrideController");
        Add("AvatarMask");
        Add("AnimationClip", "AnimClip");
        Add("Timeline", "TimelineAsset");
        Add("AudioMixer", "Mixer");
        Add("PhysicsMaterial", "PhysicMaterial", "PhysicsMat");
        Add("PhysicsMaterial2D", "PhysicsMat2D");
        Add("SpriteAtlas");
        Add("Tile");
        Add("TilePalette");
        Add("RuleTile");
        Add("AnimatedTile");
        Add("IsometricTile", "IsometricRuleTile");
        Add("HexagonalTile", "HexagonalRuleTile");
        Add("InputActions", "InputActionAsset");
        Add("UIToolkitPanelSettings", "PanelSettings");
        Add("UIDocument");
        Add("UXMLDocument", "UXML");
        Add("USSStyleSheet", "USS");
        Add("LightingSettings");
        Add("LensFlare");
        Add("Cubemap");
        Add("Texture", "Texture2D");
        Add("RenderPipelineAsset", "RPAsset");
        Add("UniversalRenderPipelineAsset", "URPAsset");
        Add("HighDefinitionRenderPipelineAsset", "HDRPAsset");
        Add("PostProcessingProfile");
        Add("VolumeProfile");
        Add("AddressablesGroup");
        Add("AddressablesAssetGroupTemplate", "AddressablesGroupTemplate");
        Add("ShaderGraph");
        Add("SubGraph");
        Add("VFXGraph");
        Add("VisualScriptingScriptGraph");
        Add("VisualScriptingStateGraph");
        Add("PlayableAsset");
        Add("PlayableGraphAsset", "PlayableGraph", "PlayablesAsset");
        Add("LocalizationTable");
        Add("StringTable");
        Add("AssetTable");
        Add("Locale");
        Add("TerrainLayer");
        Add("NavMeshData");
        Add("PlayModeTestAsset");
        Add("Preset");
        Add("SearchIndex");
        return lookup;
    }
}
