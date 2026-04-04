namespace AhkWin32.Generator.Transform;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using AhkWin32.Generator.Model;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

/// <summary>
/// Loads YAML override files and converts them to an <see cref="OverrideSet"/>
/// for use by <see cref="OverrideApplier"/>.
/// </summary>
public sealed class OverrideReader
{
    private readonly ILogger<OverrideReader> _logger;

    public OverrideReader(ILogger<OverrideReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Load all override YAML files from the given directory.
    /// Returns an <see cref="OverrideSet"/> indexed by type FQN.
    /// </summary>
    public OverrideSet LoadOverrides(string overrideDirectoryPath)
    {
        if (!Directory.Exists(overrideDirectoryPath))
        {
            _logger.LogDebug("Override directory does not exist: {Path}", overrideDirectoryPath);
            return OverrideSet.Empty;
        }

        IDeserializer deserializer = new DeserializerBuilder().Build();
        ConcurrentDictionary<string, TypeOverride> overrides = [];

        var watch = Stopwatch.StartNew();

        string[] files = [.. Directory.GetFiles(overrideDirectoryPath)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".yml" or ".yaml")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)];

        Parallel.ForEach(files, path =>
        {
            List<OverrideEntryDto> entries;
            try
            {
                entries = deserializer.Deserialize<List<OverrideEntryDto>>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is YamlException or IOException)
            {
                _logger.LogError(ex, "Failed to deserialize override file \"{File}\"", Path.GetFileName(path));
                return;
            }

            if (entries == null)
            {
                _logger.LogDebug("Override file {File} is empty", Path.GetFileName(path));
                return;
            }

            foreach (OverrideEntryDto entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Type))
                {
                    _logger.LogWarning("Override entry in {File} missing 'type' field — skipping", Path.GetFileName(path));
                    continue;
                }

                TypeOverride parsed = ParseEntry(entry, Path.GetFileName(path));

                if (!overrides.TryAdd(parsed.FQN, parsed))
                {
                    _logger.LogWarning("Duplicate override for type {FQN} in {File} — last wins",
                        parsed.FQN, Path.GetFileName(path));
                    overrides[parsed.FQN] = parsed;
                }
            }

