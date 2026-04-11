namespace AhkWin32.Generator.Model.Members;

using AhkWin32.Generator.Model;

/// <summary>
/// A method (DllImport function or COM method).
/// </summary>
public class MethodMember
{
    /// <summary>Method name.</summary>
    public required string Name { get; init; }

    /// <summary>Namespace of the declaring type.</summary>
    public required string Namespace { get; init; }

    /// <summary>Simple name of the declaring type (e.g., "Foundation" for Apis).</summary>
    public string DeclarerName => Namespace.Split('.')[^1];

    /// <summary>DLL name (e.g., "kernel32.dll"). Empty for COM methods.</summary>
    public string DllName { get; init; } = "";

    /// <summary>DLL entry point (e.g., "CreateFileW" or "#123" for ordinals). Empty for COM methods.</summary>
    public string EntryPoint { get; init; } = "";

    /// <summary>Whether the entry point is an ordinal (starts with '#').</summary>
    public bool IsOrdinal => EntryPoint.StartsWith('#');

    /// <summary>Calling convention.</summary>
    public CallingConvention CallingConvention { get; init; } = CallingConvention.StdCall;

    /// <summary>Character set.</summary>
    public StringEncoding CharSet { get; init; } = StringEncoding.None;

    /// <summary>Whether the function sets Win32 last error.</summary>
    public bool SetsLastError { get; init; }

    /// <summary>Whether [PreserveSig] is present.</summary>
    public bool PreserveSig { get; init; }

    /// <summary>Whether [CanReturnErrorsAsSuccess] is present.</summary>
    public bool CanReturnErrorsAsSuccess { get; init; }

    /// <summary>Whether [CanReturnMultipleSuccessValues] is present.</summary>
    public bool CanReturnMultipleSuccessValues { get; init; }

    /// <summary>Whether the method accepts variadic arguments (__arglist).</summary>
    public bool IsVariadic { get; init; }

    /// <summary>
    /// The deconflicted name for the variadic parameter (e.g., "args" or "_args" if "args" conflicts).
    /// Empty string if not variadic.
    /// </summary>
    public string VariadicParamName
    {
        get
        {
            if (!IsVariadic)
                throw new InvalidOperationException($"Cannot get variadic param name for non-variadic method {Namespace}.{Name}");

            var paramNames = new HashSet<string>(
                Parameters.Skip(1).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);
            
            string name = "args";
            while (paramNames.Contains(name))
                name = "_" + name;

            return name;
        }
    }

    /// <summary>
    /// All parameters, including the return value at index 0.
    /// Parameters[0] is always the return type (may be Void).
    /// Parameters[1..] are the actual function parameters.
    /// </summary>
    public required IReadOnlyList<ParameterMember> Parameters { get; init; }

    /// <summary>
    /// The logical output parameter, if the method's return value should be collapsed.
    /// Null if no output parameter collapsing should occur.
    /// Pre-determined during extraction.
    /// </summary>
    public ParameterMember? OutputParameter { get; init; }

    // --- Inline documentation ---

    /// <summary>Summary description text.</summary>
    public string? Description { get; init; }

    /// <summary>Detailed remarks text.</summary>
    public string? Remarks { get; init; }

    /// <summary>URL to Microsoft documentation.</summary>
    public Uri? HelpLink { get; init; }

    /// <summary>Deprecation message, if deprecated.</summary>
    public string? DeprecationMessage { get; init; }

    /// <summary>Return value description.</summary>
    public string? ReturnValueDoc { get; init; }

    /// <summary>Minimum OS version (from [SupportedOSPlatformAttribute]).</summary>
    public string? SupportedOSPlatform { get; init; }

    // --- Pre-computed flags ---

    /// <summary>Whether the function has a non-void return value.</summary>
    public bool HasReturnValue => Parameters.Count > 0
        && Parameters[0].Type is not PrimitiveType { Name: "Void" };

    /// <summary>
    /// Whether this method's HRESULT return should throw automatically.
    /// Pre-computed during extraction (mirrors ShouldThrowForReturnValue logic).
    /// </summary>
    public bool ShouldThrowOnHResult { get; init; }

    /// <summary>
    /// FQNs of types referenced by this method (for #Include generation).
    /// </summary>
    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];
}
