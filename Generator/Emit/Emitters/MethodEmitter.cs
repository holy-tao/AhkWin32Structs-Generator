namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits method bodies (DllCall) for DllImport methods.
/// Static helper class used by ApiTypeEmitter and ComInterfaceEmitter.
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
            EmitDllImportMethodBody(w, method, registry, unqualifyApis: false);
        }
    }

    /// <summary>
    /// Emit a complete DllImport function (documentation + signature + body). Used in v2.1. Identical to
    /// methods except not static and exported by default. Calls into other Apis files are unqualified
    /// (free functions) since v2.1 Apis modules export functions, not class methods.
    /// </summary>
    public static void EmitDllImportFunction(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        DocCommentWriter.WriteMethodDoc(w, method);

        string argList = BuildArgumentList(method);
        using (w.Function(method.Name, argList))
        {
            EmitDllImportMethodBody(w, method, registry, unqualifyApis: true);
        }
    }

    private static void EmitDllImportMethodBody(AhkWriter w, MethodMember method, TypeRegistry registry, bool unqualifyApis)
    {
        // AutoHotkey doesn't support the thiscall calling convention
        if (method.CallingConvention == CallingConvention.ThisCall)
        {
            w.Line("throw MethodError(\"Not supported: AutoHotkey does not support the thiscall calling convention\", , A_ThisFunc)");
            return;
        }

        EmitReservedParams(w, method);
        EmitParameterConversions(w, method, false, unqualifyApis);
        EmitParameterMarshalling(w, method);

        if (method.SetsLastError)
        {
            w.Line("A_LastError := 0");
            w.BlankLine();
        }

        if (method.IsOrdinal)
            EmitOrdinalLoading(w, method, unqualifyApis);

        EmitOutputParamMarshalling(w, method);

        if (method.IsVariadic)
            EmitVariadicMarshalling(w, method);

        w.Line(BuildDllCallExpression(method, unqualifyApis));

        if (method.IsOrdinal)
        {
            w.BlankLine();
            w.Line(unqualifyApis ? "FreeLibrary(hModule)" : "Foundation.FreeLibrary(hModule)");
            w.BlankLine();
        }

        EmitErrorCheck(w, method, unqualifyApis);
        EmitReturnStatement(w, method, registry, unqualifyApis);
    }

    // --- Argument list ---

    /// <summary>
    /// Build the user-facing method argument list (skips reserved and output params).
    /// Appends <c>args*</c> for variadic methods.
    /// </summary>
    private static string BuildArgumentList(MethodMember method)
    {
        var names = method.Parameters
            .Skip(1) // Skip param 0 (return value)
            .Where(p => !p.IsReserved && p != method.OutputParameter)
            .Select(p => p.Name)
            .ToList();

        if (method.IsVariadic)
            names.Add($"{method.VariadicParamName}*");

        return string.Join(", ", names);
    }

    // --- Reserved parameters ---

    private static void EmitReservedParams(AhkWriter w, MethodMember method)
    {
        var reserved = method.Parameters.Skip(1).Where(p => p.IsReserved).ToList();
        if (reserved.Count == 0) return;

        w.Line($"static {string.Join(", ", reserved.Select(p => $"{p.Name} := 0"))} ;Reserved parameters must always be NULL");
        w.BlankLine();
    }

    // --- Parameter conversions (String→StrPtr, Handle→NumGet) ---

    private static void EmitParameterConversions(AhkWriter w, MethodMember method, bool isComMethod, bool unqualifyApis = false)
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
            else if (param.IsHandle && !unqualifyApis)
            {
                // v2.0: manual handle dereference.
                w.Line($"{param.Name} := {param.Name} is Win32Handle ? NumGet({param.Name}, \"ptr\") : {param.Name}");
            }
        }

        if (w.Length > startLen)
            w.BlankLine();
    }

    // --- Parameter marshalling (VarRef detection for ptr-to-primitive) ---

    private static void EmitParameterMarshalling(AhkWriter w, MethodMember method)
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

    private static void EmitOrdinalLoading(AhkWriter w, MethodMember method, bool unqualifyApis)
    {
        string loadLib = unqualifyApis ? "LoadLibraryW" : "LibraryLoader.LoadLibraryW";
        string getProc = unqualifyApis ? "GetProcAddress" : "LibraryLoader.GetProcAddress";

        w.Line("; This method's EntryPoint is an ordinal, so we need to load the dll manually");
        w.Line($"hModule := {loadLib}(\"{method.DllName}\")");
        w.Line($"procAddr := {getProc}(hModule, {method.EntryPoint[1..]})");
        w.BlankLine();
    }

    // --- Output parameter marshalling ---

    private static void EmitOutputParamMarshalling(AhkWriter w, MethodMember method)
    {
        if (method.OutputParameter is not { } outParam) return;

        if (outParam.IsSizedBuffer)
        {
            // SizedBufferBytesParamIndex is 0-based from metadata; add 1 for Parameters[] (index 0 = return)
            string sizeParamName = method.Parameters[outParam.SizedBufferBytesParamIndex + 1].Name;
            w.Line($"{outParam.Name} := Buffer({sizeParamName}, 0)");
        }
        else if (outParam.IsPtrToStruct || outParam.IsPtrToHandle)
        {
            string pointeeName = GetPointeeName(outParam.Type);
            w.Line($"{outParam.Name} := {pointeeName}()");
        }
    }

    // --- Variadic marshalling ---

    /// <summary>
    /// Emit the varArgs array construction for variadic methods.
    /// Spreads caller's type/value pairs into an array and appends the calling convention string.
    /// </summary>
    private static void EmitVariadicMarshalling(AhkWriter w, MethodMember method)
    {
        string convString = BuildCallingConventionString(method);
        string varArgName = method.VariadicParamName;

        w.Line($"varArgs := [{varArgName}*]");
        if (!string.IsNullOrWhiteSpace(convString))
            w.Line($"varArgs.Push(\"{convString}\")");

        w.BlankLine();
    }

    // --- DllCall expression ---

    /// <summary>
    /// Build the DllCall calling convention + return type string.
    /// Returns e.g. "CDecl", "CDecl int", "int", "HRESULT", or "" (empty).
    /// </summary>
    private static string BuildCallingConventionString(MethodMember method)
    {
        var sb = new System.Text.StringBuilder();

        if (method.CallingConvention == CallingConvention.CDecl)
            sb.Append("CDecl ");

        if (method.HasReturnValue)
        {
            sb.Append(method.ShouldThrowOnHResult
                ? "HRESULT"
                : method.Parameters[0].Type.DllCallType);
        }

        return sb.ToString().Trim();
    }

    private static string BuildDllCallExpression(MethodMember method, bool unqualifyApis = false)
    {
        var sb = new System.Text.StringBuilder();

        if (method.HasReturnValue)
            sb.Append("result := ");

        // Entry point
        string entry = method.IsOrdinal
            ? "procAddr"
            : $"\"{method.DllName}\\{method.EntryPoint}\"";

        sb.Append($"DllCall({entry}");

        // Parameters
        if (method.Parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArguments(method, unqualifyApis));
        }

        // Variadic: append varArgs* (convention string is already in the array)
        if (method.IsVariadic)
        {
            sb.Append(method.Parameters.Count > 1 ? ", varArgs*" : "varArgs*");
        }
        else
        {
            // Calling convention + return type (inline)
            string convString = BuildCallingConventionString(method);
            if (!string.IsNullOrWhiteSpace(convString))
            {
                sb.Append($", \"{convString}\"");
            }
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string BuildDllCallArguments(MethodMember method, bool unqualifyApis = false)
    {
        var sb = new System.Text.StringBuilder();

        for (int i = 1; i < method.Parameters.Count; i++)
        {
            var param = method.Parameters[i];

            bool useMarshalVar = param.IsPtrToPrimitive
                && !param.IsReserved
                && param != method.OutputParameter;

            string marshalAs;
            if (useMarshalVar)
            {
                marshalAs = $"{param.Name}Marshal";
            }
            else if (param.TypeDefName is "PWSTR" or "PSTR")
            {
                marshalAs = "\"ptr\"";
            }
            else
            {
                marshalAs = GetParamDllCallTypeToken(param.Type, unqualifyApis);
            }

            // Value string
            bool isVarRefOutput = param == method.OutputParameter
                && (param.IsPtrToPrimitive || param.IsPtrToCom);
            string passAs = isVarRefOutput
                ? $"&{param.Name} := 0"
                : param.Name;

            sb.Append(marshalAs);
            sb.Append(", ");
            sb.Append(passAs);

            if (i < method.Parameters.Count - 1)
                sb.Append(", ");
        }

        return sb.ToString();
    }

    // --- Error checking ---

    private static void EmitErrorCheck(AhkWriter w, MethodMember method, bool unqualifyApis = false)
    {
        // NTSTATUS: special case — no SetsLastError interaction
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

        if (conditions.Count == 0) return;

        w.Line($"if({string.Join(" && ", conditions)}) {{");

        // Free any [FreeWith] output parameters before throwing
        foreach (var param in freeWithParams)
        {
            FreeFuncRef freeWith = param.FreeWith!;
            string callee = unqualifyApis ? freeWith.Name : $"{freeWith.DeclarerName}.{freeWith.Name}";
            w.Line($"    {callee}({param.Name})");
        }

        w.Line($"    throw OSError({string.Join(" || ", errCodeSources)})");
        w.Line("}");
        w.BlankLine();
    }

    // --- Return statement ---

    private static void EmitReturnStatement(AhkWriter w, MethodMember method, TypeRegistry registry, bool unqualifyApis = false)
    {
        if (!method.HasReturnValue && method.OutputParameter == null)
            return;

        ParameterMember fnRetVal = method.OutputParameter ?? method.Parameters[0];

        // Handle return (direct HandleRef only — ptr-to-handle output params return raw values)
        if (fnRetVal.IsHandle)
        {
            EmitHandleReturn(w, fnRetVal, registry, unqualifyApis);
            return;
        }

        // COM return (ptr-to-COM output param)
        if (fnRetVal.IsPtrToCom)
        {
            string comName = GetPointeeName(fnRetVal.Type);
            w.Line($"return {comName}({fnRetVal.Name})");
            return;
        }

        // Primitive / other
        w.Line($"return {fnRetVal.Name}");
    }

    private static void EmitHandleReturn(AhkWriter w, ParameterMember fnRetVal, TypeRegistry registry, bool unqualifyApis = false)
    {
        // Get handle info from the type
        string handleName, handleFqn;
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
            // Shouldn't reach here — caller checked IsHandle || IsPtrToHandle
            w.Line($"return {fnRetVal.Name}");
            return;
        }

        // Look up handle's value field name from the registry
        string fieldName = GetHandleFieldName(registry, handleFqn);

        // Check IgnoreIfReturn values (e.g., NULL handles → Invalid())
        if (fnRetVal.HasIgnoreIfReturn && fnRetVal.IgnoreIfReturnValues is { Count: > 0 } ignoreValues)
        {
            string orCondition = string.Join(" || ", ignoreValues.Select(v => $"{fnRetVal.Name} == {v}"));
            w.Line($"if({orCondition})");
            w.Line($"    return {fnRetVal.Name}.Invalid()");
            w.BlankLine();
        }

        // Construct handle wrapper
        if (unqualifyApis)
        {
            // v2.1 handle: `__New(value := default) { this.value := value }`. Pass the raw value.
            // TODO: handle ownership
            w.Line($"resultHandle := {handleName}({fnRetVal.Name})");
        }
        else
        {
            // v2.0 Win32Handle base: takes {field: value} object + owned flag.
            string scriptOwned = fnRetVal.ScriptOwned ? "True" : "False";
            w.Line($"resultHandle := {handleName}({{{fieldName}: {fnRetVal.Name}}}, {scriptOwned})");
        }

        // RAIIFree per-instance override (callable as .Free(); not auto-invoked in v2.1)
        if (fnRetVal.RAIIFree is { } raiiFree)
        {
            string callee = unqualifyApis ? raiiFree.Name : $"{raiiFree.DeclarerName}.{raiiFree.Name}";
            w.Line($"resultHandle.DefineProp(\"Free\", {{ Call: (self) => {callee}(self.{fieldName}) }})");
        }

        w.Line("return resultHandle");
    }

    // --- Helpers ---

    /// <summary>
    /// Get the DllCall type for a parameter, using typed pointer forms (e.g., "int*").
    /// Matches legacy GetDllCallType(useNakedPointer: false) behavior.
    /// </summary>
    private static string GetParamDllCallType(ResolvedType type) => type switch
    {
        PointerType p => p.TypedDllCallType,
        NativeTypedefRef n => GetParamDllCallType(n.Underlying),
        _ => type.DllCallType
    };

    /// <summary>
    /// Render the DllCall type token for a parameter - the exact text to paste into
    /// the DllCall arg list. For v2.0 this is a quoted type string. For v2.1
    /// (<paramref name="unqualifyApis"/> = true) named types render as unquoted class
    /// references (HWND, RECT.Ptr, BOOL, ...) so DllCall uses the type class directly.
    /// </summary>
    private static string GetParamDllCallTypeToken(ResolvedType type, bool unqualifyApis)
    {
        if (!unqualifyApis)
            return $"\"{GetParamDllCallType(type)}\"";

        return type switch
        {
            HandleRef h                                  => h.Name,
            NativeTypedefRef n                           => n.Name,
            StructRef s                                  => s.Name,
            EnumRef e                                    => e.Name,
            NtStatusType                                 => "NTSTATUS",
            PointerType { Pointee: StructRef s }         => $"{s.Name}.Ptr",
            PointerType { Pointee: HandleRef h }         => $"{h.Name}.Ptr",
            PointerType { Pointee: ComRef c }            => $"{c.Name}.Ptr",
            PointerType { Pointee: NativeTypedefRef n }  => $"{n.Name}.Ptr",
            // Fallback: pointer-to-primitive (typed star), void*, function ptr, enum, HRESULT, etc.
            _                                            => $"\"{GetParamDllCallType(type)}\""
        };
    }

    /// <summary>
    /// Get the display name of a pointer's pointee (for struct/handle/COM output params).
    /// </summary>
    private static string GetPointeeName(ResolvedType type) => type switch
    {
        PointerType { Pointee: StructRef s } => s.Name,
        PointerType { Pointee: HandleRef h } => h.Name,
        PointerType { Pointee: ComRef c } => c.Name,
        PointerType { Pointee: { } p } => p.DisplayName,
        _ => type.DisplayName
    };

    /// <summary>
    /// Look up a handle type's first field name from the registry.
    /// </summary>
    private static string GetHandleFieldName(TypeRegistry registry, string handleFqn)
    {
        if (registry.Resolve(handleFqn, Architecture.All) is HandleType ht && ht.Members.Count > 0)
            return ht.Members[0].Name;
        return "Value"; // fallback
    }

    /// <summary>
    /// Emit a complete COM method (documentation + signature + body).
    /// Port of legacy AhkComMethod.ToAhk().
    /// </summary>
    public static void EmitComMethod(AhkWriter w, ComMethodMember method, TypeRegistry registry, AhkVersion version = AhkVersion.v20)
    {
        DocCommentWriter.WriteMethodDoc(w, method);
        bool unqualifyApis = version is AhkVersion.v21;

        string argList = BuildArgumentList(method);
        using (w.InstanceMethod(method.DeduplicatedName, argList))
        {
            EmitReservedParams(w, method);
            EmitParameterConversions(w, method, isComMethod: true, unqualifyApis);
            EmitParameterMarshalling(w, method);

            if (method.SetsLastError)
            {
                w.Line("A_LastError := 0");
                w.BlankLine();
            }

            EmitOutputParamMarshalling(w, method);
            w.Line(BuildComCallExpression(method, unqualifyApis));

            EmitErrorCheck(w, method, unqualifyApis);
            EmitReturnStatement(w, method, registry, unqualifyApis);
        }
    }

    /// <summary>
    /// Build a ComCall expression: [result := ] ComCall(VTableIndex, this[, args][, "conv retType"])
    /// Port of legacy AhkComMethod.BuildDllCallCall.
    /// </summary>
    private static string BuildComCallExpression(ComMethodMember method, bool unqualifyApis = false)
    {
        var sb = new System.Text.StringBuilder();

        if (method.HasReturnValue)
            sb.Append("result := ");

        sb.Append($"ComCall({method.VTableIndex}, this");

        // Parameters
        if (method.Parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArguments(method, unqualifyApis));
        }

        // Calling convention + return type
        string convString = BuildCallingConventionString(method);
        if (!string.IsNullOrWhiteSpace(convString))
        {
            sb.Append($", \"{convString}\"");
        }

        sb.Append(')');
        return sb.ToString();
    }
}
