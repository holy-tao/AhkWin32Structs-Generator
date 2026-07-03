namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a ComInterfaceType as a v2.1 native <c>struct</c>:
/// <list type="bullet">
///   <item><description>Outer struct holds the vtable pointer at offset 0 (inherited from
///   <c>Win32ComInterface</c>'s <c>vtbl : IntPtr</c>), retyped per-interface via a
///   trailing <c>DefineProp</c> call so <c>this.vtbl.MethodName</c> is typed.</description></item>
///
///   <item><description>Nested <c>struct Vtbl extends Base.Vtbl { fn : IntPtr ... }</c> carries
///   the function-pointer layout via struct inheritance; no <c>VTableNames</c> array or
///   <c>vTableOffset</c> integer is emitted.</description></item>
///
///   <item><description>Generated <c>Implement</c> and <c>Dispose</c> chain via <c>super</c>
///   and wire callbacks with statically-known parameter counts. IUnknown opts out: its
///   <c>Implement</c>/<c>Dispose</c> live in its extension YAML, where the optional-override
///   logic for QI/AddRef/Release is hand-coded.</description></item>
///
///   <item><description></description></item>
/// </list>
/// </summary>
public sealed class ComInterfaceEmitter21(TypeRegistry registry) : ITypeEmitter
{
    private readonly TypeRegistry _registry = registry;

