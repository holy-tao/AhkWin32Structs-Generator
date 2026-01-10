
using System.Collections;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

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
    public readonly List<AhkComProperty> StaticProperties;

    public readonly List<FieldInfo> ImplementedInterfaces;

    // public readonly ImmutableArray<FieldInfo> GenericArguments;

    private readonly string baseTypeNamespace;
    private readonly string baseTypeName;

    public string Fqn => $"{Namespace}.{Name}";

    public AhkWinRTClass(MetadataReader mr, TypeDefinition typeDef, string baseNamespace, string baseName) : base(mr, typeDef)
    {
        //  The WinRT metadata unfortunately contains .NET specific constructs like System.IEnumerable
        // which we need to filter out. They made this for CSWin32, the rest of us have to suffer
        ImplementedInterfaces = GetInterfaceImplementations()
            .Where(i => i.GetTypeDefNamespace().StartsWith("Windows"))
            .ToList();

        InstanceMethods = CollectInstanceMethods();
        InstanceProperties = CollectInstanceProperties();

        StaticInterfaces = CollectStaticInterfaces();
        StaticMethods = CollectStaticMethods();
        StaticProperties = CollectStaticProperties();

        baseTypeNamespace = baseNamespace;
        baseTypeName = baseName;
    }

    public override List<string> GetReferencedTypes()
    {
        List<string> imports = base.GetReferencedTypes();
        imports.AddRange([
            "Windows.Win32.System.WinRT.Apis",          // Need for e.g. RoActivateInstance
            "Windows.Win32.System.WinRT.HSTRING"        // TODO most types need this, but not all
        ]);

        imports.Add(baseTypeName is "Object" ?
            "Windows.Win32.System.WinRT.IInspectable" :     // Object means IInspectable
            $"{baseTypeNamespace}.{baseTypeName}"
        );

        if(ImplementedInterfaces.Any(iface => iface.GenericArguments.Any()))
        {
            imports.Add("Windows.Foundation.IPropertyValue");   // Required for boxing / unboxing generics
            imports.AddRange(ImplementedInterfaces
                .SelectMany(iface => iface.GenericArguments)
                .Where(info => info.Kind is not (SimpleFieldKind.OpenGeneric or SimpleFieldKind.Primitive or SimpleFieldKind.String))
                .Select(info => info.GetTypeDefFqn()));
        }

        imports.AddRange(InstanceMethods.Select(m => $"{m.DeclaringInterfaceNamespace}.{m.DeclaringInterfaceName}"));
        imports.AddRange(StaticInterfaces.Select(iface => $"{iface.Namespace}.{iface.Name}"));
        if(extensions?.Count > 0)
            imports.AddRange(extensions.SelectMany(ex => ex.Requirements));

        return imports;
    }

    public override void ToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0 64-bit");
        sb.AppendLine();
        AppendImports(sb);
        sb.AppendLine($"#Include {GetPathToBase()}Guid.ahk");
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends {(baseTypeName is "Object" ? "IInspectable" : baseTypeName)} {{");

        if(StaticProperties.Count > 0)
        {
            sb.AppendLine($";@region Static Properties");
            foreach(AhkComProperty prop in StaticProperties)
            {
                prop.ToAhk(sb);
                sb.AppendLine();
            }
            sb.AppendLine($";@endregion Static Properties");
            sb.AppendLine();
        }

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

        if(extensions?.Count > 0)
        {
            sb.AppendLine($";@region Extensions");
            extensions.ForEach(ex => sb.AppendLine(GetExtensionCodeTokenized(ex)));
            sb.AppendLine($";@endregion Extensions");
        }

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

        // constructor documentation is under #ctor if it exists
        if(hasNoArgCtor)
        {
            ApiDetails? ctorDocs = DocumentationUtils.GetApiDetails($"{Fqn}.#ctor", null);
            if(ctorDocs is not null)
            {
                sb.AppendLine("    /**");
                sb.AppendLine("     * " + EscapeDocs(ctorDocs?.Description, "    "));

                if (!string.IsNullOrWhiteSpace(ctorDocs?.Remarks))
                {
                    sb.AppendLine("     * @remarks");
                    sb.AppendLine("     * " + EscapeDocs(ctorDocs.Remarks, "    "));
                }
                sb.AppendLine("    */");
            }   
        }

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

        foreach(FieldInfo iface in ImplementedInterfaces)
        {
            var methodDefs = (iface.TypeDef ?? throw new NullReferenceException(nameof(iface.TypeDef)))
                .GetMethods()
                .Select(iface.Reader!.GetMethodDefinition);
            
            methods.AddRange(methodDefs.Select(def => 
                new AhkWinRTMethod(
                    this, 
                    iface.Reader, 
                    def, 
                    iface.TypeDef.Value, 
                    false, 
                    false,
                    iface.GenericArguments)));
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

        foreach(AhkWinRTMethod method in InstanceMethods.Where(m => m.IsSpecialName && (m.Name.StartsWith("get_") || m.Name.StartsWith("put_"))))
        {
            string normalizedName = method.Name[4..]; // Remove "get_" or "put_"
            if(properties.Any(p => p.Name == normalizedName))
                continue;

            AhkWinRTMethod? getter = InstanceMethods.FirstOrDefault(m => m!.IsSpecialName && m.Name == "get_" + normalizedName, null);
            AhkWinRTMethod? setter = InstanceMethods.FirstOrDefault(m => m!.IsSpecialName && m.Name == "put_" + normalizedName, null);
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
        var activatableInterfaceNames = CustomAttributes
            .Where(c => c.Name is "ActivatableAttribute" && c.Attr.FixedArguments.First().Type is "System.Type")
            .Select(c => (string)(c.Attr.FixedArguments.First().Value ?? throw new NullReferenceException()));

        return StaticInterfaces.SelectMany(iface => 
                iface.Methods.Select(m =>
                {
                    string ifaceFqn = $"{iface.Namespace}.{iface.Name}";
                    bool isConstructor = activatableInterfaceNames.Contains(ifaceFqn);
                    return new AhkWinRTMethod(this, m.mr, m.methodDef, iface.typeDef, true, isConstructor, []);
                })
            )
            .ToList();
    }

    private List<AhkComProperty> CollectStaticProperties()
    {
        List<AhkComProperty> properties = [];

        foreach(AhkWinRTMethod method in StaticMethods.Where(m => m.IsSpecialName && (m.Name.StartsWith("get_") || m.Name.StartsWith("put_"))))
        {
            string normalizedName = method.Name[4..]; // Remove "get_" or "put_"
            if(properties.Any(p => p.Name == normalizedName))
                continue;

            AhkWinRTMethod? getter = StaticMethods.FirstOrDefault(m => m!.IsSpecialName && m.Name == "get_" + normalizedName, null);
            AhkWinRTMethod? setter = StaticMethods.FirstOrDefault(m => m!.IsSpecialName && m.Name == "put_" + normalizedName, null);
            properties.Add(new AhkComProperty(this, normalizedName, getter, setter, true));
        }

        return properties;
    }

    /// <summary>
    /// Collects all directly implemented interfaces for this interface and resolves any TypeReferences.
    /// </summary>
    /// <returns>All directly implemented interfaces for this interface</returns>
    private IEnumerable<FieldInfo> GetInterfaceImplementations()
    {
        return typeDef.GetInterfaceImplementations()
            .Select(ih => mr.GetInterfaceImplementation(ih).Interface)
            .Where(iface => !iface.IsNil)
            .Select(iface =>
            {
                switch(iface.Kind) 
                {
                    case HandleKind.TypeReference:
                        var resolved = FieldSignatureDecoder.ResolveTypeReference(mr, (TypeReferenceHandle)iface);
                        return FieldSignatureDecoder.DecodeTypeDef(resolved.reader, resolved.typeDef);

                    case HandleKind.TypeDefinition:
                        return FieldSignatureDecoder.DecodeTypeDef(mr, (TypeDefinitionHandle)iface);

                    case HandleKind.TypeSpecification:
                        TypeSpecification typeSpec = mr.GetTypeSpecification((TypeSpecificationHandle)iface);
                        return typeSpec.DecodeSignature(new FieldSignatureProvider(mr), new());

                    default:
                        throw new NotSupportedException($"{iface.Kind} for interface {Namespace}.{Name}");
                }
            });
    }
}