using AhkWin32.Generator.Model.Members;

namespace AhkWin32.Generator.Model.Types;

/// <summary>
/// A delegate/function pointer type. A delegate is a type carrying name information
/// and an Invoke method that holds the information about its signature.
/// </summary>
public sealed class DelegateType : Win32Type
{
    /// <summary>The delegate's method.</summary>
    public required MethodMember Invoke { get; init; }

    public required CallingConvention CallingConvention { get; init; }
}
