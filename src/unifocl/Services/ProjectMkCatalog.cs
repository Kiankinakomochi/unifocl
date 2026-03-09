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
