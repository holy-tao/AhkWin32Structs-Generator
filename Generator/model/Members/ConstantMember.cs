namespace AhkWin32.Generator.Model.Members;

using AhkWin32.Generator.Model;

/// <summary>
/// A constant member (enum value or API constant).
/// </summary>
public sealed class ConstantMember
{
    /// <summary>Name of the constant.</summary>
    public required string Name { get; init; }

    /// <summary>The pre-decoded value.</summary>
    public required ConstantValue Value { get; init; }

    /// <summary>The resolved type of this constant (for documentation).</summary>
    public required ResolvedType Type { get; init; }

    /// <summary>Documentation description for this constant.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this constant is deprecated.</summary>
    public bool IsDeprecated { get; init; }

    /// <summary>Whether this constant requires a Guid import.</summary>
    public bool NeedsGuid { get; init; }

    /// <summary>
    /// FQNs of types referenced by this constant (e.g., struct types for struct constants).
    /// Used for #Include generation.
    /// </summary>
    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];
}
