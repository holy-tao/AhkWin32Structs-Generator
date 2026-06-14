namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a NativeTypedefType as a v2.1 native `struct` block. Typedefs are emitted
/// as a single-field struct with a `__value` setter that unwraps a typed instance or
/// stores the raw value, so the instance is transparently assignable. By default no
/// getter is emitted (the wrapped instance keeps its identity); a `value-accessor`
/// override may restore a getter and/or customize the setter's raw-value coercion.
///
/// Example output (BOOL, with a value-accessor override):
/// <code>
/// struct BOOL {
///     value : Int32
///     __value {
///         get => !!this.value
///         set {
///             if (value is BOOL)
///                 this.value := value.value
///             else
///                 this.value := !!value
///         }
///     }
/// }
/// </code>
/// </summary>
public sealed class NativeTypedefEmitter21 : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is NativeTypedefType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var typedef = (NativeTypedefType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitTypedef(w, typedef);

        string filePath = ImportResolver.GetFilePath(outputRoot, typedef.Namespace, typedef.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitTypedef(AhkWriter w, NativeTypedefType typedef)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");

        SingleFieldEmitter.EmitImports(w, typedef);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, typedef);

        using (w.Struct(typedef.Name))
        {
            w.Line($"value : {typedef.Underlying.TypeSpecifier}");

            w.BlankLine();
            SingleFieldEmitter.EmitValueSetter(
                w,
                typedef,
                "value",
                typedef.ValueGetterExpr,
                typedef.ValueSetterCoerceExpr
            );

            w.BlankLine();
            using (w.InstanceMethod("__New", "value := 0"))
            {
                w.Line("this.value := value");
            }

            // Extension code blocks (e.g. NTSTATUS helper methods)
            StructEmitter21.EmitExtensions(w, typedef);
        }
    }
}
