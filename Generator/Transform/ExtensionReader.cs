namespace AhkWin32.Generator.Transform;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

/// <summary>
/// Loads YAML extension files and converts them to ExtensionCode records
/// keyed by the FQN they apply to.
/// </summary>
public sealed class ExtensionReader
{
    private const string SkipMarker = "skip";

    private readonly ILogger<ExtensionReader> _logger;
    private readonly int _maxParallelism;

    public ExtensionReader(ILogger<ExtensionReader> logger, int maxParallelism = 0)
    {
        _logger = logger;
        _maxParallelism = maxParallelism > 0 ? maxParallelism : Environment.ProcessorCount;
    }

    /// <summary>
    /// Load all extension YAML files from the given directory.
    /// Returns a dictionary mapping FQN -> list of ExtensionCode blocks.
    /// </summary>
    public FrozenDictionary<string, List<ExtensionCode>> LoadExtensions(string extensionDirectoryPath)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithEnforceRequiredMembers()
            .WithEnforceNullability()
            .Build();
        ConcurrentDictionary<string, List<ExtensionCode>> extensions = [];

        var watch = Stopwatch.StartNew();

        string[] files =
        [
            .. Directory
                .GetFiles(extensionDirectoryPath)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".yml" or ".yaml")
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase),
        ];

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism },
            path =>
            {
                ExtensionDto dto;
                try
                {
                    dto = deserializer.Deserialize<ExtensionDto>(File.ReadAllText(path));
                }
                catch (Exception ex) when (ex is YamlException or IOException)
                {
                    _logger.LogError(ex, "Failed to deserialize extension \"{file}\"", Path.GetFileName(path));
                    return;
                }

                ExtensionCode extensionCode;
                try
                {
                    extensionCode = BuildExtensionCode(dto, Path.GetFileName(path));
                }
                catch (FormatException ex)
                {
                    _logger.LogError(ex, "Invalid extension \"{file}\": {message}", Path.GetFileName(path), ex.Message);
                    return;
                }

                foreach (string fqn in dto.AddTo)
                {
                    if (!extensions.TryGetValue(fqn, out List<ExtensionCode>? list))
                    {
                        list = [];
                        extensions[fqn] = list;
                    }

                    list.Add(extensionCode);
                }

                _logger.LogDebug(
                    "Loaded extension from {FileName}: {FqnCount} target(s), {ImpCount} import(s), versions=[{Versions}]",
                    Path.GetFileName(path),
                    dto.AddTo.Count,
                    dto.Imports?.Count ?? 0,
                    string.Join(",", extensionCode.CodeByVersion.Keys)
                );
            }
        );

        watch.Stop();
        _logger.LogInformation(
            "Loaded extensions for {fqns} types from {files} files in {time:F1}s",
            extensions.Count,
            files.Length,
            watch.Elapsed.TotalSeconds
        );

        return extensions.ToFrozenDictionary();
    }

    private static ExtensionCode BuildExtensionCode(ExtensionDto dto, string fileName)
    {
        Dictionary<AhkVersion, string> codeByVersion = [];
        AddIfNotSkipped(codeByVersion, AhkVersion.v20, dto.Code.V20, "v20", fileName);
        AddIfNotSkipped(codeByVersion, AhkVersion.v21, dto.Code.V21, "v21", fileName);

        if (codeByVersion.Count == 0)
            throw new FormatException($"extension \"{fileName}\" must target at least one of v20 or v21");

        Dictionary<string, IReadOnlyList<string>> imports = [];
        if (dto.Imports != null)
        {
            foreach (var (fqn, names) in dto.Imports)
                imports[fqn] = (IReadOnlyList<string>?)names ?? [];
        }

        return new ExtensionCode(codeByVersion, imports);
    }

    private static void AddIfNotSkipped(
        Dictionary<AhkVersion, string> map,
        AhkVersion version,
        string value,
        string key,
        string fileName
    )
    {
        if (value == SkipMarker)
            return;
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"extension \"{fileName}\" has empty code block for {key}");
        map[version] = value;
    }

    /// <summary>DTO for YamlDotNet deserialization of extension YAML files.</summary>
    private class ExtensionDto
    {
        [YamlMember(Alias = "add-to", ApplyNamingConventions = false)]
        public required List<string> AddTo { get; set; }

        /// <summary>
        /// FQN -> function-name list. Null or empty list means "whole-file include".
        /// </summary>
        [YamlMember(Alias = "imports", ApplyNamingConventions = false)]
        public Dictionary<string, List<string>?>? Imports { get; set; }

        [YamlMember(Alias = "code", ApplyNamingConventions = false)]
        public required CodeDto Code { get; set; }
    }

    private class CodeDto
    {
        [YamlMember(Alias = "v20", ApplyNamingConventions = false)]
        public required string V20 { get; set; }

        [YamlMember(Alias = "v21", ApplyNamingConventions = false)]
        public required string V21 { get; set; }
    }
}
