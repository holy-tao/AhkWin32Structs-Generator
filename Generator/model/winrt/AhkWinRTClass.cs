
using System.Reflection.Metadata;
using System.Text;

/// <summary>
/// WinRT is "COM with extra steps" - in practice, that means that a WinRT class is actually an IInspectable interface
/// with a bunch of metadata about the other interfaces that you can query for its instance methods.
/// 
/// See https://devblogs.microsoft.com/oldnewthing/20210524-00/?p=105240 for more on statics and non-default 
/// constructors
/// </summary>
class AhkWinRTClass : AhkType
{
    public readonly List<AhkWinRTMethod> InstanceMethods;

    public readonly List<AhkComProperty> InstanceProperties;

    public readonly List<AhkComInterface> StaticInterfaces;

    public readonly List<AhkWinRTMethod> StaticMethods;

    public AhkWinRTClass(MetadataReader mr, TypeDefinition typeDef) : base(mr, typeDef)
    {
        InstanceMethods = CollectInstanceMethods();
        InstanceProperties = CollectInstanceProperties();

        StaticInterfaces = CollectStaticInterfaces();
        StaticMethods = CollectStaticMethods();
    }

    public override List<string> GetReferencedTypes()
    {
        List<string> imports = base.GetReferencedTypes();
        imports.AddRange([
            "Windows.Win32.System.WinRT.IInspectable",  // All WinRT classes extend IInspectable
            "Windows.Win32.System.WinRT.Apis",          // Need for e.g. RoActivateInstance
            "Windows.Win32.System.WinRT.HSTRING"        // TODO most types need this, but not all
        ]);

        imports.AddRange(InstanceMethods.Select(m => $"{m.DeclaringInterfaceNamespace}.{m.DeclaringInterfaceName}"));
        imports.AddRange(StaticInterfaces.Select(iface => $"{iface.Namespace}.{iface.Name}"));

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

        if(StaticMethods.Count > 0)
        {
            sb.AppendLine($";@region Static Methods");
            foreach(AhkWinRTMethod method in StaticMethods)
            {
                method.ToAhk(sb);
                sb.AppendLine();
            }
            sb.AppendLine($";@endregion Static Methods");
            sb.AppendLine();
        }

        if(InstanceProperties.Count > 0)
        {
            sb.AppendLine($";@region Instance Properties");
            foreach(AhkComProperty prop in InstanceProperties)
            {
                prop.ToAhk(sb);
                sb.AppendLine();
            }
            sb.AppendLine($";@endregion Instance Properties");
            sb.AppendLine();
        }

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
        // If we have an [Activatable] attr whose first fixed argument's type is UInt, there's a no-arg constructor,
        // meaning we can RoActivateInstance the fqn directly. See the table here:
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

        // The WinRT metadata unfortunately contains .NET specific constructs like System.IEnumerable
        // which we need to filter out. They made this for CSWin32, the rest of us have to suffer
        var winRTInterfaces = GetInterfaceImplementations()
            .Where(i => i.reader.GetString(i.iface.Namespace).StartsWith("Windows"));

        foreach((MetadataReader reader, TypeDefinition iface) in winRTInterfaces)
        {
            var methodDefs = iface.GetMethods().Select(reader.GetMethodDefinition);
            methods.AddRange(methodDefs.Select(def => new AhkWinRTMethod(this, reader, def, false)));
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
    /// Find all static interfaces, including factory interfaces, that apply to this class
    /// </summary>
    /// <returns></returns>
    private List<AhkComInterface> CollectStaticInterfaces()
    {
        // Collect [Static] attributes and Static constructors - [Activatable] attributes where first argument is a 
        // System.Type. See the table here:
        // https://learn.microsoft.com/en-us/uwp/api/windows.foundation.metadata.activatableattribute?view=winrt-26100
        return CustomAttributes
            .Where(c => { 
                return c.Name is "StaticAttribute" || 
                    (c.Name is "ActivatableAttribute" && c.Attr.FixedArguments.First().Type is "System.Type");
            })
            .Select(c =>
            {
                // TODO rework or make a new CustomAttributeDecoder that decodes the TypeDefinition
                string fqn = (string)(c.Attr.FixedArguments.First().Value ?? throw new NullReferenceException());
                string ns = string.Join('.', fqn.Split('.')[..^1]);
                string name = fqn.Split('.').Last().Split('`').First();

                var tdHandle = FieldSignatureDecoder.FindTypeDefinition(mr, ns, name, out var reader);
                if (tdHandle.IsNil)
                    throw new NullReferenceException($"Nil TypeDefinitionHandle for {c.Name} -> {fqn}");
                
                return new AhkComInterface(reader, reader.GetTypeDefinition(tdHandle));
            })
            .ToList();
    }

    private List<AhkWinRTMethod> CollectStaticMethods() 
    {
        return StaticInterfaces.SelectMany(iface => 
                iface.Methods.Select(m => new AhkWinRTMethod(this, m.mr, m.methodDef, true))
            )
            .ToList();
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
}