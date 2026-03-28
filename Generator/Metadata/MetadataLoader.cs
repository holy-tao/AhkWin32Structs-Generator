namespace AhkWin32.Generator.Metadata;

using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Owns all PEReader/MetadataReader lifetime. Loads primary .winmd assemblies
/// and resolves external assembly references on demand.
/// </summary>
public sealed class MetadataLoader : IDisposable
{
    private readonly Dictionary<string, (PEReader PeReader, MetadataReader Reader)> _primaryAssemblies = [];
    private readonly Dictionary<string, (PEReader PeReader, MetadataReader Reader)> _externalAssemblies = [];
    private readonly string _metadataDir;
    private readonly ILogger<MetadataLoader> _logger;

    public MetadataLoader(string metadataDir, ILogger<MetadataLoader> logger)
    {
        _metadataDir = metadataDir;
        _logger = logger;
    }

    /// <summary>
    /// Load all .winmd files from the metadata directory, optionally filtered by assembly name.
    /// </summary>
    public void LoadPrimaryAssemblies(string[]? assemblyFilter = null)
    {
        _logger.LogInformation("Loading primary assemblies from {MetadataDir}...", _metadataDir);
        Stopwatch watch = Stopwatch.StartNew();

        var winmdPaths = Directory.EnumerateFiles(_metadataDir)
            .Where(path => Path.GetExtension(path).Equals(".winmd", StringComparison.OrdinalIgnoreCase));

        foreach (string path in winmdPaths)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);

