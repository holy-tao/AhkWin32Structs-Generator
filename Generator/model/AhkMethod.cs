
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;
using System.Reflection;

class AhkMethod
{
    public string Name => mr.GetString(methodDef.Name);

    private readonly MetadataReader mr;
    private readonly MethodDefinition methodDef;
    private readonly ApiDetails? apiDetails;

    private readonly MethodImport import;

    public MethodImportAttributes CallingConvention => import.Attributes & MethodImportAttributes.CallingConventionMask;

    public MethodImportAttributes CharSet => import.Attributes & MethodImportAttributes.CharSetMask;

    public bool SetsLastError => import.Attributes.HasFlag(MethodImportAttributes.SetLastError);

    public string DLLName => mr.GetString(mr.GetModuleReference(import.Module).Name);

    public bool HasReturnValue => !(parameters[0].FieldInfo.Kind == SimpleFieldKind.Primitive && parameters[0].FieldInfo.TypeName == "Void");

    private readonly List<AhkParameter> parameters = [];

    public AhkMethod(MetadataReader mr, MethodDefinition methodDef, Dictionary<string, ApiDetails> apiDocs)
    {
        this.mr = mr;
        this.methodDef = methodDef;
        apiDocs.TryGetValue(Name, out apiDetails);

        import = methodDef.GetImport();
        parameters = ParameterDecoder.DecodeParameters(mr, methodDef);
        //Console.WriteLine("break");
    }

    public void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    static {Name}({BuildMethodArgumentList()}) {{");

        List<AhkParameter> reservedParams = [.. parameters.Where(p => p.Reserved)];
        if (reservedParams.Count > 0) {
            sb.Append("        static ");
            sb.Append(string.Join(", ", reservedParams.Select(p => $"{p.Name} := 0")));
            sb.Append(" ;Reserved parameters must always be NULL");

            sb.AppendLine();
            sb.AppendLine();
        }

#pragma warning disable CS8629 // Nullable value type may be null.
        List<AhkParameter> stringParams = [.. parameters[1..]
            .Where(p => p.FieldInfo.TypeDef.HasValue)
            .Where(p => mr.GetString(p.FieldInfo.TypeDef.Value.Name) is "PWSTR" or "PSTR")];
#pragma warning restore CS8629 // Nullable value type may be null.

        if (stringParams.Count > 0)
        {
            foreach (AhkParameter param in stringParams)
            {
                sb.AppendLine($"        {param.Name} := {param.Name} is String? StrPtr({param.Name}) : {param.Name}");
            }
            sb.AppendLine();
        }

        if (SetsLastError)
        {
            sb.AppendLine($"        A_LastError := 0");
            sb.AppendLine();
        }

        // TODO params, CDecl, output variables

        sb.AppendLine($"        {BuildDllCallCall()}");

        if (SetsLastError)
        {
            sb.AppendLine($"        if(A_LastError)");
            sb.AppendLine($"            throw OSError()");
            sb.AppendLine();
        }

        if (HasReturnValue)
            sb.AppendLine("        return result");
        
        sb.AppendLine($"    }}");
    }

    private string BuildDllCallCall()
    {
        StringBuilder sb = new();
        if (HasReturnValue)
            sb.Append("result := ");
        
        sb.Append($"DllCall(\"{DLLName}\\{Name}\"");

        if (parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArgumentList());
        }

        // Calling convention / return type
        if (CallingConvention == MethodImportAttributes.CallingConventionCDecl || parameters[0].FieldInfo.TypeName != "Void")
        {
            sb.Append(", \"");
            if (CallingConvention == MethodImportAttributes.CallingConventionCDecl)
            {
                sb.Append("CDecl ");
            }

            if (HasReturnValue)
                sb.Append(parameters[0].FieldInfo.GetDllCallType(false));

            sb.Append('"');
        }

        return sb.Append(')').ToString();
    }

    private string BuildMethodArgumentList()
    {
        return string.Join(", ", parameters
            .Slice(1, parameters.Count - 1)     // Skip param 0, the return value
            .Where(p => !p.Reserved)            // Skip reserved params
            .Select(p => p.Name)
        );
    }

    private string BuildDllCallArgumentList()
    {
        StringBuilder argList = new();

        // Skip param 0, which is return value
        for (int i = 1; i < parameters.Count; i++)
        {
            AhkParameter param = parameters[i];

            bool isString = param.FieldInfo.TypeDef.HasValue && mr.GetString(param.FieldInfo.TypeDef.Value.Name) is "PWSTR" or "PSTR";
            string dllCallType = isString? "ptr" : param.FieldInfo.GetDllCallType(false);

            argList.Append($"\"{dllCallType}\"");
            argList.Append(", ");
            argList.Append(param.Name);

            // TODO default value, optional values

            if (i < parameters.Count - 1)
                argList.Append(", ");
        }

        return argList.ToString();
    }

    private void MaybeAppendDocumentation(StringBuilder sb)
    {
        sb.AppendLine("    /**");
        sb.AppendLine("     * " + AhkType.EscapeDocs(apiDetails?.Description, "    "));

        if (!string.IsNullOrWhiteSpace(apiDetails?.Remarks))
        {
            sb.AppendLine("     * @remarks");
            sb.AppendLine("     * " + AhkType.EscapeDocs(apiDetails.Remarks, "    "));
        }

        for (int i = 1; i < parameters.Count; i++)
        {
            AhkParameter param = parameters[i];

            if (param.Reserved)
                continue;

            sb.Append($"     * @param {{{param.FieldInfo.AhkType}}} {param.Name} ");
            if (apiDetails?.Parameters.TryGetValue(param.Name, out string? docString) ?? false)
            {
                sb.Append(AhkType.EscapeDocs(docString, "    "));
            }
            sb.AppendLine();
        }

        if (HasReturnValue)
        {
            sb.AppendLine($"     * @returns {{{parameters[0].FieldInfo.AhkType}}} {AhkType.EscapeDocs(apiDetails?.ReturnValue, "    ")}");
        }
        else
        {
            // Explicitly say we return an empty string if no return type
            sb.AppendLine("     * @returns {String} Nothing - always returns an empty string");
        }
        

        if (apiDetails?.HelpLink != null)
        {
            sb.AppendLine($"     * @see {apiDetails.HelpLink}");
        }

        // One-offs
        if (CharSet == MethodImportAttributes.CharSetAnsi)
            sb.AppendLine($"     * @charset ANSI");

        if (CharSet == MethodImportAttributes.CharSetUnicode)
            sb.AppendLine($"     * @charset Unicode");

        if (CustomAttributeDecoder.GetAttribute(mr, methodDef, "ObsoleteAttribute") != null)
            sb.AppendLine($"     * @deprecated");

        CustomAttribute? osPlatform = CustomAttributeDecoder.GetAttribute(mr, methodDef, "SupportedOSPlatformAttribute");
        if (osPlatform != null)
        {
            var decoded = osPlatform.Value.DecodeValue(new CaTypeProvider());
            sb.AppendLine($"     * @since {decoded.FixedArguments[0].Value ?? ""}");
        }

        sb.AppendLine("     */");
    }
}