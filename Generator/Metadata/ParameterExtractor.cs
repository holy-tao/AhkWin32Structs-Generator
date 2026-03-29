namespace AhkWin32.Generator.Metadata;

using System.Reflection;
using System.Reflection.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// Extracts method parameters from metadata into ParameterMember instances.
/// Ports logic from ParameterDecoder + AhkParameter.
/// </summary>
public sealed class ParameterExtractor
{
    private readonly MetadataLoader _loader;
    private readonly ILogger<ParameterExtractor> _logger;

    /// <summary>
    /// Reserved parameter names in AutoHotkey. Case-insensitive lookup.
    /// Includes both language keywords and type names from loaded assemblies.
    /// </summary>
    private readonly HashSet<string> _reservedNames;

    private static readonly string[] s_builtinReservedNames =
    [
        "in", "as", "is", "contains", "not", "and", "or", "this", "return",
        "throw", "loop", "do", "while", "float", "number", "integer", "object",
        "class", "buffer", "string", "file", "enumerator"
    ];

    public ParameterExtractor(MetadataLoader loader, ILogger<ParameterExtractor> logger)
    {
        _loader = loader;
        _logger = logger;
        _reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in s_builtinReservedNames)
            _reservedNames.Add(name);

        // Pre-populate type names from all primary assemblies
        foreach (var (_, _, reader) in _loader.GetPrimaryAssemblies())
        {
            foreach (TypeDefinitionHandle hTd in reader.TypeDefinitions)
            {
                TypeDefinition td = reader.GetTypeDefinition(hTd);
                string tdName = reader.GetString(td.Name);
                _reservedNames.Add(tdName);
            }
        }

