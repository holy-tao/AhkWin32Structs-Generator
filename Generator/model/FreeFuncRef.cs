namespace AhkWin32.Generator.Model;

/// <summary>
/// A reference to a free/release function for handle cleanup or parameter release.
/// Used by HandleType (RAII free) and ParameterMember (FreeWith/RAIIFree attributes).
/// </summary>
public sealed record FreeFuncRef(
    /// <summary>Simple name of the free function (e.g., "CloseHandle").</summary>
    string Name,
    /// <summary>Namespace of the Apis type containing the function (e.g., "Windows.Win32.Foundation").</summary>
    string Namespace,
    /// <summary>FQN of the Apis type (e.g., "Windows.Win32.Foundation.Apis").</summary>
    string ApisFQN
)
{
    /// <summary>
    /// The short name of the declaring Apis class (last namespace segment).
    /// E.g., "Foundation" for "Windows.Win32.Foundation".
    /// </summary>
    public string DeclarerName => Namespace.Split('.')[^1];
}
