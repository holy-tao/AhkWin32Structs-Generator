namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;

/// <summary>
/// Emits individual constant members (enum values or API constants).
/// Handles all ConstantValue variants: primitive, GUID, struct-handle, struct-non-handle.
/// Used by EnumEmitter and later ApiTypeEmitter.
/// </summary>
public static class ConstantEmitter
{
    /// <summary>
    /// Emit a single constant (doc comment + value) into the writer at the current indent level.
    /// </summary>
    public static void EmitConstant(AhkWriter w, ConstantMember constant)
    {
        DocCommentWriter.WriteConstantDoc(w, constant);

        switch (constant.Value)
        {
            case PrimitiveConstantValue pv:
                w.StaticField(constant.Name, pv.FormattedValue);
                break;

            case GuidConstantValue gv:
                w.StaticField(constant.Name, gv.AsAhk);
                break;

            case StructConstantValue { IsHandle: true } sv:
                w.StaticField(constant.Name, sv.AsAhk);
                break;

            case StructConstantValue { IsHandle: false } sv:
                EmitStructConstantProperty(w, constant.Name, sv);
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported constant value type: {constant.Value.GetType().Name} for '{constant.Name}'"
                );
        }
    }

    /// <summary>
    /// Emit a single constant (doc comment + value) into the writer in an AHK v2.1-compatible way.
    /// <paramref name="names"/> resolves referenced type names to their local (possibly aliased)
    /// identifier so struct/handle constants keep working when their type was deconflicted.
    /// </summary>
    public static void EmitConstant21(AhkWriter w, ConstantMember constant, ModuleNameResolver names)
    {
        DocCommentWriter.WriteConstantDoc(w, constant);

        switch (constant.Value)
        {
            case PrimitiveConstantValue pv:
                w.Variable(constant.Name, pv.FormattedValue);
                break;

            case GuidConstantValue gv:
                w.Variable(constant.Name, gv.AsAhk);
                break;

            case StructConstantValue { IsHandle: true } sv:
                // Reconstruct the handle wrapper using the resolved (possibly aliased) type name
                // rather than the pre-baked AsAhk string, which embeds the raw StructName.
                w.Variable(constant.Name, $"{names.ForType(sv.StructFQN)}({{Value: {sv.HandleValue}}}, false)");
                break;

            case StructConstantValue { IsHandle: false } sv:
                EmitStructConstantFunction(w, constant.Name, sv, names);
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported constant value type: {constant.Value.GetType().Name} for '{constant.Name}'"
                );
        }
    }

    /// <summary>
    /// Emit a struct constant as a getter property with field initialization.
    /// </summary>
    private static void EmitStructConstantProperty(AhkWriter w, string name, StructConstantValue sv)
    {
        using (w.Property(name))
        {
            EmitStructConstantInitializers(w, sv);
        }
    }

    /// <summary>
    /// Emit a struct constant as a function that returns the struct. For v2.1
    /// </summary>
    private static void EmitStructConstantFunction(
        AhkWriter w,
        string name,
        StructConstantValue sv,
        ModuleNameResolver names
    )
    {
        using (w.Function(name))
        {
            EmitStructConstantInitializers(w, sv, names);
        }
    }

    /// <summary>
    /// Emit struct fill code for a given struct constant
    /// </summary>
    private static void EmitStructConstantInitializers(
        AhkWriter w,
        StructConstantValue sv,
        ModuleNameResolver? names = null
    )
    {
        using (w.GetBlock())
        {
            w.Line($"value := {names?.ForType(sv.StructFQN) ?? sv.StructName}()");

            foreach (StructFieldInit init in sv.FieldInits ?? [])
            {
                EmitFieldInit(w, init);
            }

            w.Line("return value");
        }
    }

    /// <summary>
    /// Emit a single field initialization line based on its kind.
    /// </summary>
    private static void EmitFieldInit(AhkWriter w, StructFieldInit init)
    {
        switch (init.Kind)
        {
            case StructFieldInitKind.Direct:
                w.Line($"{init.FieldPath} := {init.Value}");
                break;

            case StructFieldInitKind.ArrayElement:
                w.Line($"{init.FieldPath}[{init.ArrayIndex}] := {init.Value}");
                break;

            case StructFieldInitKind.GuidPointer:
                // Extract field name from the path for the static variable name
                string fieldName = init.FieldPath.Contains('.')
                    ? init.FieldPath[(init.FieldPath.LastIndexOf('.') + 1)..]
                    : init.FieldPath;
                w.Line($"static {fieldName}_guid := Guid(\"{{{init.GuidValue:D}}}\")");
                w.Line($"{init.FieldPath} := {fieldName}_guid.ptr");
                break;

            default:
                throw new NotSupportedException($"Unsupported struct field init kind: {init.Kind}");
        }
    }
}
