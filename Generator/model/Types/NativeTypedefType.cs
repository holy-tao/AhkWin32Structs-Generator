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
}
