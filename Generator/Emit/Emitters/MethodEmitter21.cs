namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits v2.1 method bodies (DllCall) for DllImport functions and COM methods.
/// Static helper used by ApiTypeEmitter21, ComInterfaceEmitter21, and DelegateEmitter.
/// </summary>
public static class MethodEmitter21
{
    /// <summary>
    /// Emit a complete DllImport function (documentation + signature + body).
    /// </summary>
    public static void EmitDllImportFunction(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        ModuleNameResolver names
    )
    {
        DocCommentWriter.WriteMethodDoc(w, method);

        string argList = MethodEmitter.BuildArgumentList(method);
        using (w.Function(method.Name, argList))
        {
            EmitDllImportMethodBody(w, method, registry, names);
        }
    }

    private static void EmitDllImportMethodBody(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        ModuleNameResolver? names
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

        MethodEmitter.EmitReservedParams(w, method);
        EmitParameterConversions(w, method);
        MethodEmitter.EmitParameterMarshalling(w, method);

        if (method.SetsLastError)
        {
            w.Line("A_LastError := 0");
            w.BlankLine();
        }

        if (method.IsOrdinal)
            EmitOrdinalLoading(w, method, names);

        EmitOutputParamMarshalling(w, method, registry, names);

        if (method.IsVariadic)
            EmitVariadicMarshalling(w, method, registry, names);

        w.Line(BuildDllCallExpression(method, registry, names));

        if (method.IsOrdinal)
        {
            w.BlankLine();
            string freeLib = FunctionRef(names, "Windows.Win32.Foundation.Apis", "FreeLibrary");
            w.Line($"{freeLib}(hModule)");
            w.BlankLine();
        }

        EmitErrorCheck(w, method, registry, names);
        EmitReturnStatement(w, method, registry, names);
    }

    // --- Parameter conversions (String->StrPtr) ---

