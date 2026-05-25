namespace AhkWin32.Generator.Model;

using AhkWin32.Generator.Metadata;

/// <summary>
/// A block of extension code to be appended to a type's generated output.
/// Pre-resolved from YAML extension files during extraction.
/// </summary>
public sealed record ExtensionCode(
    /// <summary>
    /// Per-version AHK code to inject. A version absent from the map means the extension
    /// is explicitly opted-out for that target (the yml file said `skip`).
    /// </summary>
    IReadOnlyDictionary<AhkVersion, string> CodeByVersion,
    /// <summary>
    /// Imports required by this extension's code. Keys are FQNs. An empty value list means
    /// "whole-file include" (routed to <see cref="ImportCollection.AddType"/>); a non-empty
    /// list names specific functions from an Apis container (routed to
    /// <see cref="ImportCollection.AddFunctions"/>).
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Imports
);
