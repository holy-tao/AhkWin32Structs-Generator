using System.Reflection.Metadata;

public static class StringExtensions
{
    public static string TrimEnd(this string source, string value)
    {
        if (!source.EndsWith(value))
            return source;

        return source[..source.LastIndexOf(value)];
    }
}

public static class MetadataReaderExtensions
{
    /// <summary>
    /// Returns the fully qualified name of the type definition.
    /// </summary>
    /// <param name="reader"></param>
    /// <param name="handle"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException">If handle is nil</exception>
    public static string GetFullyQualifiedName(this MetadataReader reader, TypeDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new NullReferenceException(nameof(handle));

        return reader.GetFullyQualifiedName(reader.GetTypeDefinition(handle));
    }

    /// <summary>
    /// Returns the fully qualified name of the type definition.
    /// </summary>
    /// <param name="reader"></param>
    /// <param name="typeDef"></param>
    /// <returns></returns>
    public static string GetFullyQualifiedName(this MetadataReader reader, TypeDefinition typeDef)
    {
        string name = reader.GetString(typeDef.Name);
        string namespaceName = reader.GetString(typeDef.Namespace);

        return $"{namespaceName}.{name}";
    }

    /// <summary>
    /// Returns the fully qualified name of the method definition in the format 
    /// <declarer namespace>.<declarer name>.<method name>
    /// </summary>
    /// <param name="reader"></param>
    /// <param name="methodDef"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public static string GetFullyQualifiedName(this MetadataReader reader, MethodDefinition methodDef)
    {
        TypeDefinitionHandle declaringTypeHandle = methodDef.GetDeclaringType();
        if (declaringTypeHandle.IsNil)
            throw new NullReferenceException(nameof(methodDef.GetDeclaringType));

        TypeDefinition declaringType = reader.GetTypeDefinition(declaringTypeHandle);
        string declaringTypeName = reader.GetFullyQualifiedName(declaringType);

        string methodName = reader.GetString(methodDef.Name);

        return $"{declaringTypeName}.{methodName}";
    }
}