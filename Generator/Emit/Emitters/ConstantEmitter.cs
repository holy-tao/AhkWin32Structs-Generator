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

            default:
                throw new NotSupportedException(
                    $"Unsupported constant value type: {constant.Value.GetType().Name} for '{constant.Name}'");
        }
    }
}
