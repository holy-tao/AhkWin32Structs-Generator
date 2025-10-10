using System.Reflection.Metadata;

class GuidDecoder
{
    /// <summary>
    /// Parse a Guid out of a [Windows.Win32.Foundation.Metadata.Guid] attribute
    /// </summary>
    /// <param name="attr">Custom attribute to parse</param>
    /// <returns>Parsed GUID</returns>
    /// <exception cref="ArgumentException"></exception>
    public static Guid DecodeFromAtribute(CustomAttribute attr)
    {
        var decoded = attr.DecodeValue(new CaTypeProvider());

        if (decoded.FixedArguments.Length != 11)
            throw new ArgumentException("Fixed arguments don't match for GUID");

        // Attribute is like [Windows.Win32.Foundation.Metadata.Guid(1851660560u, 32851, 18016, 183, 149, 107, 97, 46, 41, 188, 88)]
        return new(
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

    public static Guid DecodeGuid(MetadataReader reader, TypeDefinition typeDef)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(reader, typeDef, "GuidAttribute");
        if (!attr.HasValue)
            throw new NullReferenceException($"Type definition '{reader.GetString(typeDef.Namespace)}.{reader.GetString(typeDef.Name)}' has no GuidAttribute");

        return DecodeFromAtribute(attr.Value);
    }

    public static Guid DecodeGuid(MetadataReader reader, FieldDefinition fieldDef)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(reader, fieldDef, "GuidAttribute");
        if (!attr.HasValue)
            throw new NullReferenceException($"Field definition '{reader.GetString(fieldDef.Name)}' has no GuidAttribute");

        return DecodeFromAtribute(attr.Value);
    }
}