using System.Numerics;
using System.Reflection.Metadata;

public readonly record struct ConstantInfo(string Name, ConstantTypeCode TypeCode, object Value, FieldDefinition fieldDef)
{
    public string ValueAsAhkLiteral => TypeCode switch
    {
        ConstantTypeCode.Byte => $"0x{((byte)Value).ToString("X2")}",  //Bytes in hex format
        ConstantTypeCode.SByte => $"0x{((sbyte)Value).ToString("X2")}",
        _ => Value.ToString() ??
            throw new NullReferenceException($"{Name} ({TypeCode}): Value of type {Value.GetType()}'s ToString() returned null"),
    };
}

public class ConstantDecoder
{
    public static ConstantInfo DecodeConstant(MetadataReader reader, FieldDefinition fieldDef)
    {
        if (fieldDef.GetDefaultValue().IsNil)
        {
            throw new ArgumentException($"{reader.GetString(fieldDef.Name)} is not a constant");
        }

        ConstantHandle constHandle = fieldDef.GetDefaultValue();
        Constant constant = reader.GetConstant(constHandle);

        BlobReader blob = reader.GetBlobReader(constant.Value);

        object value = constant.TypeCode switch
        {
            ConstantTypeCode.Int32  => blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int16  => blob.ReadInt16(),
            ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int64  => blob.ReadInt64(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            ConstantTypeCode.Byte   => blob.ReadByte(),
            ConstantTypeCode.SByte  => blob.ReadSByte(),
            _ => throw new NotSupportedException($"Unexpected enum constant type {constant.TypeCode}")
        };

        return new ConstantInfo(reader.GetString(fieldDef.Name), constant.TypeCode, value, fieldDef);
    }
}