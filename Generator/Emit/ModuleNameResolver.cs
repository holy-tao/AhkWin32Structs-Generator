namespace AhkWin32.Generator.Emit;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves the local identifier to use for each imported type/function within a single
/// v2.1 module file, deconflicting names that collide with the module's own exported
/// declarations (or with each other).
///
/// This resolver keeps the module's own exports (the public API) unchanged and aliases the
/// *imported* name instead, emitting <c>{ Name as Alias }</c>. Aliases are formed by
/// suffixing the type kind (<c>INITCOMMONCONTROLSEX_struct</c>, <c>SOCKET_handle</c>),
/// falling back to a numeric suffix if that still collides.
/// </summary>
public sealed class ModuleNameResolver
{
    private readonly Dictionary<string, string> _typeLocal = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ApisFqn, string Name), string> _functionLocal = [];

    /// <summary>
    /// Build a resolver for one module file.
    /// </summary>
    /// <param name="anchorNames">
    /// The module's own exported declarations (function or constant names). These always keep
    /// their names; imports are aliased away from them.
    /// </param>
    /// <param name="typeFqns">FQNs of the types this file imports.</param>
    /// <param name="functionImports">Per-Apis-FQN free functions this file imports.</param>
    /// <param name="registry">Used to look up each imported type's kind for the alias suffix.</param>
    /// <param name="logger">Optional - logs a line per generated alias (the detection signal).</param>
    /// <param name="context">Human-readable file/namespace label for log messages.</param>
    public ModuleNameResolver(
        IEnumerable<string> anchorNames,
        IEnumerable<string> typeFqns,
        IEnumerable<(string ApisFqn, IEnumerable<string> Names)> functionImports,
        TypeRegistry registry,
        ILogger? logger = null,
        string context = ""
    )
    {
        // Names that are already taken in the module scope; never aliased. Case-insensitive.
        var occupied = new HashSet<string>(anchorNames, StringComparer.OrdinalIgnoreCase);

        // Deterministic order so output is stable across runs.
        foreach (string fqn in typeFqns.OrderBy(f => f, StringComparer.Ordinal))
        {
            string simple = ImportResolver.GetImportName(fqn);
            string suffix = KindSuffix(registry.Resolve(fqn, Architecture.All));
            _typeLocal[fqn] = Claim(simple, suffix, occupied, logger, context);
        }

        foreach (var (apisFqn, names) in functionImports.OrderBy(f => f.ApisFqn, StringComparer.Ordinal))
        {
            foreach (string name in names.OrderBy(n => n, StringComparer.Ordinal))
            {
                _functionLocal[(apisFqn, name)] = Claim(name, "fn", occupied, logger, context);
            }
        }
    }

    /// <summary>Local identifier to use for an imported type, by FQN.</summary>
    public string ForType(string fqn) =>
        _typeLocal.TryGetValue(fqn, out string? local) ? local : ImportResolver.GetImportName(fqn);

    /// <summary>Local identifier to use for an imported free function.</summary>
    public string ForFunction(string apisFqn, string name) =>
        _functionLocal.TryGetValue((apisFqn, name), out string? local) ? local : name;

    /// <summary>
    /// The member token to place inside an <c>#Import "path" { ... }</c> for a type: either
    /// <c>Name</c> or <c>Name as Alias</c> when the import was deconflicted.
    /// </summary>
    public string TypeImportToken(string fqn)
    {
        string simple = ImportResolver.GetImportName(fqn);
        string local = ForType(fqn);
        return local == simple ? simple : $"{simple} as {local}";
    }

    /// <summary>The member token for a free-function import: <c>name</c> or <c>name as alias</c>.</summary>
    public string FunctionImportToken(string apisFqn, string name)
    {
        string local = ForFunction(apisFqn, name);
        return local == name ? name : $"{name} as {local}";
    }

    private static string Claim(
        string name,
        string kindSuffix,
        HashSet<string> occupied,
        ILogger? logger,
        string context
    )
    {
        if (occupied.Add(name))
            return name;

        string candidate = $"{name}_{kindSuffix}";
        if (!occupied.Add(candidate))
        {
            int n = 2;
            while (!occupied.Add($"{candidate}_{n}"))
                n++;
            candidate = $"{candidate}_{n}";
        }

        logger?.LogDebug("Deconflicted import '{Original}' -> '{Alias}' in {Context}", name, candidate, context);
        return candidate;
    }

    private static string KindSuffix(Win32Type? type) =>
        type switch
        {
            HandleType => "handle",
            StructType => "struct",
            EnumType => "enum",
            NativeTypedefType => "typedef",
            ComInterfaceType => "com",
            DelegateType => "delegate",
            _ => "type",
        };
}
