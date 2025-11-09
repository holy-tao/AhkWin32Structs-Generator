
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkComInterface : AhkType
{
    // Interface ID for this interface
    public readonly Guid? iid;

    // CLSID for an instantiatable object the implements this interface, if any
    public readonly Guid? clsid;

    public readonly TypeDefinition? BaseInterface;

    public readonly List<AhkComMethod> Methods;

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
    }

    private TypeDefinition? GetBaseTypeDef(TypeDefinition forType)
    {
        List<TypeDefinition> impls = GetResolvedInterfaceImplementations(forType);

        return impls.Count switch
        {
            0 => null,
            1 => impls.First(),
            _ => throw new NotSupportedException($"Interface {Namespace}.{Name} implements {impls.Count} interfaces, expected 1: [{string.Join(",", impls.Select(td => mr.GetString(td.Name)))}]")
        };
    }

    /// <summary>
    /// Collects all directly implemented interfaces for this interface and resolves any TypeReferences.
    /// </summary>
    /// <returns>All directly implemented interfaces for this interface</returns>
    /// <exception cref="NullReferenceException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    private List<TypeDefinition> GetResolvedInterfaceImplementations(TypeDefinition forType)
    {
        return forType.GetInterfaceImplementations()
            .Select(ih => mr.GetInterfaceImplementation(ih).Interface)
            .Select(iface => iface.Kind switch
                {
                    // Resolve type reference, asserting that it's not null, then resolve the handle
                    HandleKind.TypeReference => mr.GetTypeDefinition(FieldSignatureDecoder.ResolveTypeReference(mr, (TypeReferenceHandle)iface, out _)),
                    HandleKind.TypeDefinition => mr.GetTypeDefinition((TypeDefinitionHandle)iface),
                    _ => throw new NotSupportedException($"{iface.Kind} for interface {Namespace}.{Name}")
                }
            )
            .ToList();
    }

    /// <summary>
    /// Count the number of methods in this interface's inheritance chain, not including
    /// itself
    /// </summary>
    /// <returns></returns>
    private int GetVTableOffset()
    {
        TypeDefinition? current = BaseInterface;
        int offset = 0;

        while (current.HasValue)
        {
            offset += current.Value.GetMethods().ToList().Count;
            current = GetBaseTypeDef(current.Value);
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
        sb.AppendLine($"class {Name} extends {(BaseInterface.HasValue ? mr.GetString(BaseInterface.Value.Name) : "Win32ComInterface")}{{");

        sb.AppendLine();
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

        sb.AppendLine();
        AppendVTableList(sb);

        foreach (AhkComMethod method in Methods)
        {
            sb.AppendLine();
            method.ToAhk(sb);
        }
        
        extensions?.ForEach(ex => sb.AppendLine(GetExtensionCodeTokenized(ex)));

        sb.AppendLine("}");
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
            imports.Add(GetFqn(mr, (TypeDefinition)BaseInterface));
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