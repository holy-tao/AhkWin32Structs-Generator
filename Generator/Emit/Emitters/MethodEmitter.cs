namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits v2.0 method bodies (DllCall) for DllImport methods.
/// Static helper class used by ApiTypeEmitter and ComInterfaceEmitter.
/// Version-independent helpers are <c>internal static</c> and reused by <see cref="MethodEmitter21"/>.
/// </summary>
public static class MethodEmitter
{
    /// <summary>
    /// Emit a complete DllImport method (documentation + signature + body).
    /// </summary>
    public static void EmitDllImportMethod(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        DocCommentWriter.WriteMethodDoc(w, method);

        string argList = BuildArgumentList(method);
        using (w.StaticMethod(method.Name, argList))
        {
            EmitDllImportMethodBody(w, method, registry);
        }
    }

    private static void EmitDllImportMethodBody(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        // AutoHotkey doesn't support the thiscall calling convention
        if (method.CallingConvention == CallingConvention.ThisCall)
        {
            w.Line(
                "throw MethodError(\"Not supported: AutoHotkey does not support the thiscall calling convention\", , A_ThisFunc)"
            );
            return;
        }

        EmitReservedParams(w, method);
        EmitParameterConversions(w, method, false);
        EmitParameterMarshalling(w, method);

        if (method.SetsLastError)
        {
            w.Line("A_LastError := 0");
            w.BlankLine();
        }

        if (method.IsOrdinal)
            EmitOrdinalLoading(w, method);

        EmitOutputParamMarshalling(w, method, registry);

        if (method.IsVariadic)
            EmitVariadicMarshalling(w, method);

        w.Line(BuildDllCallExpression(method));

        if (method.IsOrdinal)
        {
            w.BlankLine();
            w.Line("Foundation.FreeLibrary(hModule)");
            w.BlankLine();
        }

        EmitErrorCheck(w, method, registry);
        EmitReturnStatement(w, method, registry);
    }

    // --- Argument list ---

    /// <summary>
    /// Build the user-facing method argument list (skips reserved and output params).
    /// Appends <c>args*</c> for variadic methods.
    /// </summary>
    internal static string BuildArgumentList(MethodMember method)
    {
        var names = method
            .Parameters.Skip(1) // Skip param 0 (return value)
            .Where(p => !p.IsReserved && p != method.OutputParameter)
            .Select(p => p.Name)
            .ToList();

        if (method.IsVariadic)
            names.Add($"{method.VariadicParamName}*");

        return string.Join(", ", names);
    }

    // --- Reserved parameters ---

    internal static void EmitReservedParams(AhkWriter w, MethodMember method)
    {
        var reserved = method.Parameters.Skip(1).Where(p => p.IsReserved).ToList();
        if (reserved.Count == 0)
            return;

        w.Line(
            $"static {string.Join(", ", reserved.Select(p => $"{p.Name} := 0"))} ;Reserved parameters must always be NULL"
        );
        w.BlankLine();
    }

    // --- Parameter conversions (String->StrPtr, Handle->NumGet) ---

    private static void EmitParameterConversions(AhkWriter w, MethodMember method, bool isComMethod)
    {
        int startLen = w.Length;

        foreach (var param in method.InputParameters)
        {
            if (isComMethod && param.TypeDefName is "BSTR")
            {
                // Only COM methods get automatic String -> BSTR conversion - DllImport methods want to operate on the
                // handle itself, so we treat it as a regular handle to be dereferenced.
                w.Line($"{param.Name} := {param.Name} is String ? BSTR.Alloc({param.Name}).Value : {param.Name}");
            }
            else if (param.TypeDefName is "PSTR" or "PWSTR")
            {
                // DllImport methods allow AHK strings or string pointers
                w.Line($"{param.Name} := {param.Name} is String ? StrPtr({param.Name}) : {param.Name}");
            }
            else if (param.IsHandle)
            {
                // v2.0: manual handle dereference.
                w.Line($"{param.Name} := {param.Name} is Win32Handle ? NumGet({param.Name}, \"ptr\") : {param.Name}");
            }
        }

        if (w.Length > startLen)
            w.BlankLine();
    }

    // --- Parameter marshalling (VarRef detection for ptr-to-primitive) ---

