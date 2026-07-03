namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emitter for delegates / function pointers. We emit these as pointer-sized structs, like handles, but with a
/// Call method that DllCalls the pointer and a subtype that wraps an AHK function via CallbackCreate.
/// </summary>
public sealed class DelegateEmitter(TypeRegistry typeRegistry) : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is DelegateType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var writer = new AhkWriter(Metadata.AhkVersion.v21);
        EmitDelegate(writer, (DelegateType)type);

        string filePath = ImportResolver.GetFilePath(outputRoot, type.Namespace, type.CanonicalName);
        return new EmitResult(writer.ToString(), filePath);
    }

    private void EmitDelegate(AhkWriter w, DelegateType delegateType)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");
        SingleFieldEmitter.EmitImports(w, delegateType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, delegateType);
        using var _structScope = w.Struct(delegateType.Name);

        w.Line($"value : IntPtr");
        w.BlankLine();

        // TODO overrides
        SingleFieldEmitter.EmitValueSetter(w, delegateType, "value");
        w.BlankLine();

        MethodEmitter.EmitDelegateInvokeMethod(w, delegateType.Invoke, typeRegistry);

        w.BlankLine();
        using (w.JSDocComment())
        {
            w.Line($"A {delegateType.Name} that invokes the given AHK function when called.");
            w.Line("This callback is owned by the script and cleaned up automatically.");
        }
        EmitFunctionWrapper(w, delegateType);
    }

    /// <summary>
    /// Subtype that wraps an AHK function - same principle as Handle.Owned
    /// </summary>
    private static void EmitFunctionWrapper(AhkWriter w, DelegateType delegateType)
    {
        using var _ = w.Struct("From", delegateType.Name);

        List<string> paramTypes = delegateType
            .Invoke.InputParameters.Select(p => MethodEmitter.GetParamDllCallTypeToken(p.Type, true))
            .ToList();
        string returnType = MethodEmitter.GetParamDllCallTypeToken(delegateType.Invoke.Parameters[0].Type, true);

        using (w.JSDocComment())
        {
            // For .Ptr returns, the AHK script just sees the pointee
            var ahkParams = paramTypes.Select(p => p.EndsWith(".Ptr") ? p[..^4] : p);

            w.Line($"Creates a {delegateType.Name} pointer that invokes the given AHK function when called.");
            w.Line($"@param {{Func({string.Join(", ", ahkParams)}) => {returnType}}} fn the function to invoke.");
        }

        using (w.InstanceMethod("__New", "fn"))
        {
            using (w.If($"!HasMethod(fn, , {paramTypes.Count})"))
            {
                w.Line(
                    $"throw MethodError(\"Object of type \" Type(fn) \" is not callable with {paramTypes.Count} parameters.\", -1, fn)"
                );
            }

            // Get the list of parameters in CallbackCreate order, with the return type last
            string typeSpec = $"[{string.Join(", ", paramTypes.Append(returnType))}]";
            string callConv = delegateType.CallingConvention is CallingConvention.CDecl ? "\"cdecl\"" : "";

            w.Line($"this.value := CallbackCreate(fn, {callConv}, {typeSpec})");
        }

        w.BlankLine();
        using (w.InstanceMethod("__Delete", ""))
        {
            using (w.If("this.value"))
                w.Line($"CallbackFree(this.value)");
        }
    }
}
