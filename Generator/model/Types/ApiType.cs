namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model.Members;

/// <summary>
/// The "Apis" type containing free functions and constants for a namespace.
/// </summary>
public sealed class ApiType : Win32Type
{
    /// <summary>Constants defined in this API type.</summary>
    public required IReadOnlyList<ConstantMember> Constants { get; init; }

    /// <summary>Methods (DllImport functions) defined in this API type.</summary>
    public required List<MethodMember> Methods { get; init; }

    /// <summary>
    /// The display name for code generation. For Apis types this is the last segment
    /// of the namespace (e.g., "Foundation" for Windows.Win32.Foundation.Apis).
    /// </summary>
    public string DisplayName => Namespace.Split('.')[^1];

    /// <summary>Whether any constant requires a Guid import.</summary>
    public bool NeedsGuid => Constants.Any(c => c.NeedsGuid);
}
