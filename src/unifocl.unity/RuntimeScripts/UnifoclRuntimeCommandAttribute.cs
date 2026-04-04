using System;

namespace UniFocl.Runtime
{
    /// <summary>
    /// Marks a static method as a unifocl runtime command, making it discoverable at player
    /// startup by <see cref="RuntimeCommandRegistry"/>. The method's parameters are reflected
    /// to generate a typed manifest with JSON Schema validation.
    ///
    /// Methods must be static. They receive args as a raw JSON string (single parameter)
    /// or as individually-typed parameters (auto-deserialized). Return value is serialized
    /// via <c>JsonUtility.ToJson</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class UnifoclRuntimeCommandAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public RuntimeCommandKind Kind { get; }
        public RuntimeRiskLevel Risk { get; }

        public UnifoclRuntimeCommandAttribute(
            string name,
            string description,
            string category = "default",
            RuntimeCommandKind kind = RuntimeCommandKind.Query,
            RuntimeRiskLevel risk = RuntimeRiskLevel.SafeRead)
        {
            Name = name;
            Description = description;
            Category = category;
            Kind = kind;
            Risk = risk;
        }
    }
}
