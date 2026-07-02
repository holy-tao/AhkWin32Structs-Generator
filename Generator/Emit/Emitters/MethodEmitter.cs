namespace AhkWin32.Generator.Emit.Emitters;

using System.CommandLine;
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
    /// <paramref name="names"/> resolves imported type/function references to their local (possibly
    /// aliased) identifier, deconflicting imports that collide with this module's exported functions.
    /// </summary>
    public static void EmitDllImportFunction(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        ModuleNameResolver names
    )
    {
        DocCommentWriter.WriteMethodDoc(w, method);

        string argList = BuildArgumentList(method);
        using (w.Function(method.Name, argList))
        {
            EmitDllImportMethodBody(w, method, registry, unqualifyApis: true, names);
        }
    }

    private static void EmitDllImportMethodBody(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        bool unqualifyApis,
        ModuleNameResolver? names = null
    )
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
        EmitParameterConversions(w, method, false, unqualifyApis);
        EmitParameterMarshalling(w, method);

        if (method.SetsLastError)
        {
            w.Line("A_LastError := 0");
            w.BlankLine();
        }

        if (method.IsOrdinal)
            EmitOrdinalLoading(w, method, unqualifyApis, names);

        EmitOutputParamMarshalling(w, method, registry, names);

        if (method.IsVariadic)
            EmitVariadicMarshalling(w, method, registry, unqualifyApis, names);

        w.Line(BuildDllCallExpression(method, registry, unqualifyApis, names));

        if (method.IsOrdinal)
        {
            w.BlankLine();
            string freeLib = unqualifyApis
                ? FunctionRef(names, "Windows.Win32.Foundation.Apis", "FreeLibrary")
                : "Foundation.FreeLibrary";
            w.Line($"{freeLib}(hModule)");
            w.BlankLine();
        }

        EmitErrorCheck(w, method, registry, unqualifyApis, names);
        EmitReturnStatement(w, method, registry, unqualifyApis, names);
    }

    // --- Argument list ---

    /// <summary>
    /// Build the user-facing method argument list (skips reserved and output params).
    /// Appends <c>args*</c> for variadic methods.
    /// </summary>
    private static string BuildArgumentList(MethodMember method)
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

    private static void EmitReservedParams(AhkWriter w, MethodMember method)
    {
        var reserved = method.Parameters.Skip(1).Where(p => p.IsReserved).ToList();
        if (reserved.Count == 0)
            return;

        w.Line(
            $"static {string.Join(", ", reserved.Select(p => $"{p.Name} := 0"))} ;Reserved parameters must always be NULL"
        );
        w.BlankLine();
    }

    // --- Parameter conversions (String→StrPtr, Handle→NumGet) ---

    private static void EmitParameterConversions(
        AhkWriter w,
        MethodMember method,
        bool isComMethod,
        bool unqualifyApis = false
    )
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

    /// <summary>Local identifier for an imported free function (alias-aware in v2.1, bare name otherwise).</summary>
    private static string FunctionRef(ModuleNameResolver? names, string apisFqn, string name) =>
        names is null ? name : names.ForFunction(apisFqn, name);

    /// <summary>Local identifier for an imported named type (alias-aware in v2.1, fallback name otherwise).</summary>
    private static string TypeRef(ModuleNameResolver? names, string fqn, string fallbackName) =>
        names is null ? fallbackName : names.ForType(fqn);

    private static void EmitOrdinalLoading(
        AhkWriter w,
        MethodMember method,
        bool unqualifyApis,
        ModuleNameResolver? names
    )
    {
        const string libLoaderApis = "Windows.Win32.System.LibraryLoader.Apis";
        string loadLib = unqualifyApis
            ? FunctionRef(names, libLoaderApis, "LoadLibraryW")
            : "LibraryLoader.LoadLibraryW";
        string getProc = unqualifyApis
            ? FunctionRef(names, libLoaderApis, "GetProcAddress")
            : "LibraryLoader.GetProcAddress";

        w.Line("; This method's EntryPoint is an ordinal, so we need to load the dll manually");
        w.Line($"hModule := {loadLib}(\"{method.DllName}\")");
        w.Line($"procAddr := {getProc}(hModule, {method.EntryPoint[1..]})");
        w.BlankLine();
    }

    // --- Output parameter marshalling ---

    private static void EmitOutputParamMarshalling(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        ModuleNameResolver? names = null
    )
    {
        if (method.OutputParameter is not { } outParam)
            return;

        if (outParam.IsSizedBuffer)
        {
            // SizedBufferBytesParamIndex is 0-based from metadata; add 1 for Parameters[] (index 0 = return)
            string sizeParamName = method.Parameters[outParam.SizedBufferBytesParamIndex + 1].Name;
            w.Line($"{outParam.Name} := Buffer({sizeParamName}, 0)");
        }
        else if (outParam.IsPtrToStruct || outParam.IsPtrToHandle)
        {
            string pointeeName = GetPointeeName(outParam.Type, names);
            // An owned [Out] handle is boxed as its `.Owned`/`.OwnedWith(...)` subclass so __Delete
            // frees it. The API writes the handle value into this instance's storage via its `.Ptr`.
            string ctor =
                outParam.Type is PointerType { Pointee: HandleRef ph }
                && OwnedHandleClass(pointeeName, outParam, ph.FQN, registry, names) is { } owned
                    ? owned
                    : pointeeName;
            w.Line($"{outParam.Name} := {ctor}()");
        }
    }

    // --- Variadic marshalling ---

    /// <summary>
    /// Emit the varArgs array construction for variadic methods.
    /// Spreads caller's type/value pairs into an array and appends the return-type token
    /// (v2.0: a quoted calling-convention + return-type string; v2.1: a class-ref token).
    /// </summary>
    private static void EmitVariadicMarshalling(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        bool unqualifyApis,
        ModuleNameResolver? names
    )
    {
        string varArgName = method.VariadicParamName;
        w.Line($"varArgs := [{varArgName}*]");

        string retToken = unqualifyApis ? BuildReturnTypeToken(method, registry, names) : QuotedConvString(method);
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

    /// <summary>
    /// Build the v2.1 DllCall/ComCall return-type token. v2.1 has no CDecl (it's obsolete) and
    /// uses type classes directly instead of quoted type strings, so named numeric/struct classes
    /// are emitted unquoted (alias-resolved). Exceptions:
    /// <list type="bullet">
    ///     <item>HRESULT renders as the quoted <c>"HRESULT"</c> string so the runtime auto-throws an
    ///     OSError on failure codes - unless this HRESULT is not flagged to throw, in which case it is
    ///     returned as a raw <c>Int32</c>.</item>
    ///     <item>Handle returns render as <c>IntPtr</c> (a raw pointer); <see cref="EmitHandleReturn"/>
    ///     boxes the value into its handle wrapper afterwards.</item>
    /// </list>
    /// Returns "" when the method has no return value to specify.
    /// </summary>
    private static string BuildReturnTypeToken(MethodMember method, TypeRegistry registry, ModuleNameResolver? names)
    {
        if (!method.HasReturnValue)
            return "";

        ParameterMember fnRetVal = method.Parameters[0];
        ResolvedType retType = fnRetVal.Type;

        if (retType is HResultType)
            return method.ShouldThrowOnHResult ? "\"HRESULT\"" : "Int32";

        string token = ReturnTypeClassRef(retType, names);

        // Ownership: a directly-returned handle that the script owns (not [DoNotRelease]) is boxed
        // as its `.Owned` (or `.OwnedWith(...)`) subclass so __Delete frees it when it leaves scope.
        if (retType is HandleRef hr && OwnedHandleClass(token, fnRetVal, hr.FQN, registry, names) is { } owned)
            token = owned;

        return token;
    }

    /// <summary>
    /// The class expression to box an owned returned/output handle, or <c>null</c> if it's borrowed.
    /// Returns <c><paramref name="baseClass"/>.Owned</c> for the handle's default free function, or
    /// <c>.OwnedWith(freeFunc)</c> when the call-site <c>RAIIFree</c> diverges from that default (e.g.
    /// a HANDLE closed with FindClose rather than CloseHandle). Borrowed (null) when the param is
    /// <c>[DoNotRelease]</c> or the handle type has no free function at all.
    /// </summary>
    private static string? OwnedHandleClass(
        string baseClass,
        ParameterMember param,
        string handleFqn,
        TypeRegistry registry,
        ModuleNameResolver? names
    )
    {
        if (!param.ScriptOwned)
            return null;
        if (registry.Resolve(handleFqn, Architecture.All) is not HandleType ht || ht.FreeFunc is null)
            return null;
        if (DivergentRAIIFree(param, ht) is { } raii)
            return $"{baseClass}.OwnedWith({FunctionRef(names, raii.ApisFQN, raii.Name)})";
        return $"{baseClass}.Owned";
    }

    /// <summary>
    /// The call-site <c>RAIIFree</c> function for an owned handle param that differs from the handle
    /// type's default free function (and so requires an <c>OwnedWith</c> factory), or <c>null</c>.
    /// </summary>
    private static FreeFuncRef? DivergentRAIIFree(ParameterMember param, HandleType ht) =>
        ht.FreeFunc is not null && param.RAIIFree is { } raii && raii != ht.FreeFunc ? raii : null;

    /// <summary>
    /// The v2.1 type-class reference used to convert a return value, alias-resolved for the local
    /// module. Mirrors <see cref="ResolvedType.TypeSpecifier"/> but routes named types through the
    /// <see cref="ModuleNameResolver"/>. Pointer-to-named-type returns use the dereferencing
    /// <c>X.Ptr</c> form; void/opaque/primitive pointers return the raw pointer (<c>IntPtr</c>).
    /// </summary>
    private static string ReturnTypeClassRef(ResolvedType type, ModuleNameResolver? names) =>
        type switch
        {
            NativeTypedefRef n => TypeRef(names, n.FQN, n.Name),
            StructRef s => TypeRef(names, s.FQN, s.Name),
            EnumRef e => TypeRef(names, e.FQN, e.Name),
            ComRef c => TypeRef(names, c.FQN, c.Name),
            FunctionPointerType { FQN: { } fqn } f => TypeRef(names, fqn, f.Name),
            NtStatusType => "NTSTATUS",
            PointerType { Pointee: StructRef s } => $"{TypeRef(names, s.FQN, s.Name)}.Ptr",
            PointerType { Pointee: HandleRef h } => $"{TypeRef(names, h.FQN, h.Name)}.Ptr",
            PointerType { Pointee: ComRef c } => $"{TypeRef(names, c.FQN, c.Name)}.Ptr",
            PointerType { Pointee: NativeTypedefRef n } => $"{TypeRef(names, n.FQN, n.Name)}.Ptr",
            PointerType { Pointee: EnumRef e } => $"{TypeRef(names, e.FQN, e.Name)}.Ptr",
            // void*, ptr-to-primitive, function ptr -> return the raw pointer
            PointerType => "IntPtr",
            // Primitives: Int32, IntPtr, Float32, ...
            _ => type.TypeSpecifier,
        };

    private static string BuildDllCallExpression(
        MethodMember method,
        TypeRegistry registry,
        bool unqualifyApis = false,
        ModuleNameResolver? names = null,
        string? entry = null
    )
    {
        var sb = new System.Text.StringBuilder();

        if (method.HasReturnValue)
            sb.Append("result := ");

        // Entry point, if not overridden
        entry ??= method.IsOrdinal ? "procAddr.value" : $"\"{method.DllName}\\{method.EntryPoint}\"";

        sb.Append($"DllCall({entry}");

        // Parameters
        if (method.Parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArguments(method, unqualifyApis, names));
        }

        // Variadic: append varArgs* (convention string is already in the array)
        if (method.IsVariadic)
        {
            sb.Append(method.Parameters.Count > 1 ? ", varArgs*" : "varArgs*");
        }
        else
        {
            // Return type token (inline). v2.1 uses unquoted type-class refs; v2.0 a quoted
            // calling-convention + return-type string.
            string retToken = unqualifyApis ? BuildReturnTypeToken(method, registry, names) : QuotedConvString(method);
            if (!string.IsNullOrWhiteSpace(retToken))
            {
                sb.Append($", {retToken}");
            }
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string BuildDllCallArguments(
        MethodMember method,
        bool unqualifyApis = false,
        ModuleNameResolver? names = null
    )
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
                // COM out-params need IUri** — pass a raw ptr slot, then wrap the returned
                // pointer on the way out. A typed `IUri.Ptr` marshal passes the struct buffer
                // directly, collapsing a level of indirection (the API writes the object
                // pointer into the struct's vtable slot, breaking subsequent ComCalls).
                marshalAs = "\"ptr*\"";
            }
            else
            {
                marshalAs = GetParamDllCallTypeToken(param.Type, unqualifyApis, names);
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

    private static void EmitErrorCheck(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        bool unqualifyApis = false,
        ModuleNameResolver? names = null
    )
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

        if (conditions.Count == 0)
            return;

        w.Line($"if({string.Join(" && ", conditions)}) {{");

        // Free any [FreeWith] output parameters before throwing
        foreach (var param in freeWithParams)
        {
            FreeFuncRef freeWith = param.FreeWith!;
            string callee = unqualifyApis
                ? FunctionRef(names, freeWith.ApisFQN, freeWith.Name)
                : $"{freeWith.DeclarerName}.{freeWith.Name}";

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
                case NativeTypedefRef:
                case PointerType pt when pt.Pointee is NativeTypedefRef:
                    paramName = $"{paramName}.value";
                    break;
            }

            w.Line($"    {callee}({paramName})");
        }

        w.Line($"    throw OSError({string.Join(" || ", errCodeSources)})");
        w.Line("}");
        w.BlankLine();
    }

    // --- Return statement ---

    private static void EmitReturnStatement(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        bool unqualifyApis = false,
        ModuleNameResolver? names = null
    )
    {
        if (!method.HasReturnValue && method.OutputParameter == null)
            return;

        ParameterMember fnRetVal = method.OutputParameter ?? method.Parameters[0];

        // Handle return (direct HandleRef only — ptr-to-handle output params return raw values)
        // In 2.1 we don't need to manually box this, we can specify the class itself as the return value
        if (fnRetVal.IsHandle && !unqualifyApis)
        {
            EmitHandleReturn(w, fnRetVal, registry, unqualifyApis, names);
            return;
        }

        // COM return (ptr-to-COM output param): wrap the raw IUri* the API wrote
        if (fnRetVal.IsPtrToCom)
        {
            string comName = GetPointeeName(fnRetVal.Type, names);
            w.Line($"return {comName}({fnRetVal.Name})");
            return;
        }

        // Primitive / other
        w.Line($"return {fnRetVal.Name}");
    }

    private static void EmitHandleReturn(
        AhkWriter w,
        ParameterMember fnRetVal,
        TypeRegistry registry,
        bool unqualifyApis = false,
        ModuleNameResolver? names = null
    )
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
            w.Line($"resultHandle := {TypeRef(names, handleFqn, handleName)}({fnRetVal.Name})");
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
            string callee = unqualifyApis
                ? FunctionRef(names, raiiFree.ApisFQN, raiiFree.Name)
                : $"{raiiFree.DeclarerName}.{raiiFree.Name}";
            w.Line($"resultHandle.DefineProp(\"Free\", {{ Call: (self) => {callee}(self.{fieldName}) }})");
        }

        w.Line("return resultHandle");
    }

    // --- Helpers ---

    /// <summary>
    /// Get the DllCall type for a parameter, using typed pointer forms (e.g., "int*").
    /// Matches legacy GetDllCallType(useNakedPointer: false) behavior.
    /// </summary>
    private static string GetParamDllCallType(ResolvedType type) =>
        type switch
        {
            PointerType p => p.TypedDllCallType,
            NativeTypedefRef n => GetParamDllCallType(n.Underlying),
            _ => type.DllCallType,
        };

    /// <summary>
    /// Render the DllCall type token for a parameter - the exact text to paste into
    /// the DllCall arg list. For v2.0 this is a quoted type string. For v2.1
    /// (<paramref name="unqualifyApis"/> = true) named types render as unquoted class
    /// references (HWND, RECT.Ptr, BOOL, ...) so DllCall uses the type class directly.
    /// </summary>
    public static string GetParamDllCallTypeToken(
        ResolvedType type,
        bool unqualifyApis,
        ModuleNameResolver? names = null
    )
    {
        if (!unqualifyApis)
            return $"\"{GetParamDllCallType(type)}\"";

        return type switch
        {
            HandleRef h => TypeRef(names, h.FQN, h.Name),
            NativeTypedefRef n => TypeRef(names, n.FQN, n.Name),
            StructRef s => TypeRef(names, s.FQN, s.Name),
            EnumRef e => TypeRef(names, e.FQN, e.Name),
            FunctionPointerType { FQN: { } fqn } f => TypeRef(names, fqn, f.Name),
            NtStatusType => "NTSTATUS",
            PointerType { Pointee: StructRef s } => $"{TypeRef(names, s.FQN, s.Name)}.Ptr",
            PointerType { Pointee: HandleRef h } => $"{TypeRef(names, h.FQN, h.Name)}.Ptr",
            PointerType { Pointee: ComRef c } => $"{TypeRef(names, c.FQN, c.Name)}.Ptr",
            PointerType { Pointee: NativeTypedefRef n } => $"{TypeRef(names, n.FQN, n.Name)}.Ptr",
            PrimitiveType p => p.TypeSpecifier,
            // Fallback: pointer-to-primitive (typed star), void*, function ptr, HRESULT, etc.
            _ => $"\"{GetParamDllCallType(type)}\"",
        };
    }

    /// <summary>
    /// Get the display name of a pointer's pointee (for struct/handle/COM output params).
    /// </summary>
    private static string GetPointeeName(ResolvedType type, ModuleNameResolver? names = null) =>
        type switch
        {
            PointerType { Pointee: StructRef s } => TypeRef(names, s.FQN, s.Name),
            PointerType { Pointee: HandleRef h } => TypeRef(names, h.FQN, h.Name),
            PointerType { Pointee: ComRef c } => TypeRef(names, c.FQN, c.Name),
            PointerType { Pointee: { } p } => p.DisplayName,
            _ => type.DisplayName,
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
    public static void EmitComMethod(
        AhkWriter w,
        ComMethodMember method,
        TypeRegistry registry,
        AhkVersion version = AhkVersion.v20
    )
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

            EmitOutputParamMarshalling(w, method, registry);
            w.Line(BuildComCallExpression(method, registry, unqualifyApis));

            EmitErrorCheck(w, method, registry, unqualifyApis);
            EmitReturnStatement(w, method, registry, unqualifyApis);
        }
    }

    /// <summary>
    /// Build a ComCall expression: [result := ] ComCall(VTableIndex, this[, args][, "conv retType"])
    /// Port of legacy AhkComMethod.BuildDllCallCall.
    /// </summary>
    private static string BuildComCallExpression(
        ComMethodMember method,
        TypeRegistry registry,
        bool unqualifyApis = false
    )
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

        // Return type token. v2.1 uses unquoted type-class refs (ComCall defaults to HRESULT when
        // omitted); v2.0 a quoted calling-convention + return-type string.
        string retToken = unqualifyApis ? BuildReturnTypeToken(method, registry, null) : QuotedConvString(method);
        if (!string.IsNullOrWhiteSpace(retToken))
        {
            sb.Append($", {retToken}");
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Emit a delegate's <c>Invoke</c> method - this is an instance method always named "Call" whose entry
    /// point is the delegate's function pointer, not a method name.
    /// </summary>
    public static void EmitDelegateInvokeMethod(AhkWriter w, MethodMember method, TypeRegistry registry)
    {
        DocCommentWriter.WriteMethodDoc(w, method);
        string argList = BuildArgumentList(method);

        using var _ = w.InstanceMethod("Call", argList);

        EmitReservedParams(w, method);
        EmitParameterConversions(w, method, false, true);
        EmitParameterMarshalling(w, method);

        if (method.SetsLastError)
        {
            w.Line("A_LastError := 0");
            w.BlankLine();
        }

        EmitOutputParamMarshalling(w, method, registry);
        w.Line(BuildDllCallExpression(method, registry, true, null, entry: "this.value"));

        EmitErrorCheck(w, method, registry, true);
        EmitReturnStatement(w, method, registry, true);
    }
}