            if (assemblyFilter is { Length: > 0 } &&
                !assemblyFilter.Any(f => fileName.Equals(f, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Assembly {AssemblyName} skipped by filter", fileName);
                continue;
            }

            _logger.LogDebug("Found .winmd: {Path}", path);

            PEReader peReader = new(File.OpenRead(path));
            MetadataReader reader = peReader.GetMetadataReader();
            AssemblyName asmName = reader.GetAssemblyDefinition().GetAssemblyName();
            string name = asmName.Name ?? fileName;

            _primaryAssemblies[name] = (peReader, reader);

            int typeCount = reader.TypeDefinitions.Count;
            _logger.LogInformation("Loaded {AssemblyName} v{Version} ({TypeCount} types)",
                name, asmName.Version, typeCount);
        }

        watch.Stop();
        _logger.LogInformation("Loaded {Count} primary assemblies in {Elapsed:F1}s",
            _primaryAssemblies.Count, watch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Get the MetadataReader for a primary assembly.
    /// </summary>
    public MetadataReader GetPrimaryReader(string assemblyName)
    {
        return _primaryAssemblies[assemblyName].Reader;
    }

    /// <summary>
    /// Get all primary assemblies as (AssemblyName, Version, MetadataReader) tuples.
    /// </summary>
    public IEnumerable<(string AssemblyName, string Version, MetadataReader Reader)> GetPrimaryAssemblies()
    {
        foreach (var (name, (_, reader)) in _primaryAssemblies)
        {
            AssemblyName asmName = reader.GetAssemblyDefinition().GetAssemblyName();
            yield return (name, asmName.Version?.ToString() ?? "unknown", reader);
        }
    }

    /// <summary>
    /// Resolve a TypeReference to its TypeDefinition, potentially across assemblies.
    /// Returns the matched (MetadataReader, TypeDefinitionHandle) pair.
    /// </summary>
    public (MetadataReader Reader, TypeDefinitionHandle Handle) ResolveTypeReference(
        MetadataReader sourceReader, TypeReferenceHandle trHandle)
    {
        TypeReference tr = sourceReader.GetTypeReference(trHandle);
        string name = sourceReader.GetString(tr.Name);
        string ns = sourceReader.GetString(tr.Namespace);

        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.ModuleDefinition:
                // Type is in the same module
                return FindTypeDefinition(sourceReader, ns, name);

            case HandleKind.TypeReference:
                // Nested type — resolve parent first, then search its nested types
                var parentHandle = (TypeReferenceHandle)tr.ResolutionScope;
                var (parentReader, parentTdHandle) = ResolveTypeReference(sourceReader, parentHandle);
                TypeDefinition parentTd = parentReader.GetTypeDefinition(parentTdHandle);

                foreach (TypeDefinitionHandle nestedHandle in parentTd.GetNestedTypes())
                {
                    TypeDefinition nestedTd = parentReader.GetTypeDefinition(nestedHandle);
                    if (parentReader.StringComparer.Equals(nestedTd.Name, name))
                    {
                        return (parentReader, nestedHandle);
                    }
                }

                string parentNs = parentReader.GetString(parentTd.Namespace);
                string parentName = parentReader.GetString(parentTd.Name);
                throw new TypeLoadException(
                    $"Could not resolve nested type '{ns}.{name}' under '{parentNs}.{parentName}'");

            case HandleKind.AssemblyReference:
                // Type is in a different assembly, load it if necessary and search there
                AssemblyReference asmRef = sourceReader.GetAssemblyReference(
                    (AssemblyReferenceHandle)tr.ResolutionScope);
                string asmName = sourceReader.GetString(asmRef.Name);
                MetadataReader extReader = ResolveExternalAssembly(asmName);

                return FindTypeDefinition(extReader, ns, name);

            default:
                throw new NotSupportedException(
                    $"Cannot resolve '{ns}.{name}' — unsupported resolution scope kind '{tr.ResolutionScope.Kind}'");
        }
    }

    /// <summary>
    /// Resolve an external assembly reference by name. Lazy-loads and caches the result.
    /// </summary>
    public MetadataReader ResolveExternalAssembly(string assemblyName)
    {
        if (_externalAssemblies.TryGetValue(assemblyName, out var cached))
            return cached.Reader;

        // Also check primary assemblies
        if (_primaryAssemblies.TryGetValue(assemblyName, out cached))
            return cached.Reader;

        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string sdkRoot = Environment.GetEnvironmentVariable("WindowsSdkDir") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Windows Kits", "10");

        // We search these paths in order. The first assembly that we can load succesfully is returned
        List<string> probePaths =
        [
            Path.Combine(_metadataDir, $"{assemblyName}.winmd"),
            Path.Combine(_metadataDir, $"{assemblyName}.dll"),
            Path.Combine(_metadataDir, assemblyName),
            Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll"),
            Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.winmd"),
            Path.Combine(runtimeDir, $"{assemblyName}.dll"),
            Path.Combine(runtimeDir, $"{assemblyName}.winmd"),
        ];

        // Probe typical Windows SDK metadata locations
        if (Directory.Exists(sdkRoot))
        {
            string refsPath = Path.Combine(sdkRoot, "References");
            if (Directory.Exists(refsPath))
            {
                foreach (string versionDir in Directory.GetDirectories(refsPath))
                {
                    probePaths.Add(Path.Combine(versionDir, $"{assemblyName}.winmd"));

                    string asmDir = Path.Combine(versionDir, assemblyName);
                    if (!Directory.Exists(asmDir))
                        continue;

                    probePaths.Add(Path.Combine(asmDir, $"{assemblyName}.winmd"));
                    foreach (string subDir in Directory.GetDirectories(asmDir, "*", SearchOption.AllDirectories))
                    {
                        probePaths.Add(Path.Combine(subDir, $"{assemblyName}.winmd"));
                    }
                }
            }

            string unionMeta = Path.Combine(sdkRoot, "UnionMetadata");
            if (Directory.Exists(unionMeta))
            {
                probePaths.Add(Path.Combine(unionMeta, $"{assemblyName}.winmd"));
            }
        }
        else
        {
            _logger.LogWarning("Could not find Windows SDK metadata at {SdkRoot}", sdkRoot);
        }

        foreach (string path in probePaths)
        {
            _logger.LogTrace("Probing for {AssemblyName}: {ProbePath}", assemblyName, path);

            if (!File.Exists(path))
                continue;

            PEReader peReader = new(File.OpenRead(path));
            MetadataReader reader = peReader.GetMetadataReader();
            _externalAssemblies[assemblyName] = (peReader, reader);

            _logger.LogDebug("Loaded external assembly {AssemblyName} from {Path}", assemblyName, path);
            return reader;
        }

        throw new DllNotFoundException($"Failed to load external assembly '{assemblyName}'");
    }

    /// <summary>
    /// Find a TypeDefinition by namespace and name within an assembly,
    /// following type forwarders (ExportedTypes) if necessary.
    /// </summary>
    private (MetadataReader Reader, TypeDefinitionHandle Handle) FindTypeDefinition(
        MetadataReader reader, string ns, string name)
    {
        // Try normal type definitions first
        foreach (TypeDefinitionHandle tdHandle in reader.TypeDefinitions)
        {
            TypeDefinition td = reader.GetTypeDefinition(tdHandle);
            if (reader.StringComparer.Equals(td.Name, name) &&
                reader.StringComparer.Equals(td.Namespace, ns))
            {
                return (reader, tdHandle);
            }
        }

        // Check type forwarders (ExportedTypes)
        foreach (ExportedTypeHandle exportedHandle in reader.ExportedTypes)
        {
            ExportedType exported = reader.GetExportedType(exportedHandle);
            if (reader.StringComparer.Equals(exported.Name, name) &&
                reader.StringComparer.Equals(exported.Namespace, ns))
            {
                return FollowTypeForwarder(reader, exported, ns, name);
            }
        }

        string? asmName = reader.GetAssemblyDefinition().GetAssemblyName().Name;
        throw new TypeLoadException(
            $"Could not resolve '{ns}.{name}' in assembly '{asmName}'");
    }

    /// <summary>
    /// Follow a type forwarder chain to find the actual TypeDefinition.
    /// </summary>
    private (MetadataReader Reader, TypeDefinitionHandle Handle) FollowTypeForwarder(
        MetadataReader reader, ExportedType exported, string ns, string name)
    {
        switch (exported.Implementation.Kind)
        {
            case HandleKind.AssemblyReference:
                AssemblyReference targetAsmRef = reader.GetAssemblyReference(
                    (AssemblyReferenceHandle)exported.Implementation);
                string targetAsmName = reader.GetString(targetAsmRef.Name);

                _logger.LogTrace(
                    "Following type forwarder for {Namespace}.{Name} -> {TargetAssembly}",
                    ns, name, targetAsmName);

                MetadataReader targetReader = ResolveExternalAssembly(targetAsmName);
                return FindTypeDefinition(targetReader, ns, name);

            case HandleKind.ExportedType:
                ExportedType parentExported = reader.GetExportedType(
                    (ExportedTypeHandle)exported.Implementation);
                return FollowTypeForwarder(reader, parentExported, ns, name);

            default:
                throw new InvalidOperationException(
                    $"Invalid type forwarder target for '{ns}.{name}': {exported.Implementation.Kind}");
        }
    }

    public void Dispose()
    {
        foreach (var (_, (peReader, _)) in _primaryAssemblies)
            peReader.Dispose();

        foreach (var (_, (peReader, _)) in _externalAssemblies)
            peReader.Dispose();

        _primaryAssemblies.Clear();
        _externalAssemblies.Clear();
    }
}