            _logger.LogDebug("Loaded {Count} override(s) from {File}", entries.Count, Path.GetFileName(path));
        });

        watch.Stop();
        _logger.LogInformation("Loaded {Count} override(s) from {Files} file(s) in {Time:F1}s",
            overrides.Count, files.Length, watch.Elapsed.TotalSeconds);

        return new OverrideSet(overrides.ToFrozenDictionary());
    }

    private TypeOverride ParseEntry(OverrideEntryDto entry, string fileName)
    {
        // Parse field overrides
        Dictionary<string, FieldOverride>? fields = null;
        if (entry.Fields is { Count: > 0 })
        {
            fields = [];
            foreach (var (fieldName, fieldDto) in entry.Fields)
            {
                MemberFlags addFlags = ParseMemberFlags(fieldDto.AddAttributes, fileName, entry.Type!, fieldName);
                fields[fieldName] = new FieldOverride(addFlags);
            }
        }

        // Parse method overrides
        Dictionary<string, MethodOverride>? methods = null;
        if (entry.Methods is { Count: > 0 })
        {
            methods = [];
            foreach (var (methodName, methodDto) in entry.Methods)
            {
                Dictionary<string, ParameterOverride>? parameters = null;
                if (methodDto.Parameters is { Count: > 0 })
                {
                    parameters = [];
                    foreach (var (paramName, paramDto) in methodDto.Parameters)
                    {
                        ParameterFlags addFlags = ParseParameterFlags(
                            paramDto.AddAttributes, fileName, entry.Type!, methodName, paramName);
                        parameters[paramName] = new ParameterOverride(addFlags);
                    }
                }

                methods[methodName] = new MethodOverride(
                    methodDto.Skip ?? false,
                    parameters);
            }
        }

        // Parse add-methods
        List<AddMethodRef>? addMethods = null;
        if (entry.AddMethods is { Count: > 0 })
        {
            addMethods = [];
            foreach (AddMethodDto addDto in entry.AddMethods)
            {
                if (string.IsNullOrWhiteSpace(addDto.From) || string.IsNullOrWhiteSpace(addDto.Name))
                {
                    _logger.LogWarning("add-methods entry in {File} for {Type} missing 'from' or 'name' — skipping",
                        fileName, entry.Type);
                    continue;
                }
                addMethods.Add(new AddMethodRef(addDto.From, addDto.Name));
            }
        }

        return new TypeOverride(
            FQN: entry.Type!,
            Skip: entry.Skip ?? false,
            StructSizeField: entry.StructSizeField,
            Fields: fields,
            Methods: methods,
            AddMethods: addMethods);
    }

    private MemberFlags ParseMemberFlags(List<string>? attributeNames, string fileName, string typeFqn, string memberName)
    {
        if (attributeNames is null or { Count: 0 })
            return MemberFlags.None;

        MemberFlags result = MemberFlags.None;
        foreach (string name in attributeNames)
        {
            if (Enum.TryParse<MemberFlags>(name, ignoreCase: true, out var flag))
                result |= flag;
            else
                _logger.LogWarning("Unknown MemberFlags attribute '{Name}' for {Type}.{Member} in {File}",
                    name, typeFqn, memberName, fileName);
        }
        return result;
    }

    private ParameterFlags ParseParameterFlags(List<string>? attributeNames, string fileName,
        string typeFqn, string methodName, string paramName)
    {
        if (attributeNames is null or { Count: 0 })
            return ParameterFlags.None;

        ParameterFlags result = ParameterFlags.None;
        foreach (string name in attributeNames)
        {
            if (Enum.TryParse<ParameterFlags>(name, ignoreCase: true, out var flag))
                result |= flag;
            else
                _logger.LogWarning("Unknown ParameterFlags attribute '{Name}' for {Type}.{Method}.{Param} in {File}",
                    name, typeFqn, methodName, paramName, fileName);
        }
        return result;
    }

    // --- DTO types for YamlDotNet deserialization ---

    private class OverrideEntryDto
    {
        [YamlMember(Alias = "type", ApplyNamingConventions = false)]
        public string? Type { get; set; }

        [YamlMember(Alias = "skip", ApplyNamingConventions = false)]
        public bool? Skip { get; set; }

        [YamlMember(Alias = "struct-size-field", ApplyNamingConventions = false)]
        public string? StructSizeField { get; set; }

        [YamlMember(Alias = "fields", ApplyNamingConventions = false)]
        public Dictionary<string, FieldOverrideDto>? Fields { get; set; }

        [YamlMember(Alias = "methods", ApplyNamingConventions = false)]
        public Dictionary<string, MethodOverrideDto>? Methods { get; set; }

        [YamlMember(Alias = "add-methods", ApplyNamingConventions = false)]
        public List<AddMethodDto>? AddMethods { get; set; }
    }

    private class FieldOverrideDto
    {
        [YamlMember(Alias = "add-attributes", ApplyNamingConventions = false)]
        public List<string>? AddAttributes { get; set; }
    }

    private class MethodOverrideDto
    {
        [YamlMember(Alias = "parameters", ApplyNamingConventions = false)]
        public Dictionary<string, ParameterOverrideDto>? Parameters { get; set; }

        [YamlMember(Alias = "skip", ApplyNamingConventions = false)]
        public bool? Skip { get; set; }
    }

    private class ParameterOverrideDto
    {
        [YamlMember(Alias = "add-attributes", ApplyNamingConventions = false)]
        public List<string>? AddAttributes { get; set; }
    }

    private class AddMethodDto
    {
        [YamlMember(Alias = "from", ApplyNamingConventions = false)]
        public string? From { get; set; }

        [YamlMember(Alias = "name", ApplyNamingConventions = false)]
        public string? Name { get; set; }
    }
}
