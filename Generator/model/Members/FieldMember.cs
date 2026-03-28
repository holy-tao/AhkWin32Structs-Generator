namespace AhkWin32.Generator.Model.Members;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// A bitfield member within a native bitfield-backed field.
/// </summary>
public sealed record BitfieldMember(string Name, long Offset, long Length);

/// <summary>
/// A field/member of a struct type. Fully resolved with no metadata dependencies.
/// </summary>
public sealed class FieldMember
{
    /// <summary>Display name of the field.</summary>
    public required string Name { get; set; }

    /// <summary>Byte offset within the parent struct.</summary>
    public required int Offset { get; set; }

    /// <summary>Size in bytes of this field.</summary>
    public required int Size { get; init; }

    /// <summary>The resolved type of this field.</summary>
    public required ResolvedType Type { get; init; }

    /// <summary>Member flags (deprecated, reserved, alignment, union, anonymous, bitfield).</summary>
    public MemberFlags Flags { get; init; }

    /// <summary>Documentation description for this field.</summary>
    public string? Description { get; init; }

    /// <summary>Deprecation message if deprecated and one is set in the [ObsoleteAttribute].</summary>
    public string? DeprecationMessage { get; init; }

    /// <summary>
    /// For struct-typed or array-of-struct-typed fields: the embedded struct.
    /// This is a direct StructType reference (not a StructRef FQN) because
    /// nested/anonymous structs are inlined during extraction and not registered
    /// in the TypeRegistry.
    /// </summary>
    public StructType? EmbeddedStruct { get; init; }

    /// <summary>
    /// Bitfield members, if this field is a NativeBitField.
    /// Empty list if not a bitfield.
    /// </summary>
    public IReadOnlyList<BitfieldMember> Bitfields { get; init; } = [];

    /// <summary>Whether this field represents a nested struct (anonymous union or named nested type).</summary>
    public bool IsNested { get; init; }

    // Convenience
    public bool IsReserved => Flags.HasFlag(MemberFlags.Reserved);
    public bool IsAlignment => Flags.HasFlag(MemberFlags.Alignment);
    public bool IsUnion => Flags.HasFlag(MemberFlags.Union);
    public bool IsAnonymous => Flags.HasFlag(MemberFlags.Anonymous);
    public bool IsBitField => Flags.HasFlag(MemberFlags.NativeBitField);
    public bool IsDeprecated => Flags.HasFlag(MemberFlags.Deprecated);
}
