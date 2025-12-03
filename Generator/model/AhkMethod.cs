
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;
using System.Reflection;
using System.Dynamic;

public class AhkMethod
{
    public string Name => mr.GetString(methodDef.Name);

    public string Namespace => mr.GetString(mr.GetTypeDefinition(methodDef.GetDeclaringType()).Namespace);

    public string DeclarerName => Namespace.Split(".").Last();

    private protected readonly MetadataReader mr;
    private protected readonly MethodDefinition methodDef;
    private protected readonly ApiDetails? apiDetails;

    private protected readonly MethodImport import;

    public MethodImportAttributes CallingConvention => import.Attributes & MethodImportAttributes.CallingConventionMask;

    public MethodImportAttributes CharSet => import.Attributes & MethodImportAttributes.CharSetMask;

    public bool SetsLastError => import.Attributes.HasFlag(MethodImportAttributes.SetLastError);

    public string DLLName => import.Module.IsNil? "" : mr.GetString(mr.GetModuleReference(import.Module).Name);

    // The entry point for the DLL, that is, the actual value that gets looked up in the symbol table
    // This will almost always be identical to Name, but isn't required to be
    public string EntryPoint => import.Name.IsNil? "" : mr.GetString(import.Name);

    public bool PreserveSig => CustomAttributes.Any(c => c.Name is "PreserveSigAttribute");

    public bool CanReturnErrorsAsSuccess => CustomAttributes.Any(c => c.Name is "CanReturnErrorsAsSuccessAttribute");

    /// <summary>
    /// Does the function have a non-void return value? Note that it may not, but we could
    /// still have an output parameter
    /// </summary>
    public bool FuncHasReturnValue => !(parameters[0].FieldInfo.Kind == SimpleFieldKind.Primitive && parameters[0].FieldInfo.TypeName == "Void");

    public readonly List<AhkParameter> parameters = [];

    /// <summary>
    /// The logical return value of the function, if any (e.g. the [RetVal] param for Com methods,
    /// or an [out] param if we're confident that we don't want the user to allocate it)
    /// </summary>
    public readonly AhkParameter? outputParameter;

    private readonly List<CAInfo> CustomAttributes;

    public AhkMethod(MetadataReader mr, MethodDefinition methodDef)
    {
        this.mr = mr;
        this.methodDef = methodDef;
        CustomAttributes = CustomAttributeDecoder.DecodeAll(mr, methodDef);

        apiDetails = DocumentationUtils.GetApiDetails(mr, methodDef);

        import = methodDef.GetImport();
        parameters = ParameterDecoder.DecodeParameters(mr, methodDef);
        outputParameter = GetOutputParameter();
    }

    /// <summary>
    /// Get an AhkMethod by name
    /// </summary>
    /// <param name="reader">Metadata reader for the assembly to search in</param>
    /// <param name="name">Name of the method to return</param>
    /// <returns></returns>
    public static AhkMethod Get(MetadataReader reader, string name)
    {
        IEnumerable<TypeDefinition> apiTypeDefs = reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .Where(h => reader.StringComparer.Equals(h.Name, "Apis"));

        MethodDefinition def = apiTypeDefs
            .Single(td => td.GetMethods()
                .Select(reader.GetMethodDefinition)
                .Where(method => reader.StringComparer.Equals(method.Name, name))
                .Take(2).Count() == 1
            )
            .GetMethods()
            .Select(reader.GetMethodDefinition)
            .Single(methodDef => reader.StringComparer.Equals(methodDef.Name, name));

        return new AhkMethod(reader, def);
    }

    public virtual void ToAhk(StringBuilder sb)
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

        StringBuilder paramConversions = GetParameterConversions();
        sb.Append(paramConversions);
        if (paramConversions.Length > 0)
            sb.AppendLine();

        StringBuilder marshalCode = GetParameterMarshallingCode();
        sb.Append(marshalCode);
        if (marshalCode.Length > 0)
            sb.AppendLine();

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

