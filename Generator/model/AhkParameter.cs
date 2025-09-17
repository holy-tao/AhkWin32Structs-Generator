using System.Reflection;
using System.Reflection.Metadata;

[Flags]
public enum CustomParamAttributes
{
    None = 0,
    Reserved = 1,
    Constant = 2,
    SizedBuffer = 4
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

    internal bool IsInParam => Attributes.HasFlag(ParameterAttributes.In);
    internal bool IsOutParam => Attributes.HasFlag(ParameterAttributes.Out);
    internal bool Optional => Attributes.HasFlag(ParameterAttributes.Optional);
    internal bool Constant => CustomAttributes.HasFlag(CustomParamAttributes.Constant);
    internal bool Reserved => CustomAttributes.HasFlag(CustomParamAttributes.Reserved);
}