    internal static void EmitParameterMarshalling(AhkWriter w, MethodMember method)
    {
        int startLen = w.Length;

        foreach (var param in method.InputParameters.Where(p => p.IsPtrToPrimitive))
        {
            string typedDllCallType = ((PointerType)param.Type).TypedDllCallType;
            w.Line($"{param.Name}Marshal := {param.Name} is VarRef ? \"{typedDllCallType}\" : \"ptr\"");
        }

        if (w.Length > startLen)
            w.BlankLine();
    }

    // --- Ordinal entry point loading ---

    private static void EmitOrdinalLoading(AhkWriter w, MethodMember method)
    {
        w.Line("; This method's EntryPoint is an ordinal, so we need to load the dll manually");
        w.Line($"hModule := LibraryLoader.LoadLibraryW(\"{method.DllName}\")");
        w.Line($"procAddr := LibraryLoader.GetProcAddress(hModule, {method.EntryPoint[1..]})");
        w.BlankLine();
    }

    // --- Output parameter marshalling ---

    private static void EmitOutputParamMarshalling(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        if (method.OutputParameter is not { } outParam)
            return;

        if (outParam.IsSizedBuffer)
        {
            // SizedBufferBytesParamIndex is 0-based from metadata; add 1 for Parameters[] (index 0 = return)
            string sizeParamName = method.Parameters[outParam.SizedBufferBytesParamIndex + 1].Name;
            w.Line($"{outParam.Name} := Buffer({sizeParamName}, 0)");
            return;
        }

        if (!outParam.IsPtrToStruct && !outParam.IsPtrToHandle)
            return;

        string pointeeName = GetPointeeName(outParam.Type);

        // An owned [Out] handle is constructed so it auto-frees once returned; the API fills its
        // value (via the instance's `.Ptr`) during the call. v2.0 uses the Win32Handle owned flag
        // plus a DefineProp'd Free for a context-specific RAIIFree.
        if (outParam.Type is PointerType { Pointee: HandleRef ph } && IsOwnedHandle(outParam, ph.FQN, registry))
        {
            EmitV20OwnedOutHandle(w, outParam, pointeeName, ph.FQN, registry);
            return;
        }

        w.Line($"{outParam.Name} := {pointeeName}()");
    }

    /// <summary>
    /// v2.0 owned [Out] handle: construct the Win32Handle with the script-owned flag (so __Delete
    /// frees it), then DefineProp a context-specific <c>Free</c> when the call site overrides the
    /// handle type's default RAIIFree. The handle value starts at 0 and is filled by the API call.
    /// </summary>
    private static void EmitV20OwnedOutHandle(
        AhkWriter w,
        ParameterMember outParam,
        string handleName,
        string handleFqn,
        TypeRegistry registry
    )
    {
        string fieldName = GetHandleFieldName(registry, handleFqn);
        string scriptOwned = outParam.ScriptOwned ? "True" : "False";
        w.Line($"{outParam.Name} := {handleName}({{{fieldName}: 0}}, {scriptOwned})");

        if (outParam.RAIIFree is { } raiiFree)
            w.Line(
                $"{outParam.Name}.DefineProp(\"Free\", {{ Call: (self) => {raiiFree.DeclarerName}.{raiiFree.Name}(self.{fieldName}) }})"
            );
    }

    // --- Variadic marshalling ---

    /// <summary>
    /// Emit the varArgs array construction for variadic methods.
    /// Spreads caller's type/value pairs into an array and appends the return-type token
    /// (a quoted calling-convention + return-type string).
    /// </summary>
    private static void EmitVariadicMarshalling(AhkWriter w, MethodMember method)
    {
        string varArgName = method.VariadicParamName;
        w.Line($"varArgs := [{varArgName}*]");

        string retToken = QuotedConvString(method);
        if (!string.IsNullOrWhiteSpace(retToken))
            w.Line($"varArgs.Push({retToken})");

        w.BlankLine();
    }

    // --- DllCall expression ---

