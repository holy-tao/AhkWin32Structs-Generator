namespace AhkWin32.Generator.Transform;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
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
    private readonly ILogger<ExtensionReader> _logger;
    private readonly int _maxParallelism;

    public ExtensionReader(ILogger<ExtensionReader> logger, int maxParallelism = 0)
    {
        _logger = logger;
        _maxParallelism = maxParallelism > 0 ? maxParallelism : Environment.ProcessorCount;
    }

    /// <summary>
    /// Load all extension YAML files from the given directory.
    /// Returns a dictionary mapping FQN → list of ExtensionCode blocks.
    /// </summary>
    public FrozenDictionary<string, List<ExtensionCode>> LoadExtensions(string extensionDirectoryPath)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithEnforceRequiredMembers()
            .WithEnforceNullability()
            .Build();
        ConcurrentDictionary<string, List<ExtensionCode>> extensions = [];

        var watch = Stopwatch.StartNew();

        // Sort files for deterministic output ordering
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

                var extensionCode = new ExtensionCode(dto.Code, dto.Requires ?? []);

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
                    "Loaded extension from {FileName}: {FqnCount} target(s), {ReqCount} requirement(s)",
                    Path.GetFileName(path),
                    dto.AddTo.Count,
                    dto.Requires?.Count ?? 0
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

    /// <summary>DTO for YamlDotNet deserialization of extension YAML files.</summary>
    private class ExtensionDto
    {
        [YamlMember(Alias = "add-to", ApplyNamingConventions = false)]
        public required List<string> AddTo { get; set; }

        [YamlMember(Alias = "requires", ApplyNamingConventions = false)]
        public List<string>? Requires { get; set; }

        [YamlMember(Alias = "code", ApplyNamingConventions = false)]
        public required string Code { get; set; }
    }
}
