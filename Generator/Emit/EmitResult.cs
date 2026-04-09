namespace AhkWin32.Generator.Emit;

/// <summary>
/// Result of emitting a single type to AHK.
/// </summary>
public sealed record EmitResult(
    /// <summary>The generated AHK source text.</summary>
    string Content,
    /// <summary>The desired output file path (absolute).</summary>
    string FilePath
);
