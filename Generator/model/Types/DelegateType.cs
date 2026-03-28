namespace AhkWin32.Generator.Model.Types;

/// <summary>
/// A delegate/function pointer type (placeholder for future use).
/// Currently these are treated as pointers during extraction, but having the
/// type in the IR allows future expansion.
/// </summary>
public sealed class DelegateType : Win32Type
{
    /// <summary>The function signature as a human-readable string.</summary>
    public required string Signature { get; init; }
}
