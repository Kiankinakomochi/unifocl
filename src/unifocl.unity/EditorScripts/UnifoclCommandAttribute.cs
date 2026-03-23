#if UNITY_EDITOR
using System;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Tags a static editor method as a unifocl custom tool, making it discoverable
    /// by <see cref="UnifoclManifestGenerator"/> at compile time and loadable via the
    /// MCP deferred-category system.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class UnifoclCommandAttribute : Attribute
    {
        public string Name        { get; }
        public string Description { get; }
        public string Category    { get; }

        public UnifoclCommandAttribute(string name, string description, string category = "Default")
        {
            Name        = name;
            Description = description;
            Category    = category;
        }
    }
}
#endif
