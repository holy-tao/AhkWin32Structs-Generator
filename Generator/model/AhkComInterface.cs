
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// A COM property, represented by its getter and/or setter methods
/// </summary>
/// <param name="Name">Name of the property</param>
/// <param name="Getter">Getter method for the property, if any</param>
/// <param name="Setter">Setter method for the property, if any</param>
record struct AhkComProperty(AhkType Interface, string Name, AhkMethod? Getter, AhkMethod? Setter)
{
    public void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    {Name} {{");

        if (Getter is not null)
            sb.AppendLine($"        get => this.{Getter.GetDeduplicatedName()}()");

        if (Setter is not null)
            sb.AppendLine($"        set => this.{Setter.GetDeduplicatedName()}(value)");

        sb.AppendLine("    }");
    }

    public void MaybeAppendDocumentation(StringBuilder sb)
    {
        // Doesn't seem like ApiDocs have anything for properties or getters / setters
        // Keeping this here in case that changes in the future
        sb.AppendLine("    /**");

        if(Interface.apiDetails != null)
        {
            ApiDetails apiDetails = Interface.apiDetails;
            if(apiDetails.Fields.TryGetValue(Name, out string? fieldDetails))
            {
                sb.AppendLine($"     * {AhkType.EscapeDocs(fieldDetails, "     ")}");
            }
        }

        // Type is getter's return type if it exists, otherwise setter's parameter type
        AhkParameter? param = null;
        if (Getter is not null)
        {
            param = Getter.outputParameter;
        }
        else if (Setter is not null)
        {
            param = Setter.parameters.First(p => !p.Reserved);
        }

        if(param is not null)
        {
            AhkParameter typeParam = (AhkParameter)param;
            string? actualValueName = typeParam.IsPtr ? typeParam.FieldInfo.UnderlyingType?.AhkType : typeParam.FieldInfo.AhkType;
            sb.AppendLine($"     * @type {{{actualValueName}}} ");
        }

        sb.AppendLine("     */");
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        ToAhk(sb);
        return sb.ToString();
    }
}

class AhkComInterface : AhkType
{
    // Interface ID for this interface
    public readonly Guid? iid;

    // CLSID for an instantiatable object the implements this interface, if any
    public readonly Guid? clsid;

    public readonly (MetadataReader reader, TypeDefinition def)? BaseInterface;

    public readonly List<AhkComMethod> Methods;

    public readonly List<AhkComProperty> Properties;

    public readonly int VTableOffset;

    public AhkComInterface(MetadataReader mr, TypeDefinition typeDef) : base(mr, typeDef)
    {
        iid = GuidDecoder.MaybeDecodeGuid(mr, typeDef);
        clsid = GetClsid();

        BaseInterface = GetBaseTypeDef(typeDef);
        VTableOffset = GetVTableOffset();

        Methods = typeDef.GetMethods()
            .Select((handle, i) => new AhkComMethod(this, mr, mr.GetMethodDefinition(handle), i + VTableOffset))
            .ToList();

        // Collect properties from special-name methods
        Properties = [];
        foreach(AhkComMethod method in Methods.Where(m => m.IsSpecialName))
        {
            string normalizedName = method.GetDeduplicatedName()[4..]; // Remove "get_" or "put_"
            if(Properties.Any(p => p.Name == normalizedName))
                continue;

            AhkComMethod? getter = Methods.FirstOrDefault(m => m!.IsSpecialName && m.GetDeduplicatedName() == "get_" + normalizedName, null);
            AhkComMethod? setter = Methods.FirstOrDefault(m => m!.IsSpecialName && m.GetDeduplicatedName() == "put_" + normalizedName, null);
            Properties.Add(new AhkComProperty(this, normalizedName, getter, setter));
        }
    }

    private (MetadataReader, TypeDefinition)? GetBaseTypeDef(TypeDefinition forType)
    {
        // All WinRT interfaces implicitly extend IUnknown. They may implement other interfaces, but that means
        // that querying for them is guaranteed to succeed and has no impact on VTable layout, which is what we
        // care about here
        if (forType.Attributes.HasFlag(TypeAttributes.WindowsRuntime))
        {
            TypeDefinitionHandle hDef = FieldSignatureDecoder.FindTypeDefinition("Windows.Win32",
                "Windows.Win32.System.Com", "IUnknown", out var baseReader);
            return (baseReader, baseReader.GetTypeDefinition(hDef));
        }

        var impls = GetInterfaceImplementations(forType);
        return impls.Count() switch
        {
            0 => null,
            1 => impls.First(),
            _ => throw new NotSupportedException($"Extends too many interfaces [{string.Join(", ", impls.Select(td => mr.GetString(td.typeDef.Name)))}]")
        };
    }

