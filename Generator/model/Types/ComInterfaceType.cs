namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model.Members;

/// <summary>
/// A COM interface type.
/// </summary>
public sealed class ComInterfaceType : Win32Type
{
    /// <summary>Interface identifier (IID), if present.</summary>
    public Guid? IID { get; init; }

    /// <summary>Class identifier (CLSID) for an instantiatable implementing class, if any.</summary>
    public Guid? CLSID { get; init; }

    /// <summary>
    /// FQN of the base interface (e.g., "Windows.Win32.System.Com.IUnknown").
    /// Null for root interfaces (IUnknown itself).
    /// </summary>
    public string? BaseInterfaceFQN { get; init; }

    /// <summary>
    /// Simple name of the base interface (e.g., "IUnknown").
    /// Null for root interfaces.
    /// </summary>
    public string? BaseInterfaceName { get; init; }

    /// <summary>Methods defined on this interface (not inherited).</summary>
    public required IReadOnlyList<ComMethodMember> Methods { get; init; }

    /// <summary>Properties (backed by get_/put_ special-name methods).</summary>
    public required IReadOnlyList<ComPropertyMember> Properties { get; init; }

    /// <summary>
    /// Offset into the vtable where this interface's methods begin
    /// (total method count of all ancestor interfaces).
    /// </summary>
    public required int VTableOffset { get; init; }
}
