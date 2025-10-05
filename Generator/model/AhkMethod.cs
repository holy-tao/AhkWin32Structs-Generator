
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

    // The entry point for the DLL, that is, the actual value that gets looked up in the symbol table
    // This will almost always be identical to Name, but isn't required to be
    public string EntryPoint => mr.GetString(import.Name);

    public bool HasReturnValue => !(parameters[0].FieldInfo.Kind == SimpleFieldKind.Primitive && parameters[0].FieldInfo.TypeName == "Void");

    private readonly List<AhkParameter> parameters = [];

    public AhkMethod(MetadataReader mr, MethodDefinition methodDef, Dictionary<string, ApiDetails> apiDocs)
    {
        this.mr = mr;
        this.methodDef = methodDef;
        apiDocs.TryGetValue(Name, out apiDetails);

        import = methodDef.GetImport();
        parameters = ParameterDecoder.DecodeParameters(mr, methodDef);
    }

    public void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    static {Name}({BuildMethodArgumentList()}) {{");

        // AutoHotkey doesn't support the thiscall calling convention, so we'll have these
        // always throw MethodErrors.
        if (CallingConvention == MethodImportAttributes.CallingConventionThisCall)
        {
            Console.WriteLine($"!!! Found thiscall method: {Name}");
            
            sb.AppendLine($"        throw MethodError(\"Not supported: AutoHotkey does not support the thiscall calling convention\", , A_ThisFunc)");
            sb.AppendLine("    }");
            return;
        }

        List<AhkParameter> reservedParams = [.. parameters.Where(p => p.Reserved)];
        if (reservedParams.Count > 0)
        {
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

        bool epIsOrd = EntryPoint.StartsWith('#');  //Is the EntryPoint and ordinal?

        if (SetsLastError)
        {
            sb.AppendLine($"        A_LastError := 0");
            sb.AppendLine();
        }

        // If the Entry Point is an ordinal, we need to manually load and unload the module and get the
        // proc address ourselves
        if (epIsOrd)
        {
            sb.AppendLine($"        ; This method's EntryPoint is an ordinal, so we need to load the dll manually");
            sb.AppendLine($"        hModule := LibraryLoader.LoadLibraryW(\"{DLLName}\")");
            sb.AppendLine($"        procAddr := LibraryLoader.GetProcAddress(hModule, {EntryPoint[1..]})");
            sb.AppendLine();
        }

        sb.AppendLine($"        {BuildDllCallCall(epIsOrd? "procAddr" : $"\"{DLLName}\\{EntryPoint}\"")}");

        if (epIsOrd)
        {
            sb.AppendLine();
            sb.AppendLine("        Foundation.FreeLibrary(hModule)");
            sb.AppendLine();
        }

        if (SetsLastError)
        {
            // Inspect last error for errors
            sb.AppendLine($"        if(A_LastError)");
            sb.AppendLine($"            throw OSError()");
            sb.AppendLine();
        }

        if (HasReturnValue && ShouldThrowForReturnValue())
        {
            // The function returns an HRESULT and we should check to see if we need to throw
            sb.AppendLine($"        if(result != 0)");
            sb.AppendLine($"            throw OSError(result)");
            sb.AppendLine();
        }

        if (HasReturnValue)
            sb.AppendLine("        return result");

        sb.AppendLine($"    }}");
    }

    /// <summary>
    /// Get a list of the types referenced in the method - this is currently only the 
    /// LibraryLoader and Foundations APIs for ordinal methods, but in the future might
    /// be e.g. return values.
    /// </summary>
    /// <returns></returns>
    public List<TypeDefinition> GetReferencedTypes()
    {
        List<TypeDefinition> referencedTypes = [];

        // Methods with ordinal EntryPoints need APIs for Dll loading and unloadings
        if (EntryPoint.StartsWith('#'))
        {
            List<string> dllLoadRequired = [
                "Windows.Win32.Foundation.Apis",                // FreeLibrary is here for some reason
                "Windows.Win32.System.LibraryLoader.Apis"
            ];

            referencedTypes.AddRange(mr.TypeDefinitions
                .Select(mr.GetTypeDefinition)
                .Where(td => dllLoadRequired.Contains(AhkStruct.GetFqn(mr, td)))
                .ToList());
        }

        return referencedTypes;
    }

    /// <summary>
    /// Builds the actual DllCall call, like [result := ] DllCall("dll\function", "ptr", ..)
    /// </summary>
    /// <returns></returns>
    private string BuildDllCallCall(string entry)
    {
        StringBuilder sb = new();
        if (HasReturnValue)
            sb.Append("result := ");

        // Entry point is an ordinal, which means we need to manually load the dll and 

        sb.Append($"DllCall({entry}");

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
            string dllCallType = isString ? "ptr" : param.FieldInfo.GetDllCallType(false);

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

    /// <summary>
    /// Does this method return an HRESULT and, if so, should we throw an error if it's anything
    /// other than 0 (S_OK)?
    /// 
    /// This is true by default, and false if [DllImport(..., PreserveSig = false)] is present OR EITHER
    ///     1.  [CanReturnMultipleSuccessValues] is present, OR
    ///     2.  [CanReturnErrorsAsSuccess] is present
    /// </summary>
    /// <returns></returns>
    private bool ShouldThrowForReturnValue()
    {
        // If the method doesn't return an HRESULT, this is always no
        if (parameters[0].FieldInfo.Kind != SimpleFieldKind.HRESULT)
        {
            return false;
        }

        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(mr, methodDef, "PreserveSigAttribute");
        if (attr.HasValue)
        {
            var attrVal = ((CustomAttribute)attr).DecodeValue(new CaTypeProvider());
            bool hasPreserveSig = ((bool?)attrVal.FixedArguments[0].Value) ?? true;

            // https://github.com/microsoft/win32metadata/issues/1315#issuecomment-1281559120
            if (!hasPreserveSig)
            {
                return false;
            }
            else
            {
                // Check for [CanReturnMultipleSuccessValues] or [CanReturnErrorsAsSuccess]
                foreach (string attrName in CustomAttributeDecoder.GetAllNames(mr, methodDef))
                {
                    if (attrName is "CanReturnMultipleSuccessValuesAttribute" or "CanReturnErrorsAsSuccessAttribute")
                    {
                        return false;
                    }
                }
            }
        }


        return true;
    }
}