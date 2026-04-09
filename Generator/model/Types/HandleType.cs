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
}
