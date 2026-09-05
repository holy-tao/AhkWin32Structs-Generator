namespace AhkWin32.Generator.Model.Members;

using AhkWin32.Generator.Model;

/// <summary>
/// A constant member (enum value or API constant).
/// </summary>
public sealed class ConstantMember
{
    /// <summary>Name of the constant.</summary>
    /// <remarks>
    /// Settable because <see cref="Transform.EnumPrefixStripper"/> renames enum constants in place
    /// during the transform phase. When it does, it records the metadata name in <see cref="NativeName"/>.
    /// </remarks>
    public required string Name { get; set; }

    /// <summary>
    /// The original metadata name, set only when a transform has renamed this constant.
    /// Null when <see cref="Name"/> is unchanged. Emitted into the doc comment so the
    /// original C identifier stays greppable against the Microsoft docs.
    /// </summary>
    public string? NativeName { get; set; }

    /// <summary>The pre-decoded value.</summary>
    public required ConstantValue Value { get; init; }

    /// <summary>The resolved type of this constant (for documentation).</summary>
    public required ResolvedType Type { get; init; }

    /// <summary>Documentation description for this constant.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this constant is deprecated.</summary>
    public bool IsDeprecated { get; init; }

    /// <summary>Deprecation message from [ObsoleteAttribute], if any.</summary>
    public string? DeprecationMessage { get; init; }

    /// <summary>Whether this constant requires a Guid import.</summary>
    public bool NeedsGuid { get; init; }

    /// <summary>
    /// Types and functions referenced by this constant (e.g., struct types for struct constants).
    /// Used for #Include / #Import generation.
    /// </summary>
    public ImportCollection Imports { get; init; } = new();
}
