namespace AhkWin32.Generator.Model.Types;

using AhkWin32.Generator.Model;

/// <summary>
/// Base class for all IR types in the model. Contains identity, documentation,
/// and metadata common to all type kinds. Pure data with no MetadataReader dependencies.
/// </summary>
public abstract class Win32Type
{
    /// <summary>The type's identity (FQN + Architecture).</summary>
    public required TypeIdentity Identity { get; init; }

    /// <summary>Display name (with deconfliction applied, e.g., "Win32string").</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The canonical name as it appears in metadata (before deconfliction).
    /// Used for file path generation.
    /// </summary>
    public required string CanonicalName { get; init; }

    /// <summary>The source assembly name (e.g., "Windows.Win32").</summary>
    public required string AssemblyName { get; init; }

    /// <summary>The metadata version string (e.g., "Windows.Win32 v10.0.26100.0").</summary>
    public required string MetadataVersion { get; init; }

    /// <summary>Member-level flags (Deprecated, Ansi, Unicode, etc.).</summary>
    public MemberFlags Flags { get; init; }

    /// <summary>Extension code blocks to append to this type's output.</summary>
    public List<ExtensionCode> Extensions { get; init; } = [];

    /// <summary>
    /// FQNs of types referenced by this type (for #Include generation).
    /// Mutable because transforms in Phase 2 may add to it.
    /// </summary>
    public List<string> ReferencedTypes { get; init; } = [];

    // --- Inline documentation (pre-resolved from ApiDetails + attributes) ---

    /// <summary>Summary description text.</summary>
    public string? Description { get; init; }

    /// <summary>Detailed remarks text.</summary>
    public string? Remarks { get; init; }

    /// <summary>URL to Microsoft documentation.</summary>
    public Uri? HelpLink { get; init; }

    /// <summary>Deprecation message from [ObsoleteAttribute]. Null means not deprecated via attribute.</summary>
    public string? DeprecationMessage { get; init; }

    /// <summary>Minimum OS version (from [SupportedOSPlatformAttribute]).</summary>
    public string? SupportedOSPlatform { get; init; }

    // --- Derived convenience properties ---

    /// <summary>Full namespace (e.g., "Windows.Win32.Foundation").</summary>
    public string Namespace => Identity.Namespace;

    /// <summary>Fully-qualified name (e.g., "Windows.Win32.Foundation.RECT").</summary>
    public string FQN => Identity.FQN;

    /// <summary>Architecture this variant applies to.</summary>
    public Architecture Arch => Identity.Arch;

    public bool IsDeprecated => Flags.HasFlag(MemberFlags.Deprecated);
    public bool IsAnsi => Flags.HasFlag(MemberFlags.Ansi);
    public bool IsUnicode => Flags.HasFlag(MemberFlags.Unicode);
    public bool IsAnonymous => Flags.HasFlag(MemberFlags.Anonymous);
}
