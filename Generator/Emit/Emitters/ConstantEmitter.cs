namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
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
                EmitStructConstantVariable(w, constant.Name, sv, names);
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
    /// Emit a struct constant as a global (auto-execute) varable. For v2.1
    /// </summary>
    private static void EmitStructConstantVariable(
        AhkWriter w,
        string name,
        StructConstantValue sv,
        ModuleNameResolver names
    )
    {
        w.Line($"export global {name} := {names?.ForType(sv.StructFQN) ?? sv.StructName}()");
        foreach (StructFieldInit init in sv.FieldInits ?? [])
        {
            EmitFieldInit(w, init, AhkVersion.v21, name);
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
                EmitFieldInit(w, init, AhkVersion.v20, "value");
            }

            w.Line("return value");
        }
    }

    /// <summary>
    /// Emit a single field initialization line based on its kind.
    /// </summary>
    private static void EmitFieldInit(AhkWriter w, StructFieldInit init, AhkVersion version, string prefix)
    {
        string fieldPath = $"{prefix}.{string.Join(".", init.FieldPath)}";

        switch (init.Kind)
        {
            case StructFieldInitKind.Direct:
                w.Line($"{fieldPath} := {init.Value}");
                break;

            case StructFieldInitKind.ArrayElement:
                w.Line($"{fieldPath}[{init.ArrayIndex}] := {init.Value}");
                break;

            case StructFieldInitKind.Guid:
                // We can't straightforwardly assign a struct to another struct's embedded struct, we need to copy the
                // data between pointers. In v2 we have a helper method but in v2.1 we scrapped the custom base class
                // so we don't have that same utility. They're both RtlCopyMemory under the hood though
                string copyCode = version switch
                {
                    AhkVersion.v20 => $"Guid(\"{{{init.GuidValue:D}}}\").CopyTo({fieldPath}.ptr)",
                    // TODO this is kind of hacky - probably better to reimplement CopyTo?
                    // Can't use a projected method because RtoCopyMemory isn't in the projection
                    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-rtlcopymemory
                    AhkVersion.v21 => $"DllCall(\"NtDll.dll\\RtlCopyMemory\",{Environment.NewLine}"
                        + $"    IntPtr, ObjGetDataPtr({fieldPath}),{Environment.NewLine}"
                        + $"    Guid.Ptr, Guid(\"{{{init.GuidValue:D}}}\"),{Environment.NewLine}"
                        + "    UInt32, 16)",
                    _ => throw new NotSupportedException("Unreachable: " + version.ToString()),
                };
                w.Line(copyCode);
                break;

            case StructFieldInitKind.GuidPointer:
                // Extract field name from the path for the static variable name
                string fieldName = init.FieldPath[init.FieldPath.Count - 1];

                // Pin a Guid struct to either the method (v2.0) or the module (v2.1)
                string pinCode = version switch
                {
                    AhkVersion.v20 => $"static {fieldName}_guid := Guid(\"{{{init.GuidValue:D}}}\")",
                    AhkVersion.v21 => $"{fieldName}_guid := Guid(\"{{{init.GuidValue:D}}}\")",
                    _ => throw new NotSupportedException("Unreachable " + version.ToString()),
                };
                w.Line(pinCode);
                w.Line($"{fieldPath} := {fieldName}_guid.ptr");
                break;

            default:
                throw new NotSupportedException($"Unsupported struct field init kind: {init.Kind}");
        }
    }
}
