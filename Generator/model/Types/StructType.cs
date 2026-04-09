namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;

/// <summary>
/// A Win32 struct type with layout information.
/// </summary>
public class StructType : Win32Type
{
    /// <summary>Total size of the struct in bytes.</summary>
    public required int Size { get; init; }

    /// <summary>Packing alignment size.</summary>
    public required int PackingSize { get; init; }

    /// <summary>Layout kind (Sequential, Explicit, Auto).</summary>
    public required StructLayoutKind LayoutKind { get; init; }

    /// <summary>Ordered list of struct members/fields.</summary>
    public required IReadOnlyList<FieldMember> Members { get; init; }

    /// <summary>Whether this struct is a union (all members at offset 0).</summary>
    public bool IsUnion => Flags.HasFlag(MemberFlags.Union);

    /// <summary> Whether this struct is nested or not </summary>
    public required bool IsNested { get; init; }

    /// <summary>
    /// Name of the struct size field if [StructSizeFieldAttribute] is present.
    /// When set, the emitter generates a __New method that initializes this field to Size.
    /// E.g., "cbSize" for WNDCLASSEXW.
    /// </summary>
    public string? StructSizeFieldName { get; set; }
}
