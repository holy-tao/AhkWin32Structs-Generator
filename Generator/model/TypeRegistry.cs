namespace AhkWin32.Generator.Model;

using System.Collections.Concurrent;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Central store for all extracted IR types. Supports architecture-aware lookup
/// and various query patterns needed by transforms and emitters.
/// </summary>
public class TypeRegistry
{
    private readonly ConcurrentDictionary<TypeIdentity, Win32Type> _types = [];
    private readonly ConcurrentDictionary<string, List<Win32Type>> _byFqn = [];

    /// <summary>Total number of type entries (including architecture variants).</summary>
    public int Count => _types.Count;

    /// <summary>Register a type in the registry.</summary>
    public void Register(Win32Type type)
    {
        _types[type.Identity] = type;

        if (!_byFqn.TryGetValue(type.FQN, out var list))
        {
            list = [];
            _byFqn[type.FQN] = list;
        }
        list.Add(type);
    }

    /// <summary>
    /// Resolve a type by FQN for a target architecture.
    /// Returns the architecture-specific variant if one exists, otherwise the Universal variant.
    /// Returns null if not found.
    /// </summary>
    public Win32Type? Resolve(string fqn, Architecture target)
    {
        if (_byFqn.TryGetValue(fqn, out var variants))
        {
            // Prefer exact architecture match
            Win32Type? exact = variants.FirstOrDefault(v => v.Arch.HasFlag(target));
            if (exact is not null)
                return exact;

            // Fall back to universal
            return variants.FirstOrDefault(v => v.Arch == Architecture.All);
        }
        return null;
    }

    /// <summary>
    /// Resolve a type of the given type <c>T</c> by FQN for a target architecture.
    /// </summary>
    public T? Resolve<T>(string fqn, Architecture target)
        where T : Win32Type
    {
        if (_byFqn.TryGetValue(fqn, out var variants))
        {
            // Prefer exact architecture match
            T? exact = variants.OfType<T>().FirstOrDefault(v => v != null && v.Arch.HasFlag(target), null);
            if (exact is T t)
                return t;

            // Fall back to universal
            return variants.OfType<T>().FirstOrDefault(v => v is { Arch: Architecture.All }, null);
        }
        return null;
    }

    /// <summary>Get a type by exact identity (FQN + arch).</summary>
    public Win32Type? Get(TypeIdentity identity)
    {
        _types.TryGetValue(identity, out var type);
        return type;
    }

    /// <summary>Get a type by exact identity (FQN + arch).</summary>
    public T? Get<T>(TypeIdentity identity)
        where T : Win32Type
    {
        _types.TryGetValue(identity, out var type);
        return type is T t ? t : null;
    }

    /// <summary>Get all architecture variants for a given FQN.</summary>
    public IReadOnlyList<Win32Type> GetAllVariants(string fqn)
    {
        return _byFqn.TryGetValue(fqn, out var list) ? list : [];
    }

    /// <summary>Get all architecture variants of a specific kind for a given FQN.</summary>
    public IReadOnlyList<T> GetAllVariants<T>(string fqn)
        where T : Win32Type
    {
        return _byFqn.TryGetValue(fqn, out var list) ? [.. list.OfType<T>()] : [];
    }

    /// <summary>Get all types in a given namespace.</summary>
    public IEnumerable<Win32Type> GetByNamespace(string ns)
    {
        return _types.Values.Where(t => t.Namespace == ns);
    }

    /// <summary>Get all types of a specific kind in a given namespace.</summary>
    public IEnumerable<T> GetByNamespace<T>(string ns)
        where T : Win32Type
    {
        return _types.Values.Where(t => t.Namespace == ns).OfType<T>();
    }

    /// <summary>Get all types from a given source assembly.</summary>
    public IEnumerable<Win32Type> GetByAssembly(string assembly)
    {
        return _types.Values.Where(t => t.AssemblyName == assembly);
    }

    /// <summary>Get all types of a specific kind from a given source assembly.</summary>
    public IEnumerable<T> GetByAsembly<T>(string assembly)
        where T : Win32Type
    {
        return _types.Values.Where(t => t.AssemblyName == assembly).OfType<T>();
    }

    /// <summary>Get all types of a specific kind.</summary>
    public IEnumerable<T> GetAll<T>()
        where T : Win32Type
    {
        return _types.Values.OfType<T>();
    }

    /// <summary>Get all registered types.</summary>
    public IEnumerable<Win32Type> GetAll()
    {
        return _types.Values;
    }

    /// <summary>
    /// Create a new registry containing only types that match the predicate.
    /// Referenced types are NOT automatically included — use TypeFilter for dependency closure.
    /// </summary>
    public TypeRegistry Filter(Func<Win32Type, bool> predicate)
    {
        TypeRegistry filtered = new();
        foreach (var type in _types.Values.Where(predicate))
        {
            filtered.Register(type);
        }
        return filtered;
    }

    /// <summary>
    /// Remove a type and all its architecture variants from the registry.
    /// Returns the number of variants removed.
    /// </summary>
    public int Remove(string fqn)
    {
        if (!_byFqn.TryRemove(fqn, out var variants))
            return 0;

        int removed = 0;
        foreach (var type in variants)
        {
            if (_types.TryRemove(type.Identity, out _))
                removed++;
        }
        return removed;
    }

    /// <summary>Check if a type with the given FQN exists.</summary>
    public bool Contains(string fqn) => _byFqn.ContainsKey(fqn);

    /// <summary>Check if a type of a specific kind with the given FQN exists.</summary>
    public bool Contains<T>(string fqn)
        where T : Win32Type => _byFqn.TryGetValue(fqn, out var list) && list.OfType<T>().Any();

    /// <summary>Check if a type with the given identity exists.</summary>
    public bool Contains(TypeIdentity identity) => _types.ContainsKey(identity);

    /// <summary>Check if a type of a specific kind with the given identity exists.</summary>
    public bool Contains<T>(TypeIdentity identity)
        where T : Win32Type => _types.TryGetValue(identity, out var type) && type is T;
}
