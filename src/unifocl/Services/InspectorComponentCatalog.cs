using System.Text;

internal static class InspectorComponentCatalog
{
    private sealed record ComponentCatalogEntry(string DisplayName, string TypeReference, string[] Aliases);

    private static readonly List<ComponentCatalogEntry> Entries = BuildEntries();
    private static readonly Dictionary<string, ComponentCatalogEntry> Lookup = BuildLookup(Entries);

    public static IReadOnlyList<string> KnownDisplayNames => Entries
        .Select(entry => entry.DisplayName)
        .ToList();

    public static bool TryResolve(string raw, out string displayName, out string typeReference, out string error)
    {
        displayName = string.Empty;
        typeReference = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "component type is required";
            return false;
        }

        var key = NormalizeKey(raw);
        if (!Lookup.TryGetValue(key, out var entry))
        {
            error = $"unsupported component type: {raw}";
            return false;
        }

        displayName = entry.DisplayName;
        typeReference = entry.TypeReference;
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

    private static Dictionary<string, ComponentCatalogEntry> BuildLookup(IEnumerable<ComponentCatalogEntry> entries)
    {
        var lookup = new Dictionary<string, ComponentCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            lookup[NormalizeKey(entry.DisplayName)] = entry;
            lookup[NormalizeKey(entry.TypeReference)] = entry;

            var typeSimpleName = entry.TypeReference.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(typeSimpleName))
            {
                lookup[NormalizeKey(typeSimpleName)] = entry;
            }