    private static void EmitParameterConversions(AhkWriter w, MethodMember method, bool isComMethod = false)
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
        }

        if (w.Length > startLen)
            w.BlankLine();
    }

    // --- Ordinal entry point loading ---

    /// <summary>Local identifier for an imported free function (alias-aware in v2.1, bare name otherwise).</summary>
    private static string FunctionRef(ModuleNameResolver? names, string apisFqn, string name) =>
        names is null ? name : names.ForFunction(apisFqn, name);

    private static void EmitOrdinalLoading(AhkWriter w, MethodMember method, ModuleNameResolver? names)
    {
        const string libLoaderApis = "Windows.Win32.System.LibraryLoader.Apis";
        string loadLib = FunctionRef(names, libLoaderApis, "LoadLibraryW");
        string getProc = FunctionRef(names, libLoaderApis, "GetProcAddress");

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
        ModuleNameResolver? names
    )
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

        string pointeeName = MethodEmitter.GetPointeeName(outParam.Type, names);

        // An owned [Out] handle is constructed so it auto-frees once returned; the API fills its
        // value (via the instance's `.Ptr`) during the call. v2.1 boxes ownership as a
        // `.Owned`/`.OwnedWith(...)` subclass.
        if (
            outParam.Type is PointerType { Pointee: HandleRef ph }
            && MethodEmitter.IsOwnedHandle(outParam, ph.FQN, registry)
        )
        {
            string ctor = OwnedHandleClass(pointeeName, outParam, ph.FQN, registry, names)!;
            w.Line($"{outParam.Name} := {ctor}()");
            return;
        }

        w.Line($"{outParam.Name} := {pointeeName}()");
    }

    // --- Variadic marshalling ---

    /// <summary>
    /// Emit the varArgs array construction for variadic methods.
    /// Spreads caller's type/value pairs into an array and appends the return-type token (a class-ref token).
    /// </summary>
    private static void EmitVariadicMarshalling(
        AhkWriter w,
        MethodMember method,
        TypeRegistry registry,
        ModuleNameResolver? names
    )
    {
        string varArgName = method.VariadicParamName;
        w.Line($"varArgs := [{varArgName}*]");

        string retToken = BuildReturnTypeToken(method, registry, names);
        if (!string.IsNullOrWhiteSpace(retToken))
            w.Line($"varArgs.Push({retToken})");

        w.BlankLine();
    }

    // --- DllCall expression ---

    /// <summary>
    /// Build the v2.1 DllCall/ComCall return-type token. v2.1 has no CDecl (it's obsolete) and
    /// uses type classes directly instead of quoted type strings, so named numeric/struct classes
    /// are emitted unquoted (alias-resolved).
    ///
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
    /// module.
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

    /// <summary>Local identifier for an imported named type (alias-aware in v2.1, fallback name otherwise).</summary>
    private static string TypeRef(ModuleNameResolver? names, string fqn, string fallbackName) =>
        MethodEmitter.TypeRef(names, fqn, fallbackName);

    private static string BuildDllCallExpression(
        MethodMember method,
        TypeRegistry registry,
        ModuleNameResolver? names,
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
            sb.Append(BuildDllCallArguments(method, names));
        }

        // Variadic: append varArgs* (return-type token is already in the array)
        if (method.IsVariadic)
        {
            sb.Append(method.Parameters.Count > 1 ? ", varArgs*" : "varArgs*");
        }
        else
        {
            // Return type token (inline): unquoted type-class refs.
            string retToken = BuildReturnTypeToken(method, registry, names);
            if (!string.IsNullOrWhiteSpace(retToken))
            {
                sb.Append($", {retToken}");
            }
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string BuildDllCallArguments(MethodMember method, ModuleNameResolver? names)
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
                marshalAs = GetParamDllCallTypeToken(param.Type, names);
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
        ModuleNameResolver? names
    )
    {
        // NTSTATUS: special case - no SetsLastError interaction
        if (method.Parameters[0].Type is NtStatusType)
        {
            w.Line("NTSTATUS.ThrowIfError(result.value)");
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
            string callee = FunctionRef(names, freeWith.ApisFQN, freeWith.Name);

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
                    // v2.1 boxes typed-pointer (`.Ptr`) outputs into a typedef object whose
                    // `.value` holds the raw pointer.
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
        ModuleNameResolver? names
    )
    {
        if (!method.HasReturnValue && method.OutputParameter == null)
            return;

        ParameterMember fnRetVal = method.OutputParameter ?? method.Parameters[0];

        // Handle returns are not boxed manually in v2.1 - the handle class itself is specified as the
        // DllCall return type (see BuildReturnTypeToken), so a directly-returned handle already comes
        // back wrapped, and a ptr-to-handle output param was boxed in EmitOutputParamMarshalling.

        // COM return (ptr-to-COM output param): wrap the raw IUri* the API wrote
        if (fnRetVal.IsPtrToCom)
        {
            string comName = MethodEmitter.GetPointeeName(fnRetVal.Type, names);
            w.Line($"return {comName}({fnRetVal.Name})");
            return;
        }

        // Primitive / handle / other
        w.Line($"return {fnRetVal.Name}");
    }

    // --- Helpers ---

    /// <summary>
    /// Render the v2.1 DllCall type token for a parameter - the exact text to paste into the DllCall
    /// arg list. Named types render as unquoted class references (HWND, RECT.Ptr, BOOL, ...) so
    /// DllCall uses the type class directly.
    /// </summary>
    public static string GetParamDllCallTypeToken(ResolvedType type, ModuleNameResolver? names = null) =>
        type switch
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
            _ => $"\"{MethodEmitter.GetParamDllCallType(type)}\"",
        };

    /// <summary>
    /// Emit a complete COM method (documentation + signature + body).
    /// Port of legacy AhkComMethod.ToAhk().
    /// </summary>
    public static void EmitComMethod(AhkWriter w, ComMethodMember method, TypeRegistry registry)
    {
        DocCommentWriter.WriteMethodDoc(w, method);

        string argList = MethodEmitter.BuildArgumentList(method);
        using (w.InstanceMethod(method.DeduplicatedName, argList))
        {
            MethodEmitter.EmitReservedParams(w, method);
            EmitParameterConversions(w, method, isComMethod: true);
            MethodEmitter.EmitParameterMarshalling(w, method);

            if (method.SetsLastError)
            {
                w.Line("A_LastError := 0");
                w.BlankLine();
            }

            EmitOutputParamMarshalling(w, method, registry, null);
            w.Line(BuildComCallExpression(method, registry));

            EmitErrorCheck(w, method, registry, null);
            EmitReturnStatement(w, method, registry, null);
        }
    }

    /// <summary>
    /// Build a ComCall expression: [result := ] ComCall(VTableIndex, this[, args][, retType])
    /// Port of legacy AhkComMethod.BuildDllCallCall.
    /// </summary>
    private static string BuildComCallExpression(ComMethodMember method, TypeRegistry registry)
    {
        var sb = new System.Text.StringBuilder();

        if (method.HasReturnValue)
            sb.Append("result := ");

        sb.Append($"ComCall({method.VTableIndex}, this");

        // Parameters
        if (method.Parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArguments(method, null));
        }

        // Return type token: unquoted type-class refs (ComCall defaults to HRESULT when omitted).
        string retToken = BuildReturnTypeToken(method, registry, null);
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
        string argList = MethodEmitter.BuildArgumentList(method);

        using var _ = w.InstanceMethod("Call", argList);

        MethodEmitter.EmitReservedParams(w, method);
        EmitParameterConversions(w, method);
        MethodEmitter.EmitParameterMarshalling(w, method);

        if (method.SetsLastError)
        {
            w.Line("A_LastError := 0");
            w.BlankLine();
        }

        EmitOutputParamMarshalling(w, method, registry, null);
        w.Line(BuildDllCallExpression(method, registry, null, entry: "this.value"));

        EmitErrorCheck(w, method, registry, null);
        EmitReturnStatement(w, method, registry, null);
    }
}
