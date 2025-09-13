using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection;

public readonly record struct ConstantInfo(string Name, ConstantTypeCode TypeCode, object Value, FieldDefinition fieldDef)
{
    public string ValueAsAhkLiteral => TypeCode switch
    {
        ConstantTypeCode.Byte => $"0x{((byte)Value).ToString("X2")}",   //Bytes in hex format
        ConstantTypeCode.SByte => $"0x{((sbyte)Value).ToString("X2")}",
        ConstantTypeCode.String => $"\"{Value}\"",                      // Add quotes to strings
        _ => Value.ToString() ??
            throw new NullReferenceException($"{Name} ({TypeCode}): Value of type {Value.GetType()}'s ToString() returned null"),
    };

    public string Ahktype => TypeCode switch
    {
        ConstantTypeCode.String => "String",
        _ => $"Integer ({TypeCode})"
    };
}

public class ConstantDecoder
{
    public static ConstantInfo DecodeConstant(MetadataReader reader, FieldDefinition fieldDef)
    {
        if (CustomAttributeDecoder.GetAllNames(reader, fieldDef).Contains("GuidAttribute"))
            return DecodeGuidConstant(reader, fieldDef);

        if (fieldDef.GetDefaultValue().IsNil)
            throw new ArgumentException($"{reader.GetString(fieldDef.Name)} is not a constant");

        ConstantHandle constHandle = fieldDef.GetDefaultValue();
        Constant constant = reader.GetConstant(constHandle);

        BlobReader blob = reader.GetBlobReader(constant.Value);

        object value = constant.TypeCode switch
        {
            ConstantTypeCode.Int32 => blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int16 => blob.ReadInt16(),
            ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int64 => blob.ReadInt64(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.SByte => blob.ReadSByte(),
            ConstantTypeCode.Single => blob.ReadSingle(),
            ConstantTypeCode.Double => blob.ReadDouble(),
            ConstantTypeCode.Char => blob.ReadChar(),
            ConstantTypeCode.String => blob.ReadUTF16(blob.Length),
            _ => throw new NotSupportedException($"Unexpected enum constant type {constant.TypeCode}: {reader.GetString(fieldDef.Name)}")
        };

        return new ConstantInfo(reader.GetString(fieldDef.Name), constant.TypeCode, value, fieldDef);
    }

    public static ConstantInfo DecodeGuidConstant(MetadataReader reader, FieldDefinition fieldDef)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(reader, fieldDef, "GuidAttribute");
        if (!attr.HasValue)
            throw new NullReferenceException($"Field definition '{reader.GetString(fieldDef.Name)}' has no GuidAttribute");

        var decoded = ((CustomAttribute)attr).DecodeValue(new CaTypeProvider());

        if (decoded.FixedArguments.Length != 11)
            throw new ArgumentException("Fixed arguments don't match for GUID");

        // Attribute is like [Windows.Win32.Foundation.Metadata.Guid(1851660560u, 32851, 18016, 183, 149, 107, 97, 46, 41, 188, 88)]
        Guid guid = new(
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

        return new ConstantInfo(reader.GetString(fieldDef.Name), ConstantTypeCode.String, $"{{{guid.ToString("D")}}}", fieldDef);
    }

    public static bool IsConstant(MetadataReader reader, FieldDefinition fieldDef)
    {
        var customAttrs = CustomAttributeDecoder.GetAllNames(reader, fieldDef);

        return fieldDef.Attributes.HasFlag(FieldAttributes.Literal) ||
            customAttrs.Contains("ConstAttribute") ||
            (fieldDef.Attributes.HasFlag(FieldAttributes.Static) && customAttrs.Contains("GuidAttribute"));
    }
}