        _logger.LogDebug("ParameterExtractor initialized with {Count} reserved names", _reservedNames.Count);
    }

    /// <summary>
    /// Extract all parameters from a MethodDefinition into ParameterMember list.
    /// Parameter[0] is always the return type. Parameters[1..n] are actual parameters.
    /// </summary>
    public List<ParameterMember> ExtractParameters(
        MetadataReader reader, MethodDefinition methodDef,
        ApiDetails? apiDetails, TypeDefinition? resolutionContext = null)
    {
        // Decode method signature to get return type + parameter types
        var (returnType, parameterTypes) = SignatureDecoder.DecodeMethodSignature(
            reader, methodDef, _loader, _logger, resolutionContext);

        // Build lookup of SequenceNumber → Parameter metadata
        Dictionary<int, Parameter> paramInfos = [];
        foreach (ParameterHandle paramHandle in methodDef.GetParameters())
        {
            Parameter param = reader.GetParameter(paramHandle);
            paramInfos[param.SequenceNumber] = param;
        }

        List<ParameterMember> result = [];

        // Parameter[0]: return type
        if (paramInfos.TryGetValue(0, out Parameter retParam))
        {
            result.Add(BuildParameterMember(reader, retParam, returnType, 0, apiDetails));
        }
        else
        {
            // No explicit return parameter metadata — create a minimal one
            result.Add(new ParameterMember
            {
                Name = "result",
                Type = returnType,
                SequenceNumber = 0,
                Direction = ParameterDirection.None,
                Attributes = ParameterFlags.None,
                SizedBufferBytesParamIndex = -1
            });
        }

        // Parameters[1..n]: actual method parameters
        for (int i = 0; i < parameterTypes.Length; i++)
        {
            int seq = i + 1;
            paramInfos.TryGetValue(seq, out Parameter param);

            // Check for [MemorySize] — override type to ptr
            ResolvedType paramType = parameterTypes[i];
            if (!param.Name.IsNil)
            {
                bool hasMemorySize = AttributeReader.GetAllAttributeNames(reader, param.GetCustomAttributes())
                    .Any(n => n == "MemorySizeAttribute");
                if (hasMemorySize)
                    paramType = new PrimitiveType("ptr");
            }

            result.Add(BuildParameterMember(reader, param, paramType, seq, apiDetails));
        }

        return result;
    }

    private ParameterMember BuildParameterMember(
        MetadataReader reader, Parameter param, ResolvedType type,
        int sequenceNumber, ApiDetails? apiDetails)
    {
        // Get parameter name with deconfliction
        string name = GetParameterName(reader, param, sequenceNumber);

        // Decode direction from ParameterAttributes
        ParameterDirection direction = ParameterDirection.None;
        if (param.Attributes.HasFlag(ParameterAttributes.In))
            direction |= ParameterDirection.In;
        if (param.Attributes.HasFlag(ParameterAttributes.Out))
            direction |= ParameterDirection.Out;
        if (param.Attributes.HasFlag(ParameterAttributes.Optional))
            direction |= ParameterDirection.Optional;

        // Decode custom attributes in single pass
        ParameterAttrs attrs;
        if (!param.Name.IsNil)
        {
            attrs = AttributeReader.DecodeParameterAttributes(reader, param);
        }
        else
        {
            attrs = new ParameterAttrs(ParameterFlags.None, -1, null, null, null);
        }

        // Resolve FreeFuncRef for RAIIFree and FreeWith
        FreeFuncRef? raiiFree = ResolveFreeFuncRef(reader, attrs.RAIIFreeFuncName);
        FreeFuncRef? freeWith = ResolveFreeFuncRef(reader, attrs.FreeWithFuncName);

        // Get documentation (use original name without deconfliction prefix)
        string? description = null;
        if (apiDetails != null && sequenceNumber > 0)
        {
            string originalName = !param.Name.IsNil ? reader.GetString(param.Name) : name;
            apiDetails.Parameters.TryGetValue(originalName, out description);
        }

        return new ParameterMember
        {
            Name = name,
            Type = type,
            SequenceNumber = sequenceNumber,
            Direction = direction,
            Attributes = attrs.Flags,
            IgnoreIfReturnValues = attrs.IgnoreIfReturnValues,
            RAIIFree = raiiFree,
            FreeWith = freeWith,
            SizedBufferBytesParamIndex = attrs.SizedBufferBytesParamIndex,
            Description = description
        };
    }

    private string GetParameterName(MetadataReader reader, Parameter param, int sequenceNumber)
    {
        if (param.Name.IsNil)
            return "result";

        string paramName = reader.GetString(param.Name);
        if (string.IsNullOrWhiteSpace(paramName))
            return "result";

        // Reserved word deconfliction
        if (_reservedNames.Contains(paramName))
            paramName = "_" + paramName;

        return paramName;
    }

    /// <summary>
    /// Resolve a free function name to a FreeFuncRef, validating that the function
    /// exists and has exactly 2 parameters (matching legacy behavior).
    /// Searches all Apis types in the reader to find the function and its namespace.
    /// </summary>
    private FreeFuncRef? ResolveFreeFuncRef(MetadataReader reader, string? funcName)
    {
        if (funcName == null)
            return null;

        // Search all Apis types in this reader for the method
        foreach (TypeDefinitionHandle hTd in reader.TypeDefinitions)
        {
            TypeDefinition td = reader.GetTypeDefinition(hTd);
            if (!reader.StringComparer.Equals(td.Name, "Apis"))
                continue;

            foreach (MethodDefinitionHandle hMethod in td.GetMethods())
            {
                MethodDefinition md = reader.GetMethodDefinition(hMethod);
                if (!reader.StringComparer.Equals(md.Name, funcName))
                    continue;

                // Validate parameter count (legacy: must have exactly 2 = return + 1 param)
                int paramCount = md.GetParameters().Count;
                if (paramCount != 2)
                {
                    _logger.LogDebug(
                        "FreeFuncRef {FuncName} has {ParamCount} parameters, expected 2 — skipping",
                        funcName, paramCount);
                    return null;
                }

                string ns = reader.GetString(td.Namespace);
                return new FreeFuncRef(funcName, ns, $"{ns}.Apis");
            }
        }

        _logger.LogWarning("FreeFuncRef {FuncName} not found in any Apis type", funcName);
        return null;
    }
}
