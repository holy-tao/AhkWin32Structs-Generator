using System.Collections.Concurrent;
using ConcurrentCollections;

namespace AhkWin32.Generator.Model;

/// <summary>
/// A thread-safe container for information about types and functions that need to be imported.
/// Type imports map to a whole file (e.g., a struct's .ahk file). Function imports name specific
/// free functions inside an Apis file, needed for v2.1 where functions aren't class members.
/// </summary>
public sealed class ImportCollection
{
    private readonly ConcurrentHashSet<string> _types = [];
    private readonly ConcurrentDictionary<string, ConcurrentHashSet<string>> _functions = [];

    public bool AddType(string fqn) => _types.Add(fqn);

    public int AddTypes(IEnumerable<string> fqns) => fqns.Count(_types.Add);

    public bool HasType(string fqn) => _types.Contains(fqn);

    public IEnumerable<string> GetTypes() => _types;

    public bool AddFunction(string apisFqn, string fnName)
    {
        var fns = _functions.GetOrAdd(apisFqn, _ => []);
        return fns.Add(fnName);
    }

    public int AddFunctions(string apisFqn, IEnumerable<string> fns) =>
        fns.Count(fn => AddFunction(apisFqn, fn));

    public IEnumerable<string> GetFunctionNamespaces() => _functions.Keys;

    public IEnumerable<string> GetFunctionsForNamespace(string apisFqn)
    {
        if (_functions.TryGetValue(apisFqn, out var fns))
            return fns;

        throw new KeyNotFoundException($"Import collection contains no functions for namespace {apisFqn}");
    }

    /// <summary>
    /// FQNs of every distinct file that needs to be included. For v2 emitters: union of
    /// type FQNs and function-Apis FQNs (one #Include per file).
    /// </summary>
    public IEnumerable<string> GetIncludeTargets() => _types.Concat(_functions.Keys).Distinct();

    /// <summary>
    /// Merge another collection's contents into this one.
    /// </summary>
    public void MergeFrom(ImportCollection other)
    {
        foreach (string fqn in other._types)
            _types.Add(fqn);
        foreach (var (apisFqn, fns) in other._functions)
        {
            foreach (string fn in fns)
                AddFunction(apisFqn, fn);
        }
    }

    /// <summary>
    /// Merge other collections' contents into this one.
    /// </summary>
    public void MergeFrom(IEnumerable<ImportCollection> others)
    {
        foreach (var collection in others)
            MergeFrom(collection);
    }
}
