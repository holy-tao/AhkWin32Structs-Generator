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
    RetVal = 16
}

public readonly record struct AhkParameter
{
    // For sanitizing parameter names
    public static string[] ReservedNames = ["in", "as", "is", "contains", "not", "and", "or", "this"];

    public readonly string Name;
    public readonly int SequenceNumber;
    public readonly FieldInfo FieldInfo;
    public readonly ParameterAttributes Attributes;
    public readonly CustomParamAttributes CustomAttributes;

    public AhkParameter(string Name, int SequenceNumber, FieldInfo FieldInfo, ParameterAttributes Attributes, CustomParamAttributes CustomAttributes)
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
    }

    public bool IsInParam => Attributes.HasFlag(ParameterAttributes.In);
    public bool IsOutParam => Attributes.HasFlag(ParameterAttributes.Out);
    public bool Optional => Attributes.HasFlag(ParameterAttributes.Optional);
    public bool Constant => CustomAttributes.HasFlag(CustomParamAttributes.Constant);
    public bool Reserved => CustomAttributes.HasFlag(CustomParamAttributes.Reserved);
    public bool IsReturnValue => CustomAttributes.HasFlag(CustomParamAttributes.RetVal);
    public bool IsComOutPtr => CustomAttributes.HasFlag(CustomParamAttributes.ComOutPtr);
}