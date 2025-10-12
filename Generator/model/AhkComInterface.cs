
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

    public AhkComInterface(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        iid = GuidDecoder.MaybeDecodeGuid(mr, typeDef);
        clsid = GetClsid();

        List<TypeDefinition> impls = GetResolvedInterfaceImplementations();

        BaseInterface = impls.Count switch
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
    private List<TypeDefinition> GetResolvedInterfaceImplementations()
    {
        return typeDef.GetInterfaceImplementations()
            .Select(ih => mr.GetInterfaceImplementation(ih).Interface)
            .Select(iface => iface.Kind switch
                {
                    // Resolve type reference, asserting that it's not null, then resolve the handle
                    HandleKind.TypeReference => mr.GetTypeDefinition(FieldSignatureDecoder.ResolveTypeReference(mr, (TypeReferenceHandle)iface) ?? throw new NullReferenceException()),
                    HandleKind.TypeDefinition => mr.GetTypeDefinition((TypeDefinitionHandle)iface),
                    _ => throw new NotSupportedException($"{iface.Kind} for interface {Namespace}.{Name}")
                }
            )
            .ToList();
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

        if (iid.HasValue)
        {
            sb.AppendLine( "    /**");
            sb.AppendLine($"     * The interface identifier for {Name}");
            sb.AppendLine( "     * @type {Guid}");
            sb.AppendLine( "     */");
            sb.AppendLine($"    static IID => Guid(\"{iid.Value.ToString()}\")");
        }

        if (clsid.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine( "    /**");
            sb.AppendLine($"     * The class identifier for {Name.TrimStart('I')}");
            sb.AppendLine( "     * @type {Guid}");
            sb.AppendLine( "     */");
            sb.AppendLine($"    static CLSID => Guid(\"{clsid.Value.ToString()}\")");
        }

        sb.AppendLine("}");
    }

    private void HeadersToAhk(StringBuilder sb)
    {
        string pathToBase = Namespace.Split(".")
            .Select(val => $"..{Path.DirectorySeparatorChar}")
            .Aggregate((agg, cur) => agg + cur);

        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");

        if (BaseInterface.HasValue)
        {
            // Class should extend the base interface
            TypeDefinition resolved = (TypeDefinition)BaseInterface;
            string relPath = RelativePathBetweenNamespaces(Namespace, mr.GetString(resolved.Namespace));
            string directive = $"#Include {relPath}{mr.GetString(resolved.Name)}.ahk";

            sb.AppendLine(directive);
        }
        else
        {
            // This is a base interface - probably IUnknown - extend the base class
            sb.AppendLine($"#Include {pathToBase}Win32ComInterface.ahk");
        }

        sb.AppendLine($"#Include {pathToBase}Guid.ahk");
    }
}