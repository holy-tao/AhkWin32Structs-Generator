
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// A Windows Runtime event. Really just holds metadata that classes need to generate the code. The meat of the
/// WinRT event handling system lives in WinRTEventHandler.ahk
/// </summary>
record class AhkWinRTEvent
{
    /// <summary>
    /// The interface on which this event's add_* and remove_* methoids are declared
    /// </summary>
    public readonly TypeDefinition DeclaringInterface;

    /// <summary>
    /// MetadataReader which can be used to read DeclaringInterface
    /// </summary>
    public readonly MetadataReader mr;

    /// <summary>
    /// Name of the event, without the add_* (so add_Dismissed becomes "Dismissed")
    /// </summary>
    public readonly string Name;

    /// <summary>
    /// Type information for the event handler interface
    /// </summary>
    public readonly FieldInfo HandlerType;

    /// <summary>
    /// Piid to use for the event handler interface, if any. If the event handler has no generic arguments,
    /// then this is null and you should use the handler's IID to avoid allocating unnecessary Guid structs
    /// </summary>
    public readonly Guid? Piid;

    public string DeclaringInterfaceName => mr.GetString(DeclaringInterface.Name);

    public string DeclaringInterfaceFqn => mr.GetFullyQualifiedName(DeclaringInterface);

    public AhkWinRTEvent(TypeDefinition DeclaringInterface, MetadataReader mr, string Name, FieldInfo HandlerType)
    {
        this.DeclaringInterface = DeclaringInterface;
        this.mr = mr;
        this.Name = Name;
        this.HandlerType = HandlerType;
        this.Piid = GetPiid();
    }

    public void ToAhk(StringBuilder sb) 
    {
        AppendDocumentation(sb);

        // Arg list is indented since these statements can get pretty long
        string handlerArgList = string.Join(",\r\n                    ", [
            HandlerType.TypeName, 
            Piid is null ? $"{HandlerType.TypeName}.IID" : $"Guid(\"{{{Piid}}}\")", 
            ..GetHandlerArgMarshallers()]
        );

        sb.AppendLine($"    On{Name} {{");
        sb.AppendLine($"        get {{");
        sb.AppendLine($"            if(!this.HasProp(\"__On{Name}\")){{");
        sb.AppendLine($"                this.__On{Name} := WinRTEventHandler(");
        sb.AppendLine($"                    {handlerArgList}");
        sb.AppendLine($"                )");
        sb.AppendLine($"                this.__On{Name}Token := this.add_{Name}(this.__On{Name}.iface)");
        sb.AppendLine($"            }}");
        sb.AppendLine($"            return this.__On{Name}");
        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
    }

    /// <summary>
    /// Writes out code to clean up the the event handler - intended for __Delete methods 
    /// </summary>
    /// <param name="sb"></param>
    public void AppendCleanupCode(StringBuilder sb)
    {
        sb.AppendLine($"        if(this.HasProp(\"__On{Name}Token\")) {{"); 
        sb.AppendLine($"            this.remove_{Name}(this.__On{Name}Token)");
        sb.AppendLine($"            this.__On{Name}.iface.Dispose()");
        sb.AppendLine($"        }}");
    }

    private void AppendDocumentation(StringBuilder sb)
    {
        ApiDetails? apiDetails = DocumentationUtils.GetApiDetails($"{DeclaringInterfaceFqn}.{Name}", null);
        if(apiDetails is not null)
        {
            sb.AppendLine("    /**");
            sb.AppendLine("     * " + AhkType.EscapeDocs(apiDetails?.Description, "    "));

            if (!string.IsNullOrWhiteSpace(apiDetails?.Remarks))
            {
                sb.AppendLine("     * @remarks");
                sb.AppendLine("     * " + AhkType.EscapeDocs(apiDetails.Remarks, "    "));
            }
            sb.AppendLine($"     * @type {{{HandlerType.AhkType}}}");
            sb.AppendLine("    */");
        }
    }

    public IEnumerable<string> GetHandlerArgMarshallers() => HandlerType.GenericArguments
        .Select(arg => arg.GetTypeAsGenericCallable());

    public List<string> GetReferencedTypes()
    {
        List<string> imports = [HandlerType.GetTypeDefFqn()];

        imports.AddRange(HandlerType.GenericArguments
            .Concat(HandlerType.GenericArguments.SelectMany(arg => arg.GenericArguments))
            .Where(arg => arg.Kind is SimpleFieldKind.Class or SimpleFieldKind.Struct or SimpleFieldKind.COM or SimpleFieldKind.NativeTypedef or SimpleFieldKind.Primitive)
            .Select(arg => arg.Kind switch
            {
                SimpleFieldKind.Primitive or SimpleFieldKind.Struct => "Windows.Foundation.IPropertyValue",
                _ => arg.GetTypeDefFqn()
            })
        );

        return imports;
    }

    private Guid? GetPiid()
    {
        if(HandlerType.GenericArguments.Length == 0)
        {
            return null;
        }

        string typeKey = HandlerType.GetFullTypeSignature();

        if (!PiidUtils.TryGetPiid(typeKey, out Guid? piid))
            throw new KeyNotFoundException(typeKey);

        Trace.TraceInformation($"Resolved generic instantiation {typeKey} to PIID {{{piid}}}");
        return piid;
    }
}