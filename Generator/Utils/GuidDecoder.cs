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

    /// <summary>
    /// Decodes a the Guid of a Type Definition of it has a GuidAttribute
    /// </summary>
    /// <param name="reader">reader for metadata</param>
    /// <param name="typeDef">TypeDef to get the Guid for</param>
    /// <returns></returns>
    public static Guid? MaybeDecodeGuid(MetadataReader reader, TypeDefinition typeDef)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(reader, typeDef, "GuidAttribute");
        if (!attr.HasValue)
            return null;

        return DecodeFromAtribute(attr.Value);
    }

    /// <summary>
    /// Decodes a Guid from a TypeDef
    /// </summary>
    /// <param name="reader">reader for metadata</param>
    /// <param name="typeDef">TypeDef to get the Guid for</param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException">If typeDef has no GuidAttribute</exception>
    public static Guid DecodeGuid(MetadataReader reader, TypeDefinition typeDef)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(reader, typeDef, "GuidAttribute");
        if (!attr.HasValue)
            throw new NullReferenceException($"Type definition '{reader.GetString(typeDef.Namespace)}.{reader.GetString(typeDef.Name)}' has no GuidAttribute");

        return DecodeFromAtribute(attr.Value);
    }

    /// <summary>
    /// Decodes a Guid from a Field Definition
    /// </summary>
    /// <param name="reader">reader for metadata</param>
    /// <param name="fieldDef">field to get the Guid for</param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException">if fieldDef has no GuidAttribute</exception>
    public static Guid DecodeGuid(MetadataReader reader, FieldDefinition fieldDef)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(reader, fieldDef, "GuidAttribute");
        if (!attr.HasValue)
            throw new NullReferenceException($"Field definition '{reader.GetString(fieldDef.Name)}' has no GuidAttribute");

        return DecodeFromAtribute(attr.Value);
    }
}