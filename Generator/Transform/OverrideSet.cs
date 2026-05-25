namespace AhkWin32.Generator.Transform;

using System.Collections.Frozen;
using AhkWin32.Generator.Model;

/// <summary>
/// Immutable lookup structure holding all parsed overrides, indexed by type FQN.
/// Built by <see cref="OverrideReader"/>, consumed by <see cref="OverrideApplier"/>.
/// </summary>
public sealed class OverrideSet
{
    public static readonly OverrideSet Empty = new(FrozenDictionary<string, TypeOverride>.Empty);

    private readonly FrozenDictionary<string, TypeOverride> _byFqn;

    public OverrideSet(FrozenDictionary<string, TypeOverride> byFqn)
    {
        _byFqn = byFqn;
    }

    public TypeOverride? GetOverride(string fqn) => _byFqn.TryGetValue(fqn, out var ov) ? ov : null;

    public bool HasOverride(string fqn) => _byFqn.ContainsKey(fqn);

    public int Count => _byFqn.Count;

    public IEnumerable<TypeOverride> All => _byFqn.Values;
}

/// <summary>
/// Override entry scoped to a single type (identified by FQN).
/// </summary>
public sealed record TypeOverride(
    string FQN,
    bool Skip,
    string? StructSizeField,
    IReadOnlyDictionary<string, FieldOverride>? Fields,
    IReadOnlyDictionary<string, MethodOverride>? Methods,
    IReadOnlyList<AddMethodRef>? AddMethods
);

/// <summary>
/// Override for a struct/type field — adds attribute flags.
/// </summary>
public sealed record FieldOverride(MemberFlags AddAttributes);

/// <summary>
/// Override for a method — can skip the method or override its parameters.
/// </summary>
public sealed record MethodOverride(bool Skip, IReadOnlyDictionary<string, ParameterOverride>? Parameters);

/// <summary>
/// Override for a method parameter — adds attribute flags.
/// </summary>
public sealed record ParameterOverride(ParameterFlags AddAttributes);

/// <summary>
/// Reference to a method to clone from another ApiType into the target.
/// </summary>
public sealed record AddMethodRef(string SourceFQN, string MethodName);
