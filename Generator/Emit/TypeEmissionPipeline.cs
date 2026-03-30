namespace AhkWin32.Generator.Emit;

using System.Diagnostics;
using AhkWin32.Generator.Emit.Emitters;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates type emission: iterates the TypeRegistry, matches each type
/// to an appropriate ITypeEmitter, and writes the generated .ahk files.
/// </summary>
public sealed class TypeEmissionPipeline
{
    private readonly ILogger<TypeEmissionPipeline> _logger;
    private readonly ITypeEmitter[] _emitters;

    public TypeEmissionPipeline(IEnumerable<ITypeEmitter> emitters, ILogger<TypeEmissionPipeline> logger)
    {
        _emitters = emitters.ToArray();
        _logger = logger;
    }

    /// <summary>
    /// Emit all types from the registry that match a registered emitter,
    /// optionally filtered by namespace prefixes.
    /// </summary>
    public (int Emitted, int Skipped, int Errors) EmitAll(
        TypeRegistry registry, string outputDir, string[]? namespaceFilter = null)
    {
        int emitted = 0, skipped = 0, errors = 0;
        Stopwatch watch = Stopwatch.StartNew();

        foreach (Win32Type type in registry.GetAll())
        {
            if (namespaceFilter is { Length: > 0 } &&
                !namespaceFilter.Any(prefix =>
                    type.Namespace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ITypeEmitter? emitter = FindEmitter(type);
            if (emitter is null)
            {
                skipped++;
                continue;
            }

            try
            {
                EmitResult result = emitter.Emit(type, outputDir);
                string dirPath = Path.GetDirectoryName(result.FilePath)!;
                Directory.CreateDirectory(dirPath);
                File.WriteAllText(result.FilePath, result.Content);
                emitted++;

                if (emitted % 1000 == 0)
                    _logger.LogInformation("  Emitted {Count} files...", emitted);
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogError(ex, "Failed to emit {TypeName}", type.FQN);
            }
        }

        watch.Stop();
        _logger.LogInformation("Emission complete: {Emitted} emitted, {Skipped} skipped, {Errors} errors in {Elapsed:F1}s",
            emitted, skipped, errors, watch.Elapsed.TotalSeconds);

        return (emitted, skipped, errors);
    }

    private ITypeEmitter? FindEmitter(Win32Type type)
    {
        foreach (var e in _emitters)
        {
            if (e.CanEmit(type))
                return e;
        }
        return null;
    }
}