    /// <summary>
    /// Build the v2.0 DllCall calling convention + return type string.
    /// Returns e.g. "CDecl", "CDecl int", "int", "HRESULT", or "" (empty).
    /// </summary>
    private static string BuildCallingConventionString(MethodMember method)
    {
        var sb = new System.Text.StringBuilder();

        if (method.CallingConvention == CallingConvention.CDecl)
            sb.Append("CDecl ");

        if (method.HasReturnValue)
        {
            sb.Append(method.ShouldThrowOnHResult ? "HRESULT" : method.Parameters[0].Type.DllCallType);
        }

        return sb.ToString().Trim();
    }

    /// <summary>v2.0 return-type token: the calling-convention string, quoted, or "" when empty.</summary>
    private static string QuotedConvString(MethodMember method)
    {
        string conv = BuildCallingConventionString(method);
        return string.IsNullOrWhiteSpace(conv) ? "" : $"\"{conv}\"";
    }

    private static string BuildDllCallExpression(MethodMember method, string? entry = null)
    {
        var sb = new System.Text.StringBuilder();

        if (method.HasReturnValue)
            sb.Append("result := ");

        // Entry point, if not overridden
        // v2.0's GetProcAddress returns a raw pointer (unlike v2.1's FARPROC wrapper struct).
        entry ??= method.IsOrdinal ? "procAddr" : $"\"{method.DllName}\\{method.EntryPoint}\"";

        sb.Append($"DllCall({entry}");

        // Parameters
        if (method.Parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArguments(method));
        }

