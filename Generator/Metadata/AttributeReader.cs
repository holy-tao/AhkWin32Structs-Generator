namespace AhkWin32.Generator.Metadata;

using System.Reflection.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using Microsoft.Extensions.Logging;

/// <summary>
/// A decoded custom attribute entry: namespace, name, and decoded value.
/// </summary>
public readonly record struct CAInfo(string Namespace, string Name, CustomAttributeValue<string> Attr);

/// <summary>
/// Decoded attributes for a TypeDefinition. Computed once and reused.
/// </summary>
public sealed record TypeAttrs(
    IReadOnlyList<CAInfo> All,
    Architecture? SupportedArchitecture,
    bool IsHandle,
    bool IsNativeTypedef,
    bool IsFlags,
    bool IsDeprecated,
    string? DeprecationMessage,
    string? StructSizeFieldName,
    string? SupportedOSPlatform,
    IReadOnlyList<string> AlsoUsableFor,
    MemberFlags Flags
);

/// <summary>
/// Decoded attributes for a FieldDefinition. Computed once and reused.
/// </summary>
public sealed record FieldAttrs(
    IReadOnlyList<CAInfo> All,
    MemberFlags Flags,
    bool IsDeprecated,
    string? DeprecationMessage,
    IReadOnlyList<BitfieldMember>? Bitfields
);

/// <summary>
/// Decoded attributes for a Parameter. Computed once and reused.
/// </summary>
public sealed record ParameterAttrs(
    ParameterFlags Flags,
    int SizedBufferBytesParamIndex,
    IReadOnlyList<string>? IgnoreIfReturnValues,
    string? RAIIFreeFuncName,
    string? FreeWithFuncName
);

/// <param name="PreserveSig">Whether [PreserveSig] attribute is present.</param>
/// <param name="PreserveSigValue">The boolean value of [PreserveSig], or true if present with no args.
/// Null if attribute is not present.</param>
public sealed record MethodAttrs(
    bool PreserveSig,
    bool? PreserveSigValue,
    bool CanReturnErrorsAsSuccess,
    bool CanReturnMultipleSuccessValues,
    string? DeprecationMessage,
    string? SupportedOSPlatform,
    Architecture Architecture
);

/// <summary>
/// Delegate-specific attributes decoded from [UnmanagedFunctionPointer]. Note that it includes a CharSet
/// but the delegates that have charsets don't use it, they just use the [Unicode] / [Ansi] attributes.
/// </summary>
/// <param name="CallingConvention">Calling convention as declared on the delegate type.</param>
public sealed record DelegateAttrs(CallingConvention CallingConvention);

/// <summary>
/// Cached attribute decoding utilities. Decodes attributes in a single pass
/// and returns result records for reuse.
/// </summary>
public static class AttributeReader
{
    private static readonly CaTypeProvider s_caProvider = new();

    /// <summary>
    /// Decode all relevant attributes for a TypeDefinition in a single pass.
    /// </summary>
    /// <param name="reader">MetadataReader for the assembly</param>
    /// <param name="typeDef">The type definition to decode</param>
    /// <param name="fieldCount">Number of fields (used for handle detection)</param>
    /// <param name="logger">Logger for trace-level output</param>
    public static TypeAttrs DecodeTypeAttributes(
        MetadataReader reader,
        TypeDefinition typeDef,
        int fieldCount,
        ILogger? logger = null
    )
    {
        MemberFlags flags = MemberFlags.None;
        Architecture? supportedArch = null;
        bool isFlags = false;
        bool isDeprecated = false;
        string? deprecationMessage = null;
        string? structSizeFieldName = null;
        string? supportedOSPlatform = null;
        bool hasHandleAttr = false;
        bool hasNativeTypedefAttr = false;
        List<string> alsoUsableFor = [];

        List<CAInfo> allAttrs = [];

        foreach (CustomAttributeHandle attrHandle in typeDef.GetCustomAttributes())
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (string attrNamespace, string attrName) = GetAttributeTypeName(reader, attr);

            // Decode the value for storage
            CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);
            allAttrs.Add(new CAInfo(attrNamespace, attrName, decoded));

