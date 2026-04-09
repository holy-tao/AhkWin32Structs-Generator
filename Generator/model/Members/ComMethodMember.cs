namespace AhkWin32.Generator.Model.Members;

/// <summary>
/// A COM interface method (extends MethodMember with vtable information).
/// </summary>
public sealed class ComMethodMember : MethodMember
{
    /// <summary>Index into the COM vtable.</summary>
    public required int VTableIndex { get; init; }

    /// <summary>Whether this method has a BSTR parameter (needs BSTR import).</summary>
    public bool HasStringParam { get; init; }

    /// <summary>Whether this is a special name method (get_X, put_X for properties).</summary>
    public bool IsSpecialName { get; init; }

    /// <summary>
    /// Deduplicated name (handles overloaded method names by appending a counter).
    /// Pre-computed during extraction.
    /// </summary>
    public required string DeduplicatedName { get; init; }
}