    /// <summary>
    /// Collects all directly implemented interfaces for this interface and resolves any TypeReferences.
    /// </summary>
    /// <returns>All directly implemented interfaces for this interface</returns>
    /// <exception cref="NullReferenceException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public IEnumerable<(MetadataReader reader, TypeDefinition typeDef)> GetInterfaceImplementations(TypeDefinition forType)
    {
        return forType.GetInterfaceImplementations()
            .Select(ih => mr.GetInterfaceImplementation(ih).Interface)
            .Select(iface =>
            {
                switch(iface.Kind) 
                {
                    case HandleKind.TypeReference:
                        return FieldSignatureDecoder.ResolveTypeReference(mr, (TypeReferenceHandle)iface);

                    case HandleKind.TypeDefinition:
                        return (mr, mr.GetTypeDefinition((TypeDefinitionHandle)iface));

                    case HandleKind.TypeSpecification:
                        TypeSpecification typeSpec = mr.GetTypeSpecification((TypeSpecificationHandle)iface);
                        var resolved = typeSpec.DecodeSignature(new FieldSignatureProvider(mr), new());

                        return (
                            resolved.Reader ?? throw new NullReferenceException(nameof(resolved.Reader)),
                            resolved.TypeDef ?? throw new NullReferenceException(nameof(resolved.TypeDef))
                        );

                    default:
                        throw new NotSupportedException($"{iface.Kind} for interface {Namespace}.{Name}");
                }
            });
    }

    public IEnumerable<(MetadataReader reader, TypeDefinition typeDef)> GetInterfaceImplementations()
        => GetInterfaceImplementations(typeDef);

    /// <summary>
    /// Count the number of methods in this interface's inheritance chain, not including
    /// itself
    /// </summary>
    /// <returns></returns>
    private int GetVTableOffset()
    {
        (MetadataReader reader, TypeDefinition def)? current = BaseInterface;
        int offset = 0;

        while (current is not null)
        {
            offset += current.Value.def.GetMethods().Count;
            current = GetBaseTypeDef(current.Value.def);
        }

        return offset;
    }

    private Guid? GetClsid()
    {
        TypeDefinitionHandle? implClassHandle = mr.TypeDefinitions.FirstOrDefault((tdHandle) =>
        {
            if (tdHandle.IsNil)
                return false;

            TypeDefinition td = mr.GetTypeDefinition(tdHandle);
            return mr.StringComparer.Equals(td.Namespace, Namespace) && mr.StringComparer.Equals(td.Name, Name.TrimStart('I'));
        });

        if (implClassHandle.HasValue && !implClassHandle.Value.IsNil)
        {
            return GuidDecoder.MaybeDecodeGuid(mr, mr.GetTypeDefinition(implClassHandle.Value));
        }

        return null;
    }

    public override void ToAhk(StringBuilder sb)
    {
        HeadersToAhk(sb);
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        string baseName = BaseInterface.HasValue ? 
            BaseInterface.Value.reader.GetString(BaseInterface.Value.def.Name) : 
            "Win32ComInterface";
        sb.AppendLine($"class {Name} extends {baseName}{{");

        sb.AppendLine();
        AppendStaticCode(sb);

        sb.AppendLine();
        AppendVTableList(sb);

        foreach (AhkComProperty prop in Properties)
        {
            sb.AppendLine();
            prop.ToAhk(sb);
        }

        foreach (AhkComMethod method in Methods)
        {
            sb.AppendLine();
            method.ToAhk(sb);
        }
        
        extensions?.ForEach(ex => sb.AppendLine(GetExtensionCodeTokenized(ex)));

        sb.AppendLine("}");
    }

    private void AppendStaticCode(StringBuilder sb)
    {
        sb.AppendLine("    static sizeof => A_PtrSize");

        if (iid.HasValue)
        {
            sb.AppendLine("    /**");
            sb.AppendLine($"     * The interface identifier for {Name}");
            sb.AppendLine("     * @type {Guid}");
            sb.AppendLine("     */");
            sb.AppendLine($"    static IID => Guid(\"{{{iid.Value.ToString()}}}\")");
        }

        if (clsid.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine("    /**");
            sb.AppendLine($"     * The class identifier for {Name.TrimStart('I')}");
            sb.AppendLine("     * @type {Guid}");
            sb.AppendLine("     */");
            sb.AppendLine($"    static CLSID => Guid(\"{{{clsid.Value.ToString()}}}\")");
        }

        sb.AppendLine();
        sb.AppendLine("    /**");
        sb.AppendLine("     * The offset into the COM object's virtual function table at which this interface's methods begin.");
        sb.AppendLine("     * @type {Integer}");
        sb.AppendLine("     */");
        sb.AppendLine($"    static vTableOffset => {VTableOffset}");
    }

    private void AppendVTableList(StringBuilder sb)
    {
        sb.AppendLine("    /**");
        sb.AppendLine("     * @readonly used when implementing interfaces to order function pointers");
        sb.AppendLine("     * @type {Array<String>}");
        sb.AppendLine("     */");

        sb.Append("    static VTableNames => [");
        sb.Append(string.Join(", ", Methods.Select(m => $"\"{m.GetDeduplicatedName()}\"")));
        sb.AppendLine("]");
    }

    public override List<string> GetReferencedTypes()
    {
        var imports = base.GetReferencedTypes();

        // Check for methods with String parameters
        if (Methods.Any(m => m.HasStringParam))
        {
            imports.Add("Windows.Win32.Foundation.BSTR");
        }
        Methods.ForEach(m => imports.AddRange(m.GetReferencedTypes()));

        if (BaseInterface.HasValue)
        {
            imports.Add(GetFqn(BaseInterface.Value.reader, BaseInterface.Value.def));
        }

        return imports;
    }


    private protected void HeadersToAhk(StringBuilder sb)
    {
        string pathToBase = GetPathToBase();

        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine($"#Include {pathToBase}Win32ComInterface.ahk");
        sb.AppendLine($"#Include {pathToBase}Guid.ahk");

        AppendImports(sb);
    }
}