            switch (attrName)
            {
                case "ObsoleteAttribute":
                    flags |= MemberFlags.Deprecated;
                    isDeprecated = true;
                    deprecationMessage =
                        decoded.FixedArguments.Length > 0 ? decoded.FixedArguments[0].Value as string : null;
                    break;

                case "ReservedAttribute":
                    flags |= MemberFlags.Reserved;
                    break;

                case "AnsiAttribute":
                    flags |= MemberFlags.Ansi;
                    break;

                case "UnicodeAttribute":
                    flags |= MemberFlags.Unicode;
                    break;

                case "FlagsAttribute":
                    isFlags = true;
                    break;

                case "SupportedArchitectureAttribute":
                    supportedArch = (Architecture)
                        (uint)(
                            decoded.FixedArguments[0].Value
                            ?? throw new InvalidOperationException("Null SupportedArchitectureAttribute value")
                        );
                    break;

                case "StructSizeFieldAttribute":
                    structSizeFieldName = (string?)decoded.FixedArguments[0].Value;
                    break;

                case "SupportedOSPlatformAttribute":
                    supportedOSPlatform = (string?)decoded.FixedArguments[0].Value;
                    break;

                case "AlsoUsableForAttribute":
                    var target = (string?)decoded.FixedArguments[0].Value;
                    if (target is not null)
                        alsoUsableFor.Add(target);

                    hasHandleAttr = true;
                    break;

                case "RAIIFreeAttribute":
                case "InvalidHandleValueAttribute":
                    hasHandleAttr = true;
                    break;

                case "MetadataTypedefAttribute":
                case "NativeTypedefAttribute":
                    hasNativeTypedefAttr = true;
                    break;
            }
        }

        // Name-based flags (same as legacy AhkType.GetFlags)
        string typeName = reader.GetString(typeDef.Name);
        if (typeName.EndsWith("_e__Union"))
            flags |= MemberFlags.Union;
        if (typeName.EndsWith("_e__Struct") || typeName.StartsWith("_Anonymous"))
            flags |= MemberFlags.Anonymous;

        bool isHandle = fieldCount == 1 && hasHandleAttr;
        bool isNativeTypedef = fieldCount == 1 && hasNativeTypedefAttr && !hasHandleAttr;

        if (logger != null)
        {
            string fqn = $"{reader.GetString(typeDef.Namespace)}.{typeName}";
            logger.LogTrace("Decoded {Count} attributes for type {FQN}", allAttrs.Count, fqn);
            if (supportedArch.HasValue)
                logger.LogTrace("Type {FQN}: SupportedArchitecture={Arch}", fqn, supportedArch.Value);
            logger.LogTrace(
                "Type {FQN}: IsHandle={IsHandle}, IsFlags={IsFlags}, IsDeprecated={IsDeprecated}",
                fqn,
                isHandle,
                isFlags,
                isDeprecated
            );
        }

        return new TypeAttrs(
            allAttrs,
            supportedArch,
            isHandle,
            isNativeTypedef,
            isFlags,
            isDeprecated,
            deprecationMessage,
            structSizeFieldName,
            supportedOSPlatform,
            alsoUsableFor,
            flags
        );
    }

    /// <summary>
    /// Decode all relevant attributes for a FieldDefinition in a single pass.
    /// </summary>
    public static FieldAttrs DecodeFieldAttributes(MetadataReader reader, FieldDefinition fieldDef)
    {
        MemberFlags flags = MemberFlags.None;
        bool isDeprecated = false;
        string? deprecationMessage = null;
        List<BitfieldMember>? bitfields = null;

        List<CAInfo> allAttrs = [];

        foreach (CustomAttributeHandle attrHandle in fieldDef.GetCustomAttributes())
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (string attrNamespace, string attrName) = GetAttributeTypeName(reader, attr);

            CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);
            allAttrs.Add(new CAInfo(attrNamespace, attrName, decoded));

            switch (attrName)
            {
                case "ObsoleteAttribute":
                    flags |= MemberFlags.Deprecated;
                    isDeprecated = true;
                    deprecationMessage =
                        decoded.FixedArguments.Length > 0 ? decoded.FixedArguments[0].Value as string : null;
                    break;

                case "ReservedAttribute":
                    flags |= MemberFlags.Reserved;
                    break;

                case "NativeBitfieldAttribute":
                    flags |= MemberFlags.NativeBitField;
                    bitfields ??= [];
                    string memberName =
                        (string?)decoded.FixedArguments[0].Value
                        ?? throw new InvalidOperationException("Null NativeBitfieldAttribute name");
                    long bitOffset =
                        (long?)decoded.FixedArguments[1].Value
                        ?? throw new InvalidOperationException("Null NativeBitfieldAttribute offset");
                    long length =
                        (long?)decoded.FixedArguments[2].Value
                        ?? throw new InvalidOperationException("Null NativeBitfieldAttribute length");
                    bitfields.Add(new BitfieldMember(memberName, bitOffset, length));
                    break;
            }
        }

        return new FieldAttrs(allAttrs, flags, isDeprecated, deprecationMessage, bitfields);
    }

    /// <summary>
    /// Extract InvalidHandleValue values from pre-decoded type attributes.
    /// </summary>
    public static IReadOnlyList<long> DecodeInvalidHandleValues(IReadOnlyList<CAInfo> attrs)
    {
        return attrs
            .Where(a => a.Name == "InvalidHandleValueAttribute")
            .Select(a =>
                (long)(
                    a.Attr.FixedArguments[0].Value
                    ?? throw new InvalidOperationException("Null InvalidHandleValueAttribute value")
                )
            )
            .ToList();
    }

    /// <summary>
    /// Extract RAII free function reference from pre-decoded type attributes.
    /// Returns null if no RAIIFreeAttribute is present.
    /// Note: parameter-count validation is deferred to Step 3 (method extraction).
    /// </summary>
    public static FreeFuncRef? DecodeRAIIFreeFunc(IReadOnlyList<CAInfo> attrs, string typeNamespace)
    {
        // CAInfo is a record struct — FirstOrDefault returns default, not null.
        // Check by name instead.
        CAInfo raiiFree = default;
        bool found = false;
        foreach (CAInfo a in attrs)
        {
            if (a.Name == "RAIIFreeAttribute")
            {
                raiiFree = a;
                found = true;
                break;
            }
        }

        if (!found || raiiFree.Attr.FixedArguments.IsDefaultOrEmpty)
            return null;

        string? funcName = (string?)raiiFree.Attr.FixedArguments[0].Value;
        if (funcName == null)
            return null;

        return new FreeFuncRef(funcName, typeNamespace, $"{typeNamespace}.Apis");
    }

    public static CallingConvention DecodeDelegateCallingConvention(IReadOnlyList<CAInfo> attrs)
    {
        var fnPtrAttr = attrs.Single(a => a.Name is "UnmanagedFunctionPointerAttribute");
        var raw = (System.Runtime.InteropServices.CallingConvention)
            (uint)(
                fnPtrAttr.Attr.FixedArguments.First().Value
                ?? throw new NullReferenceException("[UnmanagedFunctionPointer.CallingConvention]")
            );

        return raw switch
        {
            System.Runtime.InteropServices.CallingConvention.Winapi => CallingConvention.WinApi,
            System.Runtime.InteropServices.CallingConvention.StdCall => CallingConvention.StdCall,
            System.Runtime.InteropServices.CallingConvention.Cdecl => CallingConvention.CDecl,
            System.Runtime.InteropServices.CallingConvention.FastCall => CallingConvention.FastCall,
            System.Runtime.InteropServices.CallingConvention.ThisCall => CallingConvention.ThisCall,
            _ => throw new NotImplementedException(raw.ToString()),
        };
    }

    /// <summary>
    /// Decode all relevant attributes for a Parameter in a single pass.
    /// </summary>
    public static ParameterAttrs DecodeParameterAttributes(MetadataReader reader, Parameter param)
    {
        ParameterFlags flags = ParameterFlags.None;
        int sizedBufferBytesParamIndex = -1;
        List<string>? ignoreIfReturnValues = null;
        string? raiiFreeFunc = null;
        string? freeWithFunc = null;

        foreach (CustomAttributeHandle attrHandle in param.GetCustomAttributes())
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (_, string attrName) = GetAttributeTypeName(reader, attr);

            switch (attrName)
            {
                case "ReservedAttribute":
                    flags |= ParameterFlags.Reserved;
                    break;

                case "ConstAttribute":
                    flags |= ParameterFlags.Constant;
                    break;

                case "MemorySizeAttribute":
                {
                    flags |= ParameterFlags.SizedBuffer;
                    CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);
                    foreach (var arg in decoded.NamedArguments)
                    {
                        if (arg.Name == "BytesParamIndex")
                            sizedBufferBytesParamIndex = (short)(
                                arg.Value ?? throw new InvalidOperationException("Null BytesParamIndex")
                            );
                    }
                    break;
                }

                case "ComOutPtrAttribute":
                    flags |= ParameterFlags.ComOutPtr;
                    break;

                case "RetValAttribute":
                    flags |= ParameterFlags.RetVal;
                    break;

                case "DoNotReleaseAttribute":
                    flags |= ParameterFlags.DoNotRelease;
                    break;

                case "IgnoreIfReturnAttribute":
                {
                    flags |= ParameterFlags.HasIgnoreIfReturn;
                    CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);
                    string val = (string)(
                        decoded.FixedArguments[0].Value
                        ?? throw new InvalidOperationException("Null IgnoreIfReturnAttribute value")
                    );
                    ignoreIfReturnValues ??= [];
                    ignoreIfReturnValues.Add(val);
                    break;
                }

                case "RAIIFreeAttribute":
                {
                    flags |= ParameterFlags.HasRAIIFree;
                    CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);
                    raiiFreeFunc = (string)(
                        decoded.FixedArguments[0].Value
                        ?? throw new InvalidOperationException("Null RAIIFreeAttribute value")
                    );
                    break;
                }

                case "FreeWithAttribute":
                {
                    flags |= ParameterFlags.HasFreeWith;
                    CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);
                    freeWithFunc = (string)(
                        decoded.FixedArguments[0].Value
                        ?? throw new InvalidOperationException("Null FreeWithAttribute value")
                    );
                    break;
                }
            }
        }

        return new ParameterAttrs(flags, sizedBufferBytesParamIndex, ignoreIfReturnValues, raiiFreeFunc, freeWithFunc);
    }

    /// <summary>
    /// Decode method-level custom attributes in a single pass.
    /// </summary>
    public static MethodAttrs DecodeMethodAttributes(MetadataReader reader, MethodDefinition methodDef)
    {
        bool preserveSig = false;
        bool? preserveSigValue = null;
        bool canReturnErrorsAsSuccess = false;
        bool canReturnMultipleSuccessValues = false;
        string? deprecationMessage = null;
        string? supportedOSPlatform = null;
        Architecture supportedArchitecture = Architecture.All;

        foreach (CustomAttributeHandle attrHandle in methodDef.GetCustomAttributes())
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (_, string attrName) = GetAttributeTypeName(reader, attr);
            CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);

            switch (attrName)
            {
                case "PreserveSigAttribute":
                    preserveSig = true;
                    preserveSigValue =
                        decoded.FixedArguments.Length <= 0 || (decoded.FixedArguments[0].Value as bool? ?? true);
                    break;

                case "CanReturnErrorsAsSuccessAttribute":
                    canReturnErrorsAsSuccess = true;
                    break;

                case "CanReturnMultipleSuccessValuesAttribute":
                    canReturnMultipleSuccessValues = true;
                    break;

                case "ObsoleteAttribute":
                    deprecationMessage =
                        decoded.FixedArguments.Length > 0 ? decoded.FixedArguments[0].Value as string : null;
                    break;

                case "SupportedOSPlatformAttribute":
                    supportedOSPlatform = (string?)decoded.FixedArguments[0].Value;
                    break;

                case "SupportedArchitectureAttribute":
                    supportedArchitecture = (Architecture)
                        (uint)(
                            decoded.FixedArguments[0].Value
                            ?? throw new InvalidOperationException("Null SupportedArchitectureAttribute value")
                        );
                    break;
            }
        }

        return new MethodAttrs(
            preserveSig,
            preserveSigValue,
            canReturnErrorsAsSuccess,
            canReturnMultipleSuccessValues,
            deprecationMessage,
            supportedOSPlatform,
            supportedArchitecture
        );
    }

    /// <summary>
    /// Decode a GUID from a TypeDefinition's GuidAttribute. Returns null if not present.
    /// </summary>
    public static Guid? DecodeGuid(MetadataReader reader, TypeDefinition typeDef)
    {
        CustomAttribute? attr = FindAttribute(reader, typeDef.GetCustomAttributes(), "GuidAttribute");
        return attr.HasValue ? DecodeGuidFromAttribute(attr.Value) : null;
    }

    /// <summary>
    /// Decode a GUID from a FieldDefinition's GuidAttribute. Returns null if not present.
    /// </summary>
    public static Guid? DecodeGuid(MetadataReader reader, FieldDefinition fieldDef)
    {
        CustomAttribute? attr = FindAttribute(reader, fieldDef.GetCustomAttributes(), "GuidAttribute");
        return attr.HasValue ? DecodeGuidFromAttribute(attr.Value) : null;
    }

    /// <summary>
    /// Decode a GUID from a custom attribute with 11 fixed arguments.
    /// </summary>
    public static Guid DecodeGuidFromAttribute(CustomAttribute attr)
    {
        CustomAttributeValue<string> decoded = attr.DecodeValue(s_caProvider);

        if (decoded.FixedArguments.Length != 11)
            throw new ArgumentException($"GuidAttribute has {decoded.FixedArguments.Length} arguments, expected 11");

        return new Guid(
            (int)(uint)decoded.FixedArguments[0].Value!,
            (short)(ushort)decoded.FixedArguments[1].Value!,
            (short)(ushort)decoded.FixedArguments[2].Value!,
            (byte)decoded.FixedArguments[3].Value!,
            (byte)decoded.FixedArguments[4].Value!,
            (byte)decoded.FixedArguments[5].Value!,
            (byte)decoded.FixedArguments[6].Value!,
            (byte)decoded.FixedArguments[7].Value!,
            (byte)decoded.FixedArguments[8].Value!,
            (byte)decoded.FixedArguments[9].Value!,
            (byte)decoded.FixedArguments[10].Value!
        );
    }

    /// <summary>
    /// Decode a GUID from a queue of string values (used for ConstantAttribute parsing).
    /// </summary>
    public static Guid DecodeGuidFromQueue(Queue<string> values)
    {
        if (values.Count < 11)
            throw new ArgumentException($"Queue has {values.Count} elements, GUIDs require 11");

        return new Guid(
            (int)uint.Parse(values.Dequeue()),
            (short)ushort.Parse(values.Dequeue()),
            (short)ushort.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue()),
            byte.Parse(values.Dequeue())
        );
    }

    /// <summary>
    /// Find a single attribute by name in a collection.
    /// </summary>
    public static CustomAttribute? FindAttribute(
        MetadataReader reader,
        CustomAttributeHandleCollection handles,
        string targetName
    )
    {
        foreach (CustomAttributeHandle attrHandle in handles)
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (_, string attrName) = GetAttributeTypeName(reader, attr);
            if (attrName == targetName)
                return attr;
        }
        return null;
    }

    /// <summary>
    /// Get all attribute names from a collection.
    /// </summary>
    public static IEnumerable<string> GetAllAttributeNames(
        MetadataReader reader,
        CustomAttributeHandleCollection handles
    )
    {
        foreach (CustomAttributeHandle attrHandle in handles)
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (_, string attrName) = GetAttributeTypeName(reader, attr);
            yield return attrName;
        }
    }

    /// <summary>
    /// Resolve the (Namespace, Name) of a custom attribute's declaring type.
    /// </summary>
    public static (string Namespace, string Name) GetAttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                MemberReference mr = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                EntityHandle parent = mr.Parent;

                if (parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference tr = reader.GetTypeReference((TypeReferenceHandle)parent);
                    return (reader.GetString(tr.Namespace), reader.GetString(tr.Name));
                }
                else if (parent.Kind == HandleKind.TypeDefinition)
                {
                    TypeDefinition td = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                    return (reader.GetString(td.Namespace), reader.GetString(td.Name));
                }
                break;
            }

            case HandleKind.MethodDefinition:
            {
                MethodDefinition md = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                TypeDefinition td = reader.GetTypeDefinition(md.GetDeclaringType());
                return (reader.GetString(td.Namespace), reader.GetString(td.Name));
            }
        }

        throw new NotSupportedException($"Unsupported attribute constructor kind: {attr.Constructor.Kind}");
    }
}
