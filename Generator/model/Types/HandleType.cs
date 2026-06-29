namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model;

/// <summary>
/// A Win32 handle type (extends StructType with handle-specific semantics).
/// </summary>
public sealed class HandleType : StructType
{
    /// <summary>
    /// List of invalid handle sentinel values (e.g., 0, -1).
    /// From [InvalidHandleValueAttribute].
    /// </summary>
    public required IReadOnlyList<long> InvalidValues { get; init; }

    /// <summary>
    /// Reference to the free function (from [RAIIFreeAttribute]).
    /// Null if no RAII free function is defined or if it has != 2 parameters.
    /// </summary>
    public FreeFuncRef? FreeFunc { get; init; }

    /// <summary>
    /// True when some function returns or outputs this handle owned with a context-specific
    /// <c>[RAIIFree]</c> that differs from <see cref="FreeFunc"/>, so the emitter must generate an
    /// <c>OwnedWith(freeFunc)</c> factory. Set in Transform by <c>OwnedHandleResolver</c> (it needs
    /// every handle's resolved default free function, so it can't be computed during extraction).
    /// </summary>
    public bool NeedsOwnedWith { get; set; }

    /// <summary>
    /// Optional AHK expression for a restored <c>__value</c> getter, where <c>$field</c> resolves
    /// to <c>this.&lt;member&gt;</c>. Null = no getter (default; preserves type identity). Set in
    /// Transform from a <c>value-accessor</c> override.
    /// </summary>
    public string? ValueGetterExpr { get; set; }

    /// <summary>
    /// Optional AHK expression that transforms a raw incoming value in the <c>__value</c> setter's
    /// else branch, where <c>$value</c> resolves to the setter's <c>value</c>. Null = store the
    /// value unchanged. Set in Transform from a <c>value-accessor</c> override.
    /// </summary>
    public string? ValueSetterCoerceExpr { get; set; }
}
