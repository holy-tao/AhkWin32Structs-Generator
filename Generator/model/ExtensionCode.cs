namespace AhkWin32.Generator.Model;

/// <summary>
/// A block of extension code to be appended to a type's generated output.
/// Pre-resolved from YAML extension files during extraction.
/// </summary>
public sealed record ExtensionCode(
    /// <summary>The raw AHK code to inject.</summary>
    string Code,
    /// <summary>FQN strings of types required by this extension (for #Include generation).</summary>
    IReadOnlyList<string> Requirements
);
