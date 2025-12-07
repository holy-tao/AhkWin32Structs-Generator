
using System.Reflection.Metadata;
using System.Text;

/// <summary>
/// WinRT is "COM with extra steps" - in practice, that means that a WinRT class is actually an IInspectable interface
/// with a bunch of metadata about the other interfaces that you can query for its instance methods.
/// </summary>
class AhkWinRTClass : AhkType
{
    public readonly List<AhkWinRTMethod> InstanceMethods;

    public readonly List<AhkComProperty> InstanceProperties;

    public AhkWinRTClass(MetadataReader mr, TypeDefinition typeDef) : base(mr, typeDef)
    {
        InstanceMethods = CollectInstanceMethods();
        InstanceProperties = CollectInstanceProperties();
    }

    public override List<string> GetReferencedTypes()
    {
        List<string> imports = base.GetReferencedTypes();
        imports.AddRange([
            "Windows.Win32.System.WinRT.IInspectable",  // All WinRT classes extend IInspectable
            "Windows.Win32.System.WinRT.Apis"           // Need for e.g. RoActivateInstance
        ]);

        imports.AddRange(InstanceMethods.Select(m => $"{m.DeclaringInterfaceNamespace}.{m.DeclaringInterfaceName}"));

        return imports;
    }

    public override void ToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0 64-bit");
        sb.AppendLine();
        AppendImports(sb);
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends IInspectable {{");

        sb.AppendLine($";@region Instance Properties");
        foreach(AhkComProperty prop in InstanceProperties)
        {
            prop.ToAhk(sb);
            sb.AppendLine();
        }
        sb.AppendLine($";@endregion Instance Properties");
        sb.AppendLine();

        sb.AppendLine($";@region Instance Methods");
        foreach(AhkWinRTMethod method in InstanceMethods)
        {
            method.ToAhk(sb);
            sb.AppendLine();
        }
        sb.AppendLine($";@endregion Instance Methods");

        sb.AppendLine("}");
    }

    /// <summary>
    /// Collects all of the methods of all of this type's implemented interfaces
    /// </summary>
    /// <returns></returns>
    private List<AhkWinRTMethod> CollectInstanceMethods()
    {
        List<AhkWinRTMethod> methods = [];

        foreach((MetadataReader reader, TypeDefinition iface) in GetInterfaceImplementations())
        {
            var methodDefs = iface.GetMethods().Select(reader.GetMethodDefinition);
            methods.AddRange(methodDefs.Select(def => new AhkWinRTMethod(this, reader, def)));
        }

        return methods;
    }

    /// <summary>
    /// Collect all instance properties of the class - these are methods with the [SpecialName] attribute
    /// </summary>
    /// <returns></returns>
    private List<AhkComProperty> CollectInstanceProperties()
    {
        List<AhkComProperty> properties = [];

        foreach(AhkWinRTMethod method in InstanceMethods.Where(m => m.IsSpecialName))
        {
            string normalizedName = method.GetDeduplicatedName()[4..]; // Remove "get_" or "put_"
            if(properties.Any(p => p.Name == normalizedName))
                continue;

            AhkWinRTMethod? getter = InstanceMethods.FirstOrDefault(m => m!.IsSpecialName && m.GetDeduplicatedName() == "get_" + normalizedName, null);
            AhkWinRTMethod? setter = InstanceMethods.FirstOrDefault(m => m!.IsSpecialName && m.GetDeduplicatedName() == "put_" + normalizedName, null);
            properties.Add(new AhkComProperty(this, normalizedName, getter, setter));
        }

        return properties;
    }

    /// <summary>
    /// Collects all directly implemented interfaces for this interface and resolves any TypeReferences.
    /// </summary>
    /// <returns>All directly implemented interfaces for this interface</returns>
    private IEnumerable<(MetadataReader reader, TypeDefinition iface)> GetInterfaceImplementations()
    {
        return typeDef.GetInterfaceImplementations()
            .Select(ih => mr.GetInterfaceImplementation(ih).Interface)
            .Select(iface =>
            {
                switch(iface.Kind) 
                {
                    case HandleKind.TypeReference:
                        TypeDefinitionHandle hTypeDef = FieldSignatureDecoder.ResolveTypeReference(
                            mr, (TypeReferenceHandle)iface, out MetadataReader resolvedReader);
                        TypeDefinition resolvedTypeDef = resolvedReader.GetTypeDefinition(hTypeDef);
                        return (resolvedReader, resolvedTypeDef);

                    case HandleKind.TypeDefinition:
                        return (mr, mr.GetTypeDefinition((TypeDefinitionHandle)iface));

                    default:
                        throw new NotSupportedException($"{iface.Kind} for interface {Namespace}.{Name}");
                }
            });
    }
}