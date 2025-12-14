using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using Gma.DataStructures.StringSearch;  // https://github.com/gmamaladze/trienet

[Flags]
public enum CustomParamAttributes
{
    None = 0,
    Reserved = 1,
    Constant = 2,
    SizedBuffer = 4,
    ComOutPtr = 8,
    RetVal = 16,
    DoNotRelease = 32,
    HasIgnoreIfReturn = 64,  // Caller will need to decode the value but we can indicate that it exists
    HasRAIIFree = 128,
    HasFreeWith = 256
}

public readonly record struct AhkParameter
{
    // We also need to ensure no conflicts with type names since AHK names are case-insensitive. I'm not 
    // happy about this either. Ideally we'd populate this with every type name we're going to load before
    // starting codegen, but doing it per-assembly as we encounter them is good enough in practice.
    private static readonly PatriciaTrie<string> ReservedNames;

    private static readonly HashSet<MetadataReader> IndexedAssemblies = [];

    static AhkParameter()
    {
        // Initial population of our reserved name trie
        ReservedNames = new();
        string[] constReservedNames = ["in", "as", "is", "contains", "not", "and", "or", "this", "return", 
            "throw", "loop", "do", "while", "float", "number", "integer", "object", "class", "buffer", "string",
            "file", "enumerator"];

        foreach(string name in constReservedNames)
        {
            ReservedNames.Add(name, name);
        }
    }

    public readonly MetadataReader? mr;

    public readonly Parameter param;

    public readonly string Name;

    public readonly int SequenceNumber => param.SequenceNumber;
    public readonly FieldInfo FieldInfo;
    public readonly ParameterAttributes Attributes => param.Attributes;
    public readonly CustomParamAttributes CustomAttributes;

    public readonly AhkMethod? RAIIFree;

    public readonly AhkMethod? FreeWith;

    public readonly List<string>? IgnoreIfReturnValues;

    public bool IsInParam => Attributes.HasFlag(ParameterAttributes.In);
    public bool IsOutParam => Attributes.HasFlag(ParameterAttributes.Out);
    public bool Optional => Attributes.HasFlag(ParameterAttributes.Optional);
    public bool Constant => CustomAttributes.HasFlag(CustomParamAttributes.Constant);
    public bool Reserved => CustomAttributes.HasFlag(CustomParamAttributes.Reserved);
    public bool IsReturnValue => CustomAttributes.HasFlag(CustomParamAttributes.RetVal);
    public bool IsComOutPtr => CustomAttributes.HasFlag(CustomParamAttributes.ComOutPtr);
    public bool ScriptOwned => !CustomAttributes.HasFlag(CustomParamAttributes.DoNotRelease);

    [MemberNotNullWhen(true, nameof(IgnoreIfReturnValues))]
    public bool HasIgnoreIfReturn => CustomAttributes.HasFlag(CustomParamAttributes.HasIgnoreIfReturn);

    public bool HasRAIIFree => RAIIFree is not null;

    public bool HasFreeWith => FreeWith is not null;

    public bool IsPtr => FieldInfo.Kind == SimpleFieldKind.Pointer;
    public bool IsPrimitive => FieldInfo.Kind == SimpleFieldKind.Primitive;
    public bool IsArray => FieldInfo.Kind == SimpleFieldKind.Array;
    public bool IsStruct => FieldInfo.Kind == SimpleFieldKind.Struct;
    public bool IsString => FieldInfo.Kind == SimpleFieldKind.String;
    public bool IsHRESULT => FieldInfo.Kind == SimpleFieldKind.HRESULT;
    public bool IsCom => FieldInfo.Kind == SimpleFieldKind.COM;
    public bool IsClass => FieldInfo.Kind == SimpleFieldKind.Class;
    public bool IsOther => FieldInfo.Kind == SimpleFieldKind.Other;

    public bool IsPtrToPrimitive => IsPtr && (FieldInfo.UnderlyingType?.Kind is SimpleFieldKind.Primitive or SimpleFieldKind.Pointer or SimpleFieldKind.NativeTypedef or SimpleFieldKind.HRESULT);

    public bool IsPtrToCom => IsPtr && (FieldInfo.UnderlyingType?.Kind is SimpleFieldKind.COM);

    public bool IsPtrToNativeTypedef => IsPtr && (FieldInfo.UnderlyingType?.Kind is SimpleFieldKind.NativeTypedef);
    
    public bool IsPtrToStruct => IsPtr && (FieldInfo.UnderlyingType?.Kind is SimpleFieldKind.Struct);

    public bool IsPtrToString => IsPtr && (FieldInfo.UnderlyingType?.Kind is SimpleFieldKind.String);
    
    public AhkParameter(MetadataReader? mr, Parameter param, FieldInfo FieldInfo)
    {
        this.mr = mr;
        this.param = param;
        this.FieldInfo = FieldInfo;

        Name = GetName();

        CustomAttributes = GetCustomParamAttributes();
        IgnoreIfReturnValues = GetIgnoreIfReturnValues();

        FreeWith = MaybeGetReleaseMethod("FreeWithAttribute");
        RAIIFree = MaybeGetReleaseMethod("RAIIFreeAttribute");
    }

    private string GetName()
    {
        string? paramName = mr?.GetString(param.Name) ;
        string? nameVal = paramName?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(nameVal) || string.IsNullOrWhiteSpace(paramName) || mr is null)
        {
            return "result";
        }

        // Index the current assembly's type names as reserved words if necessary
        if(!IndexedAssemblies.Contains(mr))
        {
            foreach(TypeDefinitionHandle hTd in mr.TypeDefinitions)
            {
                TypeDefinition td = mr.GetTypeDefinition(hTd);
                string tdName = mr.GetString(td.Name).ToLowerInvariant();
                ReservedNames.Add(tdName, tdName);
            }

            IndexedAssemblies.Add(mr);
        }

        if (ReservedNames.Retrieve(nameVal).Any(n => n.Equals(nameVal)))
        {
            paramName = "_" + paramName;
        }

        return paramName;
    }

    private CustomParamAttributes GetCustomParamAttributes()
    {
        CustomParamAttributes attrs = CustomParamAttributes.None;
        if(mr is null)
            return attrs;

        foreach (string attrName in CustomAttributeDecoder.GetAllNames(mr, param))
        {
            attrs |= attrName switch
            {
                "ReservedAttribute" => CustomParamAttributes.Reserved,
                "ConstAttribute" => CustomParamAttributes.Constant,
                "MemorySizeAttribute" => CustomParamAttributes.SizedBuffer,
                "ComOutPtrAttribute" => CustomParamAttributes.ComOutPtr,
                "RetValAttribute" => CustomParamAttributes.RetVal,
                "DoNotReleaseAttribute" => CustomParamAttributes.DoNotRelease,
                "IgnoreIfReturnAttribute" => CustomParamAttributes.HasIgnoreIfReturn,
                _ => 0
            };
        }

        return attrs;
    }

    private List<string>? GetIgnoreIfReturnValues()
    {
        if(mr is null)
            return null;

        var conditions = CustomAttributeDecoder.DecodeAll(mr, param)
            .Where(attr => attr.Name == "IgnoreIfReturnAttribute")
            .Select(info => info.Attr.FixedArguments[0].Value)
            .Select(v => (string)(v ?? throw new NullReferenceException(nameof(v))))
            .ToList();

        return conditions.Count > 0? conditions : null;
    }

    private AhkMethod? MaybeGetReleaseMethod(string attrName)
    {
        if(mr is null)
            return null;

        string? attrVal = MaybeGetParamAttrValue(attrName);
        if(attrVal is null)
        {
            return null;
        }

        AhkMethod method = AhkMethod.Get(mr, attrVal);
        if(method.parameters.Count != 2)
        {
            return null;
        }

        return method;
    }

    private string? MaybeGetParamAttrValue(string attrName)
    {
        if(mr is null)
            return null;

        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(mr, param, attrName);
        if (attr.HasValue)
        {
            var decoded = attr.Value.DecodeValue(new CaTypeProvider());
            return (string)(decoded.FixedArguments[0].Value 
                ?? throw new NullReferenceException(nameof(decoded.FixedArguments)));
        }

        return null;
    }

    public bool IsPtrToHandle()
    {
        if (!IsPtr || FieldInfo.UnderlyingType?.TypeDef is null || FieldInfo.UnderlyingType?.Reader is null)
            return false;

        return AhkStruct.TypeIsHandle(
            FieldInfo.UnderlyingType?.Reader ?? throw new NullReferenceException(), 
            FieldInfo.UnderlyingType?.TypeDef ?? throw new NullReferenceException()
        );
    }

    public bool IsHandle()
    {
        if (!FieldInfo.TypeDef.HasValue)
            return false;
        return AhkStruct.TypeIsHandle(FieldInfo.Reader ?? throw new NullReferenceException(nameof(FieldInfo.Reader))
            , FieldInfo.TypeDef.Value);
    }

    public string? GetTypeDefName()
    {
        if (FieldInfo == null || FieldInfo.TypeDef == null || FieldInfo.Reader == null)
            return null;
        return FieldInfo.Reader.GetString(FieldInfo.TypeDef.Value.Name);
    }
    
    public string? GetTypeDefNamespace() {
        if (FieldInfo == null || FieldInfo.TypeDef == null || FieldInfo.Reader == null)
            return null;
        return FieldInfo.Reader.GetString(FieldInfo.TypeDef.Value.Namespace);
    }
}