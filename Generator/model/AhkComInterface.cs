
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkComInterface : AhkType
{
    // Interface ID for this interface
    public readonly Guid iid;

    // CLSID for an instantiatable object the implements this interface, if any
    public readonly Guid? clsid;

    public AhkComInterface(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        iid = GuidDecoder.DecodeGuid(mr, typeDef);
        clsid = GetClsid();
        
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
            return GuidDecoder.DecodeGuid(mr, mr.GetTypeDefinition(implClassHandle.Value));
        }

        return null;
    }

    public override void ToAhk(StringBuilder sb)
    {
        HeadersToAhk(sb);
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends Win32ComInterface{{");
        sb.AppendLine( "    /**");
        sb.AppendLine($"     * The interface identifier for {Name}");
        sb.AppendLine( "     * @type {Guid}");
        sb.AppendLine( "     */");
        sb.AppendLine($"    static IID => Guid(\"{iid.ToString()}\")");

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
        sb.AppendLine($"#Include {pathToBase}Win32ComInterface.ahk");
        sb.AppendLine($"#Include {pathToBase}Guid.ahk");
    }
}