        // Variadic: append varArgs* (convention string is already in the array)
        if (method.IsVariadic)
        {
            sb.Append(method.Parameters.Count > 1 ? ", varArgs*" : "varArgs*");
        }
        else
        {
            // Return type token (inline): a quoted calling-convention + return-type string.
            string retToken = QuotedConvString(method);
            if (!string.IsNullOrWhiteSpace(retToken))
            {
                sb.Append($", {retToken}");
            }
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string BuildDllCallArguments(MethodMember method)
    {
        var sb = new System.Text.StringBuilder();

        for (int i = 1; i < method.Parameters.Count; i++)
        {
            var param = method.Parameters[i];

            bool useMarshalVar = param.IsPtrToPrimitive && !param.IsReserved && param != method.OutputParameter;
            bool isComOutput = param == method.OutputParameter && param.IsPtrToCom;

            string marshalAs;
            if (useMarshalVar)
            {
                marshalAs = $"{param.Name}Marshal";
            }
            else if (param.TypeDefName is "PWSTR" or "PSTR")
            {
                marshalAs = "\"ptr\"";
            }
            else if (isComOutput)
            {
                // COM out-params need IUri** - pass a raw ptr slot, then wrap the returned
                // pointer on the way out. A typed `IUri.Ptr` marshal passes the struct buffer
                // directly, collapsing a level of indirection (the API writes the object
                // pointer into the struct's vtable slot, breaking subsequent ComCalls).
                marshalAs = "\"ptr*\"";
            }
            else
            {
                marshalAs = GetParamDllCallTypeToken(param.Type);
            }

            // Value string
            bool isVarRefOutput = param == method.OutputParameter && (param.IsPtrToPrimitive || param.IsPtrToCom);
            string passAs = isVarRefOutput ? $"&{param.Name} := 0" : param.Name;

            sb.Append(marshalAs);
            sb.Append(", ");
            sb.Append(passAs);

            if (i < method.Parameters.Count - 1)
                sb.Append(", ");
        }

        return sb.ToString();
    }

    // --- Error checking ---

    private static void EmitErrorCheck(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        // NTSTATUS: special case - no SetsLastError interaction
        if (method.Parameters[0].Type is NtStatusType)
        {
            w.Line("NTSTATUS.ThrowIfError(result)");
            return;
        }

        List<string> conditions = [];
        List<string> errCodeSources = [];

        var freeWithParams = method.Parameters.Where(p => p.FreeWith != null).ToList();

        if (method.Parameters[0].Type is HResultType && freeWithParams.Count != 0)
        {
            conditions.Add("result != 0");
        }

        if (method.SetsLastError)
        {
            if (method.Parameters[0].TypeDefName == "BOOL")
                conditions.Add("!result");

            conditions.Add("A_LastError");
            errCodeSources.Add("A_LastError");
        }

        if (conditions.Count == 0)
            return;

        w.Line($"if({string.Join(" && ", conditions)}) {{");

        // Free any [FreeWith] output parameters before throwing
        foreach (var param in freeWithParams)
        {
            FreeFuncRef freeWith = param.FreeWith!;
            string callee = $"{freeWith.DeclarerName}.{freeWith.Name}";

            // Extracting the underlying primitive value guarantees that the call works even when free function
            // takes an untyped pointer (e.g. CoTaskMemFree takes void*)
            // TODO we could be stricter - check free function arg type, cast if possible, otherwise use the
            // primitive escape hatch
            string paramName = param.Name;
            switch (param.Type)
            {
                case HandleRef handleRef:
                    string handleMember =
                        registry.Resolve<HandleType>(handleRef.FQN, Architecture.All)?.Members.Single().Name
                        ?? throw new NullReferenceException();
                    paramName = $"{paramName}.{handleMember}";
                    break;
                case PointerType pt when pt.Pointee is HandleRef handleRef:
                    string ptHandleMember =
                        registry.Resolve<HandleType>(handleRef.FQN, Architecture.All)?.Members.Single().Name
                        ?? throw new NullReferenceException();
                    paramName = $"{paramName}.{ptHandleMember}";
                    break;
            }

            w.Line($"    {callee}({paramName})");
        }

        w.Line($"    throw OSError({string.Join(" || ", errCodeSources)})");
        w.Line("}");
        w.BlankLine();
    }

    // --- Return statement ---

    private static void EmitReturnStatement(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        if (!method.HasReturnValue && method.OutputParameter == null)
            return;

        ParameterMember fnRetVal = method.OutputParameter ?? method.Parameters[0];

        // Handle return (direct HandleRef only - ptr-to-handle output params return raw values)
        if (fnRetVal.IsHandle)
        {
            EmitHandleReturn(w, fnRetVal, registry);
            return;
        }

        // COM return (ptr-to-COM output param): wrap the raw IUri* the API wrote
        if (fnRetVal.IsPtrToCom)
        {
            string comName = GetPointeeName(fnRetVal.Type);
            w.Line($"return {comName}({fnRetVal.Name})");
            return;
        }

        // Primitive / other
        w.Line($"return {fnRetVal.Name}");
    }

    private static void EmitHandleReturn(AhkWriter w, ParameterMember fnRetVal, TypeRegistry registry)
    {
        // Get handle info from the type
        string handleName,
            handleFqn;
        if (fnRetVal.Type is HandleRef hr)
        {
            handleName = hr.Name;
            handleFqn = hr.FQN;
        }
        else if (fnRetVal.Type is PointerType { Pointee: HandleRef phr })
        {
            handleName = phr.Name;
            handleFqn = phr.FQN;
        }
        else
        {
            // Shouldn't reach here - caller checked IsHandle || IsPtrToHandle
            w.Line($"return {fnRetVal.Name}");
            return;
        }

        // Look up handle's value field name from the registry
        string fieldName = GetHandleFieldName(registry, handleFqn);

        // Check IgnoreIfReturn values (e.g., NULL handles -> Invalid())
        if (fnRetVal.HasIgnoreIfReturn && fnRetVal.IgnoreIfReturnValues is { Count: > 0 } ignoreValues)
        {
            string orCondition = string.Join(" || ", ignoreValues.Select(v => $"{fnRetVal.Name} == {v}"));
            w.Line($"if({orCondition})");
            w.Line($"    return {fnRetVal.Name}.Invalid()");
            w.BlankLine();
        }

        // Construct handle wrapper - v2.0 Win32Handle base: takes {field: value} object + owned flag.
        string scriptOwned = fnRetVal.ScriptOwned ? "True" : "False";
        w.Line($"resultHandle := {handleName}({{{fieldName}: {fnRetVal.Name}}}, {scriptOwned})");

        // RAIIFree per-instance override (callable as .Free())
        if (fnRetVal.RAIIFree is { } raiiFree)
        {
            string callee = $"{raiiFree.DeclarerName}.{raiiFree.Name}";
            w.Line($"resultHandle.DefineProp(\"Free\", {{ Call: (self) => {callee}(self.{fieldName}) }})");
        }

        w.Line("return resultHandle");
    }

    // --- Helpers ---

    /// <summary>
    /// Get the DllCall type for a parameter, using typed pointer forms (e.g., "int*").
    /// Matches legacy GetDllCallType(useNakedPointer: false) behavior.
    /// </summary>
    internal static string GetParamDllCallType(ResolvedType type) =>
        type switch
        {
            PointerType p => p.TypedDllCallType,
            NativeTypedefRef n => GetParamDllCallType(n.Underlying),
            _ => type.DllCallType,
        };

    /// <summary>
    /// Render the v2.0 DllCall type token for a parameter - the exact quoted type string to
    /// paste into the DllCall arg list.
    /// </summary>
    private static string GetParamDllCallTypeToken(ResolvedType type) => $"\"{GetParamDllCallType(type)}\"";

    /// <summary>
    /// Get the display name of a pointer's pointee (for struct/handle/COM output params).
    /// In v2.1 the optional <paramref name="names"/> resolver rewrites named types to their local
    /// (possibly aliased) identifier; v2.0 passes null and gets the type's own name.
    /// </summary>
    internal static string GetPointeeName(ResolvedType type, ModuleNameResolver? names = null) =>
        type switch
        {
            PointerType { Pointee: StructRef s } => TypeRef(names, s.FQN, s.Name),
            PointerType { Pointee: HandleRef h } => TypeRef(names, h.FQN, h.Name),
            PointerType { Pointee: ComRef c } => TypeRef(names, c.FQN, c.Name),
            PointerType { Pointee: { } p } => p.DisplayName,
            _ => type.DisplayName,
        };

    /// <summary>Local identifier for an imported named type (alias-aware in v2.1, fallback name otherwise).</summary>
    internal static string TypeRef(ModuleNameResolver? names, string fqn, string fallbackName) =>
        names is null ? fallbackName : names.ForType(fqn);

    /// <summary>
    /// Look up a handle type's first field name from the registry.
    /// </summary>
    internal static string GetHandleFieldName(TypeRegistry registry, string handleFqn)
    {
        if (registry.Resolve(handleFqn, Architecture.All) is HandleType ht && ht.Members.Count > 0)
            return ht.Members[0].Name;
        return "Value"; // fallback
    }

    /// <summary>
    /// Whether a returned/output handle param is owned by the script (not <c>[DoNotRelease]</c>) and
    /// its handle type has a free function - i.e. it should auto-free. The boxing differs by version.
    /// </summary>
    internal static bool IsOwnedHandle(ParameterMember param, string handleFqn, TypeRegistry registry) =>
        param.ScriptOwned && registry.Resolve(handleFqn, Architecture.All) is HandleType ht && ht.FreeFunc is not null;

    /// <summary>
    /// Emit a complete COM method (documentation + signature + body).
    /// Port of legacy AhkComMethod.ToAhk().
    /// </summary>
    public static void EmitComMethod(AhkWriter w, ComMethodMember method, TypeRegistry registry)
    {
        DocCommentWriter.WriteMethodDoc(w, method);

        string argList = BuildArgumentList(method);
        using (w.InstanceMethod(method.DeduplicatedName, argList))
        {
            EmitReservedParams(w, method);
            EmitParameterConversions(w, method, isComMethod: true);
            EmitParameterMarshalling(w, method);

            if (method.SetsLastError)
            {
                w.Line("A_LastError := 0");
                w.BlankLine();
            }

            EmitOutputParamMarshalling(w, method, registry);
            w.Line(BuildComCallExpression(method));

            EmitErrorCheck(w, method, registry);
            EmitReturnStatement(w, method, registry);
        }
    }

    /// <summary>
    /// Build a ComCall expression: [result := ] ComCall(VTableIndex, this[, args][, "conv retType"])
    /// Port of legacy AhkComMethod.BuildDllCallCall.
    /// </summary>
    private static string BuildComCallExpression(ComMethodMember method)
    {
        var sb = new System.Text.StringBuilder();

        if (method.HasReturnValue)
            sb.Append("result := ");

        sb.Append($"ComCall({method.VTableIndex}, this");

        // Parameters
        if (method.Parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArguments(method));
        }

        // Return type token: a quoted calling-convention + return-type string.
        string retToken = QuotedConvString(method);
        if (!string.IsNullOrWhiteSpace(retToken))
        {
            sb.Append($", {retToken}");
        }

        sb.Append(')');
        return sb.ToString();
    }
}
