namespace AhkWin32.Generator.Emit.Emitters;

using System.Runtime.InteropServices.Swift;
using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a HandleType as a v2.1 native `struct` block. Handles are emitted as a
/// single-field struct with `__value` get/set so the instance is transparently
/// usable as the underlying integer/pointer in DllCall and assignment.
///
/// `Free()` is emitted from <see cref="HandleType.FreeFunc"/> metadata but is NOT
/// wired into `__delete` - auto-cleanup is the caller's responsibility for now.
/// </summary>
public sealed class HandleEmitter21 : ITypeEmitter
{
    public bool CanEmit(Win32Type type) => type is HandleType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var handleType = (HandleType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitHandle(w, handleType);

        string filePath = ImportResolver.GetFilePath(outputRoot, handleType.Namespace, handleType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private static void EmitHandle(AhkWriter w, HandleType handleType)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");

        SingleFieldEmitter.EmitImports(w, handleType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, handleType);

        FieldMember valueField = handleType.Members.Single();
        long firstInvalid = handleType.InvalidValues.FirstOrDefault(0);

        using (w.Struct(handleType.Name))
        {
            w.Line($"{valueField.Name} : {valueField.Type.TypeSpecifier}");

            w.BlankLine();
            SingleFieldEmitter.EmitValueSetter(
                w,
                handleType,
                valueField.Name,
                handleType.ValueGetterExpr,
                handleType.ValueSetterCoerceExpr
            );

            w.BlankLine();
            w.Line("/**");
            w.Line(" * The list of values which indicate that the handle is invalid");
            w.Line(" * @type {Array<Integer>}");
            w.Line(" */");
            w.StaticField("invalidValues", $"[{string.Join(", ", handleType.InvalidValues)}]");

            w.BlankLine();
            using (w.InstanceMethod("__New", $"{valueField.Name} := {firstInvalid}"))
            {
                w.Line($"this.{valueField.Name} := {valueField.Name}");
            }

            // Extension code blocks (e.g. helper methods from metadata/extensions/*.yml)
            StructEmitter21.EmitExtensions(w, handleType);

            // If we have a free function, emit a Free() method, an `Owned` subclass that
            // calls it automatically in __Delete, an `OwnedWith()` factory for handles
            // freed by a context-specific function, and an `Adopt()` method that converts
            // a handle to an owned handle.
            if (handleType.FreeFunc is not null)
            {
                // "handle is still valid" guard, shared by Free() and __FreeWith(). Treats 0 as
                // invalid in addition to the metadata-declared invalid values, making free
                // idempotent (so a moved-from handle is a no-op).
                string validGuard = string.Join(
                    " && ",
                    handleType.InvalidValues.Append(0).Distinct().Select(val => $"this.{valueField.Name} != {val}")
                );

                w.BlankLine();
                using (w.InstanceMethod("Free", ""))
                {
                    w.Line("; Do nothing if the handle is invalid already");
                    using (w.If(validGuard))
                    {
                        w.Line($"{handleType.FreeFunc.Name}(this.{valueField.Name})");
                        w.Line($"this.{valueField.Name} := {firstInvalid}");
                    }
                }

                w.BlankLine();
                w.Line("/**");
                w.Line($" * A `{handleType.Name}` which is owned by the script and which frees itself");
                w.Line(" * in `__Delete`.");
                w.Line(" */");
                using (w.Struct("Owned", handleType.Name))
                {
                    using (w.InstanceMethod("__Delete", ""))
                    {
                        w.Line("this.Free()");
                    }
                }

                // Only handles actually returned with a context-specific (divergent) RAIIFree need
                // the OwnedWith factory; gating keeps it off the many handles that never use it.
                if (handleType.NeedsOwnedWith)
                {
                    w.BlankLine();
                    w.Line("/**");
                    w.Line(" * Frees this handle using a caller-supplied function rather than the default.");
                    w.Line(" * Used by `OwnedWith` for handles returned with a context-specific RAIIFree.");
                    w.Line(" * @param {Func} freeFunc called with the raw handle value");
                    w.Line(" */");
                    using (w.InstanceMethod("__FreeWith", "freeFunc"))
                    {
                        using (w.If(validGuard))
                        {
                            w.Line($"freeFunc(this.{valueField.Name})");
                            w.Line($"this.{valueField.Name} := {firstInvalid}");
                        }
                    }

                    w.BlankLine();
                    w.Line("/**");
                    w.Line($" * Returns a cached `{handleType.Name}.Owned` subclass whose `Free()` calls `freeFunc`");
                    w.Line(" * instead of the default. Used for handles returned with a context-specific");
                    w.Line(" * RAIIFree (e.g. a HANDLE that must be closed with FindClose, not CloseHandle).");
                    w.Line(" * @param {Func} freeFunc called with the raw handle value to free it");
                    w.Line(" * @returns {Class} a subclass of {@link " + handleType.Name + ".Owned}");
                    w.Line(" */");
                    using (w.StaticMethod("OwnedWith", "freeFunc"))
                    {
                        w.Line("static cache := Map()");
                        using (w.If("cache.Has(freeFunc)"))
                        {
                            w.Line("return cache[freeFunc]");
                        }
                        w.Line($"cls := Class({handleType.Name}.Owned)");
                        w.Line("DefineProp(cls.Prototype, \"Free\", { Call: (self) => self.__FreeWith(freeFunc) })");
                        w.Line("return cache[freeFunc] := cls");
                    }
                }

                w.BlankLine();
                w.Line("/**");
                w.Line($" * Takes ownership of this {handleType.Name}, returning an owned handle that frees");
                w.Line(" * itself when it falls out of scope. This is a *move*: the original handle is");
                w.Line(" * invalidated so the underlying resource has exactly one owner.");
                w.Line($" * @returns {{{handleType.Name}.Owned}}");
                w.Line(" */");
                using (w.InstanceMethod("Adopt", ""))
                {
                    using (w.If($"this is {handleType.Name}.Owned"))
                    {
                        w.Line($"throw TypeError(\"Cannot adopt an owned {handleType.Name}\", -1)");
                    }
                    w.Line($"owned := {handleType.Name}.Owned(this.{valueField.Name})");
                    w.Line($"this.{valueField.Name} := {firstInvalid}");
                    w.Line("return owned");
                }
            }
        }
    }
}
