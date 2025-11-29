using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;

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
    // For sanitizing parameter names
    public static string[] ReservedNames = ["in", "as", "is", "contains", "not", "and", "or", "this", "return", 
        "throw", "loop", "do", "while", "float", "number", "integer", "object", "class", "buffer"];

    public readonly string Name;
    public readonly int SequenceNumber;
    public readonly FieldInfo FieldInfo;
    public readonly ParameterAttributes Attributes;
    public readonly CustomParamAttributes CustomAttributes;

    public readonly string? RAIIFree;

    public readonly string? FreeWith;

    public readonly List<string>? IgnoreIfReturnValues;

    public AhkParameter(string Name, int SequenceNumber, FieldInfo FieldInfo, ParameterAttributes Attributes, 
        CustomParamAttributes CustomAttributes, string? RAIIFree = null, string? FreeWith = null, List<string>? IgnoreIfReturnValues = null)
    {
        if (ReservedNames.Contains(Name.ToLowerInvariant()))
        {
            Name += "_R";
        }

        this.Name = Name;
        this.SequenceNumber = SequenceNumber;
        this.FieldInfo = FieldInfo;
        this.Attributes = Attributes;
        this.CustomAttributes = CustomAttributes;

        this.RAIIFree = RAIIFree;
        this.FreeWith = FreeWith;
        this.IgnoreIfReturnValues = IgnoreIfReturnValues;
    }

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

    [MemberNotNullWhen(true, nameof(RAIIFree))]
    public bool HasRAIIFree => CustomAttributes.HasFlag(CustomParamAttributes.HasRAIIFree);

    [MemberNotNullWhen(true, nameof(FreeWith))]
    public bool HasFreeWith => CustomAttributes.HasFlag(CustomParamAttributes.HasFreeWith);

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

    public bool IsPtrToHandle(MetadataReader mr)
    {
        if (!IsPtr || FieldInfo.UnderlyingType?.TypeDef == null)
            return false;

        return AhkStruct.IsHandle(mr, FieldInfo.UnderlyingType?.TypeDef ?? throw new NullReferenceException());
    }

    public bool IsHandle(MetadataReader mr)
    {
        if (!FieldInfo.TypeDef.HasValue)
            return false;
        return AhkStruct.IsHandle(mr, FieldInfo.TypeDef.Value);
    }

    public string? GetTypeDefName(MetadataReader mr)
    {
        if (FieldInfo == null || FieldInfo.TypeDef == null)
            return null;
        return mr.GetString(FieldInfo.TypeDef.Value.Name);
    }
    
    public string? GetTypeDefNamespace(MetadataReader mr) {
        if (FieldInfo == null || FieldInfo.TypeDef == null)
            return null;
        return mr.GetString(FieldInfo.TypeDef.Value.Namespace);
    }
}