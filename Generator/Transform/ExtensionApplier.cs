namespace AhkWin32.Generator.Transform;

using AhkWin32.Generator.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads YAML extensions and attaches them to types in the TypeRegistry.
/// Runs between extraction (Phase 1) and emission (Phase 3).
/// </summary>
public sealed class ExtensionApplier
{
    private readonly ExtensionReader _reader;
    private readonly ILogger<ExtensionApplier> _logger;

    public ExtensionApplier(ExtensionReader reader, ILogger<ExtensionApplier> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    /// <summary>
    /// Load extensions from the given directory and attach them to matching types in the registry.
    /// </summary>
    public void Apply(TypeRegistry registry, string extensionDirectoryPath)
    {
        var extensionsByFqn = _reader.LoadExtensions(extensionDirectoryPath);

        int totalExtensions = 0;
        int matchedTypes = 0;
        int unmatchedFqns = 0;

        foreach (var (fqn, extensions) in extensionsByFqn)
        {
            var variants = registry.GetAllVariants(fqn);
            if (variants.Count == 0)
            {
                _logger.LogWarning("Extension targets type not in registry: {FQN}", fqn);
                unmatchedFqns++;
                continue;
            }

            foreach (var type in variants)
            {
                type.Extensions.AddRange(extensions);
                foreach (var ext in extensions)
                foreach (var (importFqn, names) in ext.Imports)
                {
                    if (names.Count == 0)
                        type.Imports.AddType(importFqn);
                    else
                        type.Imports.AddFunctions(importFqn, names);
                }
            }

            matchedTypes++;
            totalExtensions += extensions.Count;
        }

        _logger.LogInformation(
            "Applied {ExtCount} extension(s) to {TypeCount} type(s){Unmatched}",
            totalExtensions,
            matchedTypes,
            unmatchedFqns > 0 ? $" ({unmatchedFqns} unmatched FQN(s))" : ""
        );
    }
}
