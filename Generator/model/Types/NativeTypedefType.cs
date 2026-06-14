namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model;

/// <summary>
/// A NativeTypedef declared in the metadata (e.g. BOOL, NTSTATUS, BSTR, DWORD).
/// In Win32 metadata these are types marked [NativeTypedefAttribute] with a single
/// field carrying the underlying value. Emitted in v2.1 as a `struct` with a
/// `value` field plus `__value` get/set so DllCall and assignment treat the
/// instance transparently as the underlying value.
/// </summary>
public sealed class NativeTypedefType : Win32Type
{
    /// <summary>The underlying primitive/pointer type backing this typedef.</summary>
    public required ResolvedType Underlying { get; init; }

    /// <summary>
    /// Optional AHK expression for a restored <c>__value</c> getter (e.g. BOOL <c>!!$field</c>),
    /// where <c>$field</c> resolves to <c>this.value</c>. Null = no getter (default; preserves
    /// type identity). Set in Transform from a <c>value-accessor</c> override.
    /// </summary>
    public string? ValueGetterExpr { get; set; }

    /// <summary>
    /// Optional AHK expression that transforms a raw incoming value in the <c>__value</c> setter's
    /// else branch (e.g. BOOL <c>!!$value</c>), where <c>$value</c> resolves to the setter's
    /// <c>value</c>. Null = store the value unchanged. Set in Transform from a
    /// <c>value-accessor</c> override.
    /// </summary>
    public string? ValueSetterCoerceExpr { get; set; }
}
