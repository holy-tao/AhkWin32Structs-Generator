namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits ComInterfaceType as a complete .ahk file.
/// Port of legacy AhkComInterface.ToAhk().
/// </summary>
public sealed class ComInterfaceEmitter(TypeRegistry registry, AhkVersion version) : ITypeEmitter
{
    private readonly TypeRegistry _registry = registry;

    private readonly AhkVersion _version = version;

    public bool CanEmit(Win32Type type) => type is ComInterfaceType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var comType = (ComInterfaceType)type;
        var w = new AhkWriter(_version);

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
        using (w.Class(comType.Name, baseClass))
        {
            w.BlankLine();

            EmitStaticCode(w, comType);

            w.BlankLine();

            EmitVTableNames(w, comType);

            foreach (var prop in comType.Properties)
            {
                w.BlankLine();
                EmitProperty(w, prop);
            }

            foreach (var method in comType.Methods)
            {
                w.BlankLine();
                MethodEmitter.EmitComMethod(w, method, _registry, _version);
            }

            StructEmitter.EmitExtensions(w, comType);
        }
    }

    private void EmitHeaders(AhkWriter w, ComInterfaceType comType)
    {
        string pathToBase = ImportResolver.GetPathToBase(comType.Namespace);
        if (_version is AhkVersion.v21)
        {
            w.Require("AutoHotkey v2.1-alpha+ 64-bit");
            w.Import($"{pathToBase}Win32ComInterface.ahk", ["Win32ComInterface"]);
            w.Import($"{pathToBase}Guid.ahk", ["Guid"]);
        }
        else
        {
            w.Require("AutoHotkey v2.0.0 64-bit");
            w.Include($"{pathToBase}Win32ComInterface.ahk");
            w.Include($"{pathToBase}Guid.ahk");
        }
        
        EmitImports(w, comType);
    }

    private void EmitImports(AhkWriter w, ComInterfaceType comType)
    {
        if (_version is AhkVersion.v21)
        {
            foreach (string fqn in comType.Imports.GetTypes().Where(f => f != comType.FQN))
            {
                string path = ImportResolver.GetIncludePath(comType.Namespace, fqn);
                w.Import(path, [ImportResolver.GetImportName(fqn)]);
            }

            foreach (string apisFqn in comType.Imports.GetFunctionNamespaces())
            {
                string path = ImportResolver.GetIncludePath(comType.Namespace, apisFqn);
                w.Import(path, comType.Imports.GetFunctionsForNamespace(apisFqn));
            }
        }
        else
        {
            foreach (string fqn in comType.Imports.GetIncludeTargets().Where(f => f != comType.FQN))
            {
                w.Include(ImportResolver.GetIncludePath(comType.Namespace, fqn));
            }
        }
    }

    /// <summary>
    /// Emit static sizeof, IID, CLSID, and vTableOffset.
    /// Port of AhkComInterface.AppendStaticCode.
    /// </summary>
    private static void EmitStaticCode(AhkWriter w, ComInterfaceType comType)
    {
        w.StaticField("sizeof", "A_PtrSize");

        if (comType.IID is { } iid)
        {
            w.Line("/**");
            w.Line($" * The interface identifier for {comType.Name}");
            w.Line(" * @type {Guid}");
            w.Line(" */");
            w.StaticField("IID", $"Guid(\"{{{iid}}}\")");
        }

        if (comType.CLSID is { } clsid)
        {
            w.BlankLine();
            w.Line("/**");
            w.Line($" * The class identifier for {comType.Name.TrimStart('I')}");
            w.Line(" * @type {Guid}");
            w.Line(" */");
            w.StaticField("CLSID", $"Guid(\"{{{clsid}}}\")");
        }

        w.BlankLine();
        w.Line("/**");
        w.Line(" * The offset into the COM object's virtual function table at which this interface's methods begin.");
        w.Line(" * @type {Integer}");
        w.Line(" */");
        w.StaticField("vTableOffset", comType.VTableOffset.ToString());
    }

    /// <summary>
    /// Emit VTableNames array.
    /// Port of AhkComInterface.AppendVTableList.
    /// </summary>
    private static void EmitVTableNames(AhkWriter w, ComInterfaceType comType)
    {
        w.Line("/**");
        w.Line(" * @readonly used when implementing interfaces to order function pointers");
        w.Line(" * @type {Array<String>}");
        w.Line(" */");

        string names = string.Join(", ", comType.Methods.Select(m => $"\"{m.DeduplicatedName}\""));
        w.StaticField("VTableNames", $"[{names}]");
    }

    /// <summary>
    /// Emit a single COM property (documentation + get/set block).
    /// Port of AhkComProperty.ToAhk.
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
}
