namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model.Members;

/// <summary>
/// A Win32 enum type.
/// </summary>
public sealed class EnumType : Win32Type
{
    /// <summary>Enum constant values.</summary>
    public required IReadOnlyList<ConstantMember> Constants { get; init; }

    /// <summary>Whether this enum is a [Flags] enum (bitfield).</summary>
    public required bool IsFlags { get; init; }

    /// <summary>The underlying primitive type name (e.g., "Int32", "UInt32").</summary>
    public required string UnderlyingTypeName { get; init; }
}