        AppendOutputParamMarshallingCode(sb);
        sb.AppendLine($"        {BuildDllCallCall(epIsOrd? "procAddr" : $"\"{DLLName}\\{EntryPoint}\"")}");

        if (epIsOrd)
        {
            sb.AppendLine();
            sb.AppendLine("        Foundation.FreeLibrary(hModule)");
            sb.AppendLine();
        }

        AppendErrorCheck(sb);
        AppendReturnStatement(sb);

        sb.AppendLine($"    }}");
    }

    private protected virtual void AppendErrorCheck(StringBuilder sb)
    {
        // AHK code which will be ORed together
        List<string> conditions = [];

        if (SetsLastError)
        {
            conditions.Add(parameters[0].FieldInfo.TypeName == "BOOL"? "(!result && A_LastError)" : "A_LastError");
        }

        if(ShouldThrowForReturnValue()) 
        {
            conditions.Add("result != 0");
        }

        if(conditions.Count == 0)
        {
            return; // No error checking
        }

        sb.AppendLine($"        if({string.Join(" || ", conditions)}) {{");
                
        // Free any [FreeWith] output parameters before throwing
        foreach(AhkParameter param in parameters.Where(p => p.HasFreeWith))
        {
            AhkMethod freeWith = param.FreeWith ?? throw new NullReferenceException(nameof(param.FreeWith));
            sb.AppendLine($"            {freeWith.DeclarerName}.{freeWith.Name}({param.Name})");
        }

        sb.AppendLine($"            throw OSError({(FuncHasReturnValue? "A_LastError || result" : "A_LastError")})");
        sb.AppendLine($"        }}");
        sb.AppendLine();
    }

    private protected void AppendOutputParamMarshallingCode(StringBuilder sb)
    {
        if (!outputParameter.HasValue)
        {
            return;
        }

        AhkParameter outParam = outputParameter.Value;
        if (outParam.CustomAttributes.HasFlag(CustomParamAttributes.SizedBuffer))
        {
            // We need to create a buffer with some size
            Parameter param = methodDef.GetParameters()
                .Select(mr.GetParameter)
                .Single(p => mr.StringComparer.Equals(p.Name, outParam.Name));
            CAInfo memSize = CustomAttributeDecoder.DecodeAll(mr, param)
                .Single(p => p.Name == "MemorySizeAttribute");
            short bytesParamIndex = (short)(memSize.Attr.NamedArguments.Single(arg => arg.Name == "BytesParamIndex").Value ?? throw new NullReferenceException());

            // bytesParamIndex + 1 because we include the return value as a param
            sb.AppendLine($"        {outParam.Name} := Buffer({parameters[bytesParamIndex + 1].Name}, 0)");
        }
        else if (outParam.IsPtrToStruct || outParam.IsPtrToHandle())
        {
            sb.AppendLine($"        {outParam.Name} := {outParam.FieldInfo.UnderlyingType?.TypeName}()");
        }
    }

    private protected void AppendReturnStatement(StringBuilder sb)
    {
        if (!FuncHasReturnValue && outputParameter is null)
        {
            return;
        }

        AhkParameter fnRetVal = outputParameter ?? parameters[0];

        // We need to wrap handles and decide ownership & validity
        if (fnRetVal.IsHandle())
        {
            TypeDefinition returnValueType = fnRetVal.FieldInfo.TypeDef ?? throw new NullReferenceException();

            if (fnRetVal.HasIgnoreIfReturn)
            {
                var conditions = fnRetVal.IgnoreIfReturnValues.Select(v => $"{fnRetVal.Name} == {v}");
                string orStatement = string.Join(" || ", conditions);

                sb.AppendLine($"        if({orStatement})");
                sb.AppendLine($"            return {fnRetVal.Name}.Invalid()");
                sb.AppendLine();
            }

            MetadataReader retValReader = fnRetVal.FieldInfo.Reader ?? throw new NullReferenceException(nameof(FieldInfo.Reader));
            var handleField = retValReader.GetFieldDefinition(returnValueType.GetFields().First());
            string fieldName = retValReader.GetString(handleField.Name);
            sb.AppendLine($"        resultHandle := {fnRetVal.GetTypeDefName()}({{{fieldName}: {fnRetVal.Name}}}, {fnRetVal.ScriptOwned})");
            if (fnRetVal.RAIIFree is not null)
            {
                // Destructor for RAIIFree is in this namespace - not necessarily true for FreeWith
                sb.AppendLine($"        resultHandle.DefineProp(\"Free\", {{ Call: (self) => {fnRetVal.RAIIFree.DeclarerName}.{fnRetVal.RAIIFree.Name}(self.{fieldName}) }})");
            }

            sb.AppendLine("        return resultHandle");
        }
        else if (fnRetVal.IsPtrToCom)
        {
            sb.AppendLine($"        return {fnRetVal.FieldInfo.UnderlyingType?.TypeName}({fnRetVal.Name})");
        }
        else
        {
            sb.AppendLine($"        return {fnRetVal.Name}");
        }
    }

    private protected virtual StringBuilder GetParameterConversions()
    {
        StringBuilder conversions = new();

        foreach (AhkParameter param in parameters[1..].Where(p => !p.Reserved && p != outputParameter))
        {
            string? typeName = param.GetTypeDefName();

            if (typeName is "PSTR" or "PWSTR")
            {
                conversions.AppendLine($"        {param.Name} := {param.Name} is String ? StrPtr({param.Name}) : {param.Name}");
            }
            else if (param.IsHandle())
            {
                conversions.AppendLine($"        {param.Name} := {param.Name} is Win32Handle ? NumGet({param.Name}, \"ptr\") : {param.Name}");
            }
        }

        return conversions;
    }

    private protected virtual StringBuilder GetParameterMarshallingCode()
    {
        StringBuilder code = new();

        foreach (AhkParameter param in parameters[1..].Where(p => !p.Reserved && p != outputParameter))
        {
            // Allow pointers to primitives to be either VarRefs or raw pointers. If we only use asterisk marshalling, it's
            // impossible to ever pass null to a method, and users may want to pass pointers to e.g. buffers
            //      variable name is {param.Name}Marshal
            if (param.IsPtrToPrimitive)
            {
                string dllCallType = param.FieldInfo.GetDllCallType(false);
                code.AppendLine($"        {param.Name}Marshal := {param.Name} is VarRef ? \"{dllCallType}\" : \"ptr\"");
            }
        }

        return code;
    }

    private protected virtual AhkParameter? GetOutputParameter()
    {
        // If we have PreserveSig OR the function doesn't return an HRESULT, don't collapse
        // [out] parameters
        if(PreserveSig || CanReturnErrorsAsSuccess || !parameters[0].IsHRESULT)
        {
            return null;
        }

        AhkParameter outParam = default;
        IEnumerable<AhkParameter> candidateParams = parameters
            .Where(p => p.IsOutParam && !p.IsInParam)
            .Where(p => p.IsPtrToPrimitive || p.IsPtrToHandle() || p.IsPtrToCom);     // Only consider scalar, handle, and com output pointers
        if (candidateParams.Count() == 1)
        {
            outParam = candidateParams.Single();
        }

        return (outParam == default) ? null : outParam;
    }

    /// <summary>
    /// Get a list of the types referenced in the method - this is currently only the 
    /// LibraryLoader and Foundations APIs for ordinal methods, but in the future might
    /// be e.g. return values.
    /// </summary>
    /// <returns></returns>
    public List<string> GetReferencedTypes()
    {
        List<string> referencedTypes = [];

        // Methods with ordinal EntryPoints need APIs for Dll loading and unloadings
        if (EntryPoint.StartsWith('#'))
        {
            referencedTypes.AddRange([
                "Windows.Win32.Foundation.Apis",                // FreeLibrary is here for some reason
                "Windows.Win32.System.LibraryLoader.Apis"
            ]);
        }

        // If the return type is a handle, we need to import the handle
        if (FuncHasReturnValue && parameters[0].IsHandle())
        {
            referencedTypes.Add(AhkType.GetFqn(
                parameters[0].FieldInfo.Reader ?? throw new NullReferenceException(nameof(FieldInfo.Reader)),
                parameters[0].FieldInfo.TypeDef ?? throw new NullReferenceException(nameof(FieldInfo.TypeDef)))
            );
        }

        // If we have an output parameter, import its type if it's in the Win32Metadata
        if(outputParameter != null)
        {
            FieldInfo? underlying = outputParameter?.FieldInfo.UnderlyingType;
            bool mustImport = underlying?.Kind is SimpleFieldKind.Struct or SimpleFieldKind.COM;

            if(mustImport && (underlying?.Reader == null || underlying?.Reader == mr))
            {
                TypeDefinition? td = underlying?.TypeDef;
                if(td.HasValue)
                    referencedTypes.Add(AhkType.GetFqn(mr, td.Value));
            }
        }

        // If any parameters at all have [FreeWith] attributes, import the types they live in
        if(parameters.Any(p => p.FreeWith is not null))
        {
            var apiTypeDefs = mr.TypeDefinitions
                .Select(mr.GetTypeDefinition)
                .Where(h => mr.StringComparer.Equals(h.Name, "Apis"));

            foreach(AhkParameter param in parameters.Where(p => p.FreeWith is not null))
            {
                AhkMethod freeWith = param.FreeWith ?? throw new NullReferenceException(nameof(param.FreeWith));
                referencedTypes.Add($"{freeWith.Namespace}.{freeWith.DeclarerName}");
            }
        }

        return referencedTypes;
    }

    /// <summary>
    /// Builds the actual DllCall call, like [result := ] DllCall("dll\function", "ptr", ..)
    /// </summary>
    /// <returns></returns>
    private protected virtual string BuildDllCallCall(string entry)
    {
        StringBuilder sb = new();
        if (FuncHasReturnValue)
            sb.Append("result := ");

        sb.Append($"DllCall({entry}");

        if (parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArgumentList());
        }

        // Calling convention / return type
        if (CallingConvention == MethodImportAttributes.CallingConventionCDecl || FuncHasReturnValue)
        {
            sb.Append(", \"");
            if (CallingConvention == MethodImportAttributes.CallingConventionCDecl)
            {
                sb.Append("CDecl ");
            }

            if (FuncHasReturnValue)
                sb.Append(parameters[0].FieldInfo.GetDllCallType(true));

            sb.Append('"');
        }

        return sb.Append(')').ToString();
    }

    private protected string BuildMethodArgumentList()
    {
        return string.Join(", ", parameters
            .Slice(1, parameters.Count - 1)                 // Skip param 0, the return value
            .Where(p => !p.Reserved)   // Skip reserved params and explicit return values
            .Where(p => p != outputParameter)
            .Select(p => p.Name)
        );
    }

    private protected virtual string BuildDllCallArgumentList()
    {
        StringBuilder argList = new();

        // Skip param 0, which is return value
        for (int i = 1; i < parameters.Count; i++)
        {
            AhkParameter param = parameters[i];

            bool isString = param.GetTypeDefName() is "PWSTR" or "PSTR";
            string dllCallType = isString ? "ptr" : param.FieldInfo.GetDllCallType(false);

            string marshalAs = (param.IsPtrToPrimitive && !param.Reserved && param != outputParameter) ? $"{param.Name}Marshal" : $"\"{dllCallType}\"";
            string passAs = (param == outputParameter && (param.IsPtrToPrimitive || param.IsPtrToCom)) ? $"&{param.Name} := 0" : param.Name;

            argList.Append(marshalAs);
            argList.Append(", ");
            argList.Append(passAs);

            // TODO default value, optional values

            if (i < parameters.Count - 1)
                argList.Append(", ");
        }

        return argList.ToString();
    }

    private protected void MaybeAppendDocumentation(StringBuilder sb)
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

            if (param.Reserved || param == outputParameter)
                continue;

            sb.Append($"     * @param {{{param.FieldInfo.AhkType}}} {param.Name} ");
            if (apiDetails?.Parameters.TryGetValue(param.Name, out string? docString) ?? false)
            {
                sb.Append(AhkType.EscapeDocs(docString, "    "));
            }
            sb.AppendLine();
        }

        if (FuncHasReturnValue || outputParameter != null)
        {
            if (outputParameter != null)
            {
                AhkParameter param = outputParameter.Value;
                string? actualValueName = param.IsPtr ? param.FieldInfo.UnderlyingType?.AhkType : param.FieldInfo.AhkType;
                sb.Append($"     * @returns {{{actualValueName}}} ");
                if (apiDetails?.Parameters.TryGetValue(param.Name, out string? docString) ?? false)
                {
                    sb.Append(AhkType.EscapeDocs(docString, "    "));
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"     * @returns {{{parameters[0].FieldInfo.AhkType}}} {AhkType.EscapeDocs(apiDetails?.ReturnValue, "    ")}");
            }
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

        if (CustomAttributes.Any(c => c.Name is "ObsoleteAttribute"))
        {
            string message = DocumentationUtils.GetDeprecationMessage(mr, methodDef);
            sb.AppendLine($"     * @deprecated {message}");
        }

        CAInfo osPlatform = CustomAttributes.SingleOrDefault(c => c.Name is "SupportedOSPlatformAttribute");
        if (osPlatform != default)
        {
            sb.AppendLine($"     * @since {osPlatform.Attr.FixedArguments[0].Value ?? ""}");
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
    private protected bool ShouldThrowForReturnValue()
    {
        // If the method doesn't return an HRESULT, this is always no
        if (!parameters[0].IsHRESULT)
        {
            return false;
        }

        // If [PreserveSig] exists and is false, don't check HRESULTS
        CAInfo attr = CustomAttributes.SingleOrDefault(c => c.Name is "PreserveSigAttribute");
        if (attr != default)
        {
            return ((bool?)attr.Attr.FixedArguments[0].Value) ?? true;
        }

        // Otherwise, check for [CanReturnMultipleSuccessValues] or [CanReturnErrorsAsSuccess]
        return !CustomAttributes.Any(c => c.Name is "CanReturnMultipleSuccessValuesAttribute" or "CanReturnErrorsAsSuccessAttribute");
    }
}