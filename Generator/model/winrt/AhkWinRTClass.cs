
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
        ApendAhkConstructor(sb);
        sb.AppendLine();
        foreach(AhkWinRTMethod method in InstanceMethods)
        {
            method.ToAhk(sb);
            sb.AppendLine();
        }
        sb.AppendLine($";@endregion Instance Methods");

        sb.AppendLine("}");
    }

    /// <summary>
    /// Creates the __New method. If the class supports no-argument constructors, creates one, 
    /// otherwise just takes a pointer and passes it to super.__New
    /// </summary>
    /// <param name="sb"></param>
    private void ApendAhkConstructor(StringBuilder sb)
    {
        // If we have an [Activatable] attr whose first fixed argument's type is UInt, there's a no-arg constructor
        // https://learn.microsoft.com/en-us/uwp/api/windows.foundation.metadata.activatableattribute?view=winrt-26100
        bool hasNoArgCtor = CustomAttributes
            .Where(c => c.Name is "ActivatableAttribute")
            .Any(c => c.Attr.FixedArguments.First().Type is "UInt32");

        // TODO doc comment

        sb.AppendLine($"    __New(ptr{(hasNoArgCtor ? " := 0" : string.Empty)}) {{");

        if (hasNoArgCtor)
        {
            sb.AppendLine($"        if(ptr == 0) {{");
            sb.AppendLine($"            activatableClassId := HSTRING.Create(\"{Namespace}.{Name}\")");
            sb.AppendLine($"            ptr := WinRT.RoActivateInstance(activatableClassId)");
            sb.AppendLine($"        }}");
            sb.AppendLine();
        }

        sb.AppendLine($"        super.__New(ptr)");
        sb.AppendLine($"    }}");
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