    public bool CanEmit(Win32Type type) => type is ComInterfaceType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var comType = (ComInterfaceType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitComInterface(w, comType);

        string filePath = ImportResolver.GetFilePath(outputRoot, comType.Namespace, comType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private void EmitComInterface(AhkWriter w, ComInterfaceType comType)
    {
        EmitHeaders(w, comType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, comType);

        string baseClass = comType.BaseInterfaceName ?? "Win32ComInterface";
        using (w.Struct(comType.Name, baseClass))
        {
            EmitStaticIdentifiers(w, comType);
            EmitStaticNew(w, comType);

            // Nested Vtbl struct - one IntPtr per method on THIS interface.
            // Parent methods come via `extends Base.Vtbl`.
            EmitVtblStruct(w, comType);

            EmitNew(w, comType);

            // Property accessors
            foreach (var prop in comType.Properties)
            {
                w.BlankLine();
                EmitProperty(w, prop);
            }

            // Method wrappers (ComCall by absolute vtable index)
            foreach (var method in comType.Methods)
            {
                w.BlankLine();
                MethodEmitter.EmitComMethod(w, method, _registry, AhkVersion.v21);
            }

            EmitQuery(w, comType);

            // IUnknown's Implement/Dispose handle optional QI/AddRef/Release overrides and
            // live in IUnknown.yml. Every other interface gets generated implementations.
            if (comType.BaseInterfaceFQN is not null)
            {
                w.BlankLine();
                EmitImplement(w, comType);

                w.BlankLine();
                EmitDispose(w, comType);
            }

            StructEmitter21.EmitExtensions(w, comType);
        }
    }

    /// <summary>
    /// Retype the inherited `vtbl` field on this prototype so `this.vtbl.MethodName`
    ///is typed against THIS interface's Vtbl rather than Win32ComInterface's IntPtr.
    /// </summary>
    private static void EmitStaticNew(AhkWriter w, ComInterfaceType comType)
    {
        w.BlankLine();
        using (w.StaticMethod("__New", ""))
        {
            w.Line("; Retype our prototype's vtable pointer to be our vtbl's type");
            w.Line("DefineProp(this.Prototype, 'vtbl', { type: this.Vtbl.Ptr, offset: 0 })");
            w.Line("this.DeleteProp(\"__New\")");
        }
    }

    /// <summary>
    /// Emit the instance constructor. This allocates the vtable and shells out to the
    /// base constructor.
    /// </summary>
    /// <param name="w"></param>
    /// <param name="comType"></param>
    private static void EmitNew(AhkWriter w, ComInterfaceType comType)
    {
        w.BlankLine();
        using (w.InstanceMethod("__New", "implObj := 0, flags := \"\""))
        {
            // Read offset 0 via the intrinsic backing address: `this` marshals as the
            // overridden `Ptr` (comPtr), which is still 0 this early in construction.
            using (w.If("NumGet(ObjGetDataPtr(this), 0, \"ptr\") == 0"))
            {
                w.Line($"this.vtbl := {comType.Name}.Vtbl()");
            }
            w.Line($"super.__New(implObj, flags)");
        }
    }

    private static void EmitQuery(AhkWriter w, ComInterfaceType comType)
    {
        w.BlankLine();
        using (w.InstanceMethod("Query", "iid"))
        {
            using (w.If($"{comType.Name}.IID.Equals(iid)"))
            {
                w.Line("return true");
            }
            w.Line("return super.Query(iid)");
        }
    }

    private static void EmitHeaders(AhkWriter w, ComInterfaceType comType)
    {
        w.Require("AutoHotkey v2.1-alpha.30+ 64-bit");

        // The COM-runtime fixtures (Win32ComInterface, Win32Struct, Guid) live at the
        // module root (the "Windows" folder-module directory) regardless of namespace;
        // emit explicit imports rather than routing through the per-FQN ImportResolver paths.
        string pathToModuleRoot = ImportResolver.GetPathToModuleRoot(comType.Namespace);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        WriteImport(w, $"{pathToModuleRoot}Win32ComInterface.ahk", "Win32ComInterface", seen);
        WriteImport(w, $"{pathToModuleRoot}Guid.ahk", "Guid", seen);

        foreach (string fqn in comType.Imports.GetTypes().Where(f => f != comType.FQN))
        {
            string path = ImportResolver.GetIncludePath(comType.Namespace, fqn, moduleRelative: true);
            WriteImport(w, path, ImportResolver.GetImportName(fqn), seen);
        }

        foreach (string apisFqn in comType.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(comType.Namespace, apisFqn, moduleRelative: true);
            w.Import(path, comType.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }

    private static void WriteImport(AhkWriter w, string path, string name, HashSet<string> seen)
    {
        if (!seen.Add(name))
            return;
        w.Import(path, [name]);
    }

    /// <summary>Emit static IID and optional CLSID fields.</summary>
    private static void EmitStaticIdentifiers(AhkWriter w, ComInterfaceType comType)
    {
        if (comType.IID is { } iid)
        {
            w.Line("/**");
            w.Line($" * The interface identifier for {comType.Name}");
            w.Line(" * @type {Guid}");
            w.Line(" */");
            w.Line($"static IID := Guid(\"{{{iid}}}\")");
        }

        if (comType.CLSID is { } clsid)
        {
            w.BlankLine();
            w.Line("/**");
            w.Line($" * The class identifier for {comType.Name.TrimStart('I')}");
            w.Line(" * @type {Guid}");
            w.Line(" */");
            w.Line($"static CLSID := Guid(\"{{{clsid}}}\")");
        }
    }

    /// <summary>
    /// Emit the nested <c>struct Vtbl { ... }</c>. For root interfaces (IUnknown) no base
    /// is used; otherwise <c>extends Base.Vtbl</c> threads the parent's slots in declaration
    /// order at the start of the vtable.
    /// </summary>
    private static void EmitVtblStruct(AhkWriter w, ComInterfaceType comType)
    {
        w.BlankLine();

        string? vtblBase = comType.BaseInterfaceName is { } baseName ? $"{baseName}.Vtbl" : null;
        int align = comType.Methods.Any() ? comType.Methods.Max(m => m.DeduplicatedName.Length) : 0;

        w.Line("/**");
        w.Line(" * The {@link https://devblogs.microsoft.com/oldnewthing/20040205-00/?p=40733 Virtual Function Table}");
        w.Line($" * used for {comType.Name} interfaces");
        w.Line("*/");

        using (w.Struct("Vtbl", vtblBase))
        {
            foreach (var method in comType.Methods)
            {
                w.Line($"{method.DeduplicatedName.PadRight(align)} : IntPtr");
            }
        }
    }

    /// <summary>
    /// Emit a single COM property (documentation + get/set block).
    /// </summary>
    private static void EmitProperty(AhkWriter w, ComPropertyMember prop)
    {
        DocCommentWriter.WritePropertyDoc(w, prop);
        using (w.InstanceProperty(prop.Name))
        {
            if (prop.Getter is not null)
                w.Line($"get => this.{prop.Getter.DeduplicatedName}()");

            if (prop.Setter is not null)
                w.Line($"set => this.{prop.Setter.DeduplicatedName}(value)");
        }
    }

    /// <summary>
    /// Emit <c>Implement(implObj, flags := "")</c>: chain to super, then wire each
    /// method on this interface via <c>CallbackCreate</c> with the COM-ABI parameter
    /// count (declared params + 1 for the C++ this pointer).
    /// </summary>
    private static void EmitImplement(AhkWriter w, ComInterfaceType comType)
    {
        using (w.InstanceMethod("Implement", "implObj, flags := \"\""))
        {
            w.Line("super.Implement(implObj, flags)");
            foreach (ComMethodMember method in comType.Methods)
            {
                // C++ ABI passes `this` + every declared parameter. Parameters[0] is the
                // return-value slot in the IR, so Parameters.Count = 1 (return) + N (declared),
                // which is exactly the cppThis + N callback args we need.
                int paramCount = method.Parameters.Count;
                w.Line(
                    $"this.vtbl.{method.DeduplicatedName} := CallbackCreate("
                        + $"GetMethod(implObj, \"{method.DeduplicatedName}\"), flags, {paramCount})"
                );
            }
        }
    }

    /// <summary>
    /// Emit <c>Dispose()</c>: chain to super, then <c>CallbackFree</c> each callback this
    /// interface installed.
    /// </summary>
    private static void EmitDispose(AhkWriter w, ComInterfaceType comType)
    {
        using (w.InstanceMethod("Dispose", ""))
        {
            using (w.If("!this.owned"))
            {
                w.Line("throw MethodError(\"Cannot dispose of an unowned interface\", -1, this)");
            }

            w.Line("super.Dispose()");
            foreach (var method in comType.Methods)
            {
                w.Line($"CallbackFree(this.vtbl.{method.DeduplicatedName})");
            }
        }
    }
}
