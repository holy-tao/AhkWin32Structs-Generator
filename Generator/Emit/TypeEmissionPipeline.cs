namespace AhkWin32.Generator.Emit;

using System.Collections.Concurrent;
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
    private readonly ParallelOptions _parallelOptions;

    public TypeEmissionPipeline(IEnumerable<ITypeEmitter> emitters, ILogger<TypeEmissionPipeline> logger, int maxParallelism = 0)
    {
        _emitters = [.. emitters];
        _logger = logger;
        _parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism > 0 ? maxParallelism : Environment.ProcessorCount
        };
    }

    /// <summary>
    /// Emit all types from the registry that match a registered emitter,
    /// optionally filtered by namespace prefixes.
    /// </summary>
    public (int Emitted, int Skipped, int Errors) EmitAll(
        TypeRegistry registry, string outputDir, string[]? namespaceFilter = null)
    {
        _logger.LogInformation("Beginning type emission...");

        int skipped = 0, errors = 0;
        Stopwatch watch = Stopwatch.StartNew();

        Win32Type[] filteredTypes = [.. registry.GetAll().Where(type => ShouldEmit(type, namespaceFilter))];

        // Pre-create all namespace directories so emitters don't call Directory.CreateDirectory per-file
        foreach (string ns in filteredTypes.Select(t => t.Namespace).Distinct())
        {
            string dirPath = Path.Join(outputDir, Path.Join(ns.Split('.')));
            Directory.CreateDirectory(dirPath);
        }

        // Phase 1: Emit to memory (CPU-bound)
        ConcurrentBag<EmitResult> results = [];

        Parallel.ForEach(filteredTypes, (type) =>
        {
            ITypeEmitter? emitter = FindEmitter(type);
            if (emitter is null)
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            try
            {
                _logger.LogTrace("Emitting {Namespace}.{Name}", type.Namespace, type.Name);
                results.Add(emitter.Emit(type, outputDir));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);
                _logger.LogError(ex, "Failed to emit {TypeName}", type.FQN);
            }
        });

        watch.Stop();
        _logger.LogInformation("Emitted {Count} types to memory in {Elapsed:F1}s, writing files...", 
            results.Count, watch.Elapsed.TotalSeconds);

        // Phase 2: Write files (I/O-bound, async to avoid blocking thread pool threads)
        watch.Restart();
        int written = 0;

        Parallel.ForEachAsync(results, _parallelOptions, async (result, cancellationToken) =>
        {
            await File.WriteAllTextAsync(result.FilePath, result.Content, cancellationToken);

            int count = Interlocked.Increment(ref written);
            if (count % 5000 == 0)
                _logger.LogInformation("  Wrote {Count} files...", count);
        }).GetAwaiter().GetResult();

        watch.Stop();
        _logger.LogInformation("Emission complete: {Emitted} emitted, {Skipped} skipped, {Errors} errors in {Elapsed:F1}s",
            results.Count, skipped, errors, watch.Elapsed.TotalSeconds);

        return (results.Count, skipped, errors);
    }

    /// <summary>
    /// Checks to see if type passes the namespace filter
    /// </summary>
    private static bool ShouldEmit(Win32Type type, string[]? namespaceFilter)
    {
        if (namespaceFilter != null && namespaceFilter.Length > 0)
        {
            return namespaceFilter.Any(prefix =>
                type.Namespace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return true;
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
