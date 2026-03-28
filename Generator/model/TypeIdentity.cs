namespace AhkWin32.Generator.Model;

/// <summary>
/// Composite key for architecture-aware type lookup.
/// Types with the same FQN but different architectures are distinct entries.
/// </summary>
public sealed record TypeIdentity(string FQN, Architecture Arch)
{
    /// <summary>Create a TypeIdentity for a type that is the same on all architectures.</summary>
    public static TypeIdentity Universal(string fqn) => new(fqn, Architecture.All);

    /// <summary>The simple name (last segment after the final dot).</summary>
    public string Name => FQN.Contains('.') ? FQN[(FQN.LastIndexOf('.') + 1)..] : FQN;

    /// <summary>The namespace (everything before the final dot).</summary>
    public string Namespace => FQN.Contains('.') ? FQN[..FQN.LastIndexOf('.')] : string.Empty;

    public override string ToString() => Arch == Architecture.All ? FQN : $"{FQN} [{Arch}]";
}