            foreach (var alias in entry.Aliases)
            {
                lookup[NormalizeKey(alias)] = entry;
            }
        }

        return lookup;
    }

    private static List<ComponentCatalogEntry> BuildEntries()
    {
        var entries = new List<ComponentCatalogEntry>();
        void Add(string displayName, string typeReference, params string[] aliases)
            => entries.Add(new ComponentCatalogEntry(displayName, typeReference, aliases));

        Add("Mesh Filter", "UnityEngine.MeshFilter");
        Add("Mesh Renderer", "UnityEngine.MeshRenderer");
        Add("Skinned Mesh Renderer", "UnityEngine.SkinnedMeshRenderer");
        Add("Text Mesh", "UnityEngine.TextMesh");

        Add("Rigidbody", "UnityEngine.Rigidbody");
        Add("Character Controller", "UnityEngine.CharacterController");
        Add("Box Collider", "UnityEngine.BoxCollider");
        Add("Sphere Collider", "UnityEngine.SphereCollider");
        Add("Capsule Collider", "UnityEngine.CapsuleCollider");
        Add("Mesh Collider", "UnityEngine.MeshCollider");
        Add("Wheel Collider", "UnityEngine.WheelCollider");
        Add("Terrain Collider", "UnityEngine.TerrainCollider");
        Add("Fixed Joint", "UnityEngine.FixedJoint");
        Add("Spring Joint", "UnityEngine.SpringJoint");
        Add("Hinge Joint", "UnityEngine.HingeJoint");
        Add("Character Joint", "UnityEngine.CharacterJoint");
        Add("Configurable Joint", "UnityEngine.ConfigurableJoint");
        Add("Constant Force", "UnityEngine.ConstantForce");

        Add("Rigidbody 2D", "UnityEngine.Rigidbody2D");
        Add("Box Collider 2D", "UnityEngine.BoxCollider2D");
        Add("Circle Collider 2D", "UnityEngine.CircleCollider2D");
        Add("Edge Collider 2D", "UnityEngine.EdgeCollider2D");
        Add("Polygon Collider 2D", "UnityEngine.PolygonCollider2D");
        Add("Capsule Collider 2D", "UnityEngine.CapsuleCollider2D");
        Add("Composite Collider 2D", "UnityEngine.CompositeCollider2D");
        Add("Distance Joint 2D", "UnityEngine.DistanceJoint2D");
        Add("Fixed Joint 2D", "UnityEngine.FixedJoint2D");
        Add("Friction Joint 2D", "UnityEngine.FrictionJoint2D");
        Add("Hinge Joint 2D", "UnityEngine.HingeJoint2D");
        Add("Relative Joint 2D", "UnityEngine.RelativeJoint2D");
        Add("Slider Joint 2D", "UnityEngine.SliderJoint2D");
        Add("Spring Joint 2D", "UnityEngine.SpringJoint2D");
        Add("Target Joint 2D", "UnityEngine.TargetJoint2D");
        Add("Wheel Joint 2D", "UnityEngine.WheelJoint2D");
        Add("Area Effector 2D", "UnityEngine.AreaEffector2D");
        Add("Buoyancy Effector 2D", "UnityEngine.BuoyancyEffector2D");
        Add("Point Effector 2D", "UnityEngine.PointEffector2D");
        Add("Platform Effector 2D", "UnityEngine.PlatformEffector2D");
        Add("Surface Effector 2D", "UnityEngine.SurfaceEffector2D");
        Add("Constant Force 2D", "UnityEngine.ConstantForce2D");

        Add("Audio Listener", "UnityEngine.AudioListener");
        Add("Audio Source", "UnityEngine.AudioSource");
        Add("Audio Reverb Zone", "UnityEngine.AudioReverbZone");
        Add("Audio Low Pass Filter", "UnityEngine.AudioLowPassFilter");
        Add("Audio High Pass Filter", "UnityEngine.AudioHighPassFilter");
        Add("Audio Echo Filter", "UnityEngine.AudioEchoFilter");
        Add("Audio Distortion Filter", "UnityEngine.AudioDistortionFilter");
        Add("Audio Reverb Filter", "UnityEngine.AudioReverbFilter");
        Add("Audio Chorus Filter", "UnityEngine.AudioChorusFilter");

        Add("Camera", "UnityEngine.Camera");
        Add("Light", "UnityEngine.Light");
        Add("Particle System", "UnityEngine.ParticleSystem");
        Add("Particle System Force Field", "UnityEngine.ParticleSystemForceField");
        Add("Trail Renderer", "UnityEngine.TrailRenderer");
        Add("Line Renderer", "UnityEngine.LineRenderer");
        Add("Light Probe Group", "UnityEngine.LightProbeGroup");
        Add("Light Probe Proxy Volume", "UnityEngine.LightProbeProxyVolume");
        Add("Reflection Probe", "UnityEngine.ReflectionProbe");
        Add("Flare Layer", "UnityEngine.FlareLayer");
        Add("Halo", "UnityEngine.Rendering.Halo");
        Add("Lens Flare", "UnityEngine.LensFlare");
        Add("Projector", "UnityEngine.Projector");
        Add("Skybox", "UnityEngine.Skybox");
        Add("Sorting Group", "UnityEngine.Rendering.SortingGroup");
        Add("Sprite Renderer", "UnityEngine.SpriteRenderer");
        Add("Sprite Mask", "UnityEngine.SpriteMask");

        Add("Canvas", "UnityEngine.Canvas");
        Add("Canvas Group", "UnityEngine.CanvasGroup");
        Add("Canvas Renderer", "UnityEngine.CanvasRenderer");
        Add("Canvas Scaler", "UnityEngine.UI.CanvasScaler");
        Add("Graphic Raycaster", "UnityEngine.UI.GraphicRaycaster");
        Add("Text", "UnityEngine.UI.Text");
        Add("TextMeshPro - Text (UI)", "TMPro.TextMeshProUGUI", "TextMeshPro Text UI");
        Add("Image", "UnityEngine.UI.Image");
        Add("Raw Image", "UnityEngine.UI.RawImage");
        Add("Button", "UnityEngine.UI.Button");
        Add("Toggle", "UnityEngine.UI.Toggle");
        Add("Toggle Group", "UnityEngine.UI.ToggleGroup");
        Add("Slider", "UnityEngine.UI.Slider");
        Add("Scrollbar", "UnityEngine.UI.Scrollbar");
        Add("Dropdown", "UnityEngine.UI.Dropdown");
        Add("Input Field", "UnityEngine.UI.InputField");
        Add("Scroll Rect", "UnityEngine.UI.ScrollRect");
        Add("Layout Element", "UnityEngine.UI.LayoutElement");
        Add("Content Size Fitter", "UnityEngine.UI.ContentSizeFitter");
        Add("Aspect Ratio Fitter", "UnityEngine.UI.AspectRatioFitter");
        Add("Horizontal Layout Group", "UnityEngine.UI.HorizontalLayoutGroup");
        Add("Vertical Layout Group", "UnityEngine.UI.VerticalLayoutGroup");
        Add("Grid Layout Group", "UnityEngine.UI.GridLayoutGroup");
        Add("Mask", "UnityEngine.UI.Mask");
        Add("Rect Mask 2D", "UnityEngine.UI.RectMask2D");
        Add("Shadow", "UnityEngine.UI.Shadow");
        Add("Outline", "UnityEngine.UI.Outline");
        Add("Position As UV1", "UnityEngine.UI.PositionAsUV1");

        Add("NavMesh Agent", "UnityEngine.AI.NavMeshAgent");
        Add("NavMesh Obstacle", "UnityEngine.AI.NavMeshObstacle");
        Add("Off Mesh Link", "UnityEngine.AI.OffMeshLink");

        Add("Animator", "UnityEngine.Animator");
        Add("Animation", "UnityEngine.Animation");
        Add("Playable Director", "UnityEngine.Playables.PlayableDirector");

        Add("Tilemap", "UnityEngine.Tilemaps.Tilemap");
        Add("Tilemap Renderer", "UnityEngine.Tilemaps.TilemapRenderer");
        Add("Tilemap Collider 2D", "UnityEngine.Tilemaps.TilemapCollider2D");
        Add("Grid", "UnityEngine.Grid");

        Add("Video Player", "UnityEngine.Video.VideoPlayer");

        Add("Terrain", "UnityEngine.Terrain");
        Add("Wind Zone", "UnityEngine.WindZone");
        Add("Tree", "UnityEngine.Tree");

        Add("Event System", "UnityEngine.EventSystems.EventSystem");
        Add("Standalone Input Module", "UnityEngine.EventSystems.StandaloneInputModule");
        Add("Physics Raycaster", "UnityEngine.EventSystems.PhysicsRaycaster");
        Add("Physics 2D Raycaster", "UnityEngine.EventSystems.Physics2DRaycaster");

        return entries;
    }
}
