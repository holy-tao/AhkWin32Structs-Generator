namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits a StructType as a v2.1 native `struct` block. Fields are typed properties
/// (`name : TypeSpecifier`) when their metadata offset matches natural layout; fields
/// whose offsets diverge (anonymous-union overlaps, padding gaps) are emitted as
/// DefineProp calls on the prototype with explicit `offset:`.
/// </summary>
public sealed class StructEmitter21(TypeRegistry registry) : ITypeEmitter
{
    private readonly TypeRegistry _registry = registry;

    public bool CanEmit(Win32Type type) => type is StructType and not HandleType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var structType = (StructType)type;
        var w = new AhkWriter(AhkVersion.v21);

        EmitStruct(w, structType);

        string filePath = ImportResolver.GetFilePath(outputRoot, structType.Namespace, structType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private void EmitStruct(AhkWriter w, StructType structType)
    {
        w.Require("AutoHotkey v2.1-alpha.26+ 64-bit");

        EmitImports(w, structType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, structType);

        var deferred = new List<DeferredProp>();
        using (w.Struct(structType.Name))
        {
            w.Line($"#StructPack {structType.PackingSize}");
            w.BlankLine();
            EmitBody(w, structType, structType.Name, deferred);
        }

        w.BlankLine();

        // DefineProp calls for fields whose offsets diverge from natural layout
        foreach (DeferredProp d in deferred)
        {
            w.Line($"DefineProp({d.QualifiedClass}.Prototype, '{d.Name}', {{type: {d.TypeExpr}, offset: {d.Offset}}})");
        }
    }

    /// <summary>
    /// Emit the body of a struct: nested struct definitions, typed property fields,
    /// bit accessors, struct-size init, and extension blocks.
    /// </summary>
    internal void EmitBody(AhkWriter w, StructType structType, string parentClassName,
        List<DeferredProp> deferred)
    {
        // 1. Nested non-anonymous struct definitions (referenced by member fields below)
        var nestedClassDefs = structType.Members
            .Where(m => m.IsNested && !m.IsAnonymous && m.Name is not "Reserved")
            .Where(m => m.EmbeddedStruct is not null)
            .Select(m => m.EmbeddedStruct!)
            .DistinctBy(s => s.Name);

        foreach (StructType nested in nestedClassDefs)
        {
            w.BlankLine();
            using (w.Struct(nested.Name))
            {
                EmitBody(w, nested, $"{parentClassName}.{nested.Name}", deferred);
            }
        }

        // 2. Field properties - track the natural-layout cursor to detect overlaps.
        int cursor = 0;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EmitFields(w, structType, structType.Members, parentClassName, deferred, ref cursor, emitted, embeddingOffset: 0);

        // 3. Extensions
        EmitExtensions(w, structType);

        // Struct-size init is handled by the typed-property initializer (`:= this.Size`)
        // emitted at the size field above; no separate __New() needed.
    }

    /// <summary>
    /// Walk the field list emitting typed properties; recurse into anonymous nested
    /// structs/unions to flatten them into the parent's namespace.
    /// </summary>
    private void EmitFields(AhkWriter w, StructType owner, IReadOnlyList<FieldMember> fields,
        string parentClassName, List<DeferredProp> deferred,
        ref int cursor, HashSet<string> emitted, int embeddingOffset)
    {
        foreach (FieldMember field in fields)
        {
            int absOffset = field.Offset + embeddingOffset;

            // Anonymous nested struct/union - flatten into parent
            if (field.IsNested && field.IsAnonymous)
            {
                if (field.EmbeddedStruct is null)
                    throw new InvalidOperationException(
                        $"{owner.Name}.{field.Name} is anonymous but has no EmbeddedStruct");

                EmitFields(w, owner, field.EmbeddedStruct.Members, parentClassName, deferred,
                    ref cursor, emitted, absOffset);
                continue;
            }

            // Reserved / alignment fields participate in declaration-order layout under
            // v2.1 and so MUST be emitted; auto-layout + #StructPack handles their offsets.

            // Name deconfliction against already-emitted fields
            string name = field.Name;
            int suffix = 0;
            while (emitted.Contains(name))
                name = field.Name + ++suffix;
            field.Name = name;

            string typeExpr = GetTypeExpression(field, parentClassName);

            // Forward fields (offset >= cursor) become typed properties in the body;
            // overlap fields (offset < cursor) become DefineProp calls on the prototype.
            // Auto-layout + #StructPack handles natural alignment padding, so a gap
            // between cursor and absOffset is fine.
            if (absOffset >= cursor)
            {
                DocCommentWriter.WriteFieldDoc(w, field, AhkVersion.v21);
                if (owner.StructSizeFieldName == name)
                {
                    w.Line($"{name} : {typeExpr} := this.Size");
                }
                else
                {
                    w.Line($"{name} : {typeExpr}");
                }

                cursor = absOffset + field.Size;
                w.BlankLine();
            }
            else
            {
                deferred.Add(new DeferredProp(parentClassName, name, typeExpr, absOffset));
            }

            emitted.Add(name);

            // Bitfield accessors are dynamic properties regardless of how the backing was emitted
            if (field.IsBitField)
                EmitBitfieldAccessors(w, field);
        }
    }

    /// <summary>
    /// Build the AHK type expression for a field's typed-property declaration.
    /// </summary>
    private string GetTypeExpression(FieldMember field, string parentClassName)
    {
        return field.Type switch
        {
            // String fields (CHAR[N]/WCHAR[N]) - IR collapses these to StringType,
            // re-expand to typedef-element arrays so the named CHAR/WCHAR survives.
            StringType s => $"{(s.Encoding == StringEncoding.Ansi ? "CHAR" : "WCHAR")}[{s.Length}]",

            // Array of nested-defined struct: qualify the element name
            ArrayType { ElementType: StructRef es } a when field.IsNested
                => $"{parentClassName}.{es.Name}[{a.Length}]",

            // Bitfields - backing field uses a primitive type derived from its size
            _ when field.IsBitField => BitfieldBackingTypeSpecifier(field.Size),

            // Nested struct ref defined inline in the parent: qualify the name
            StructRef sr when field.IsNested => $"{parentClassName}.{sr.Name}",

            // Anything else: TypeSpecifier already produces the right token
            _ => field.Type.TypeSpecifier
        };
    }

    private static string BitfieldBackingTypeSpecifier(int sizeBytes) => sizeBytes switch
    {
        1 => "Int8",
        2 => "Int16",
        4 => "Int32",
        8 => "Int64",
        _ => throw new InvalidOperationException($"Unsupported bitfield backing size: {sizeBytes} bytes")
    };

    private static void EmitBitfieldAccessors(AhkWriter w, FieldMember field)
    {
        foreach (BitfieldMember bf in field.Bitfields)
        {
            if (bf.Name is "Reserved")
                continue;

            w.BlankLine();
            DocCommentWriter.WriteBitfieldDoc(w, field, bf, null);

            long mask = (1L << (int)bf.Length) - 1;

            using (w.InstanceProperty(bf.Name))
            {
                w.Line($"get => (this.{field.Name} >> {bf.Offset}) & 0x{mask:X}");
                w.Line($"set => this.{field.Name} := ((value & 0x{mask:X}) << {bf.Offset}) | (this.{field.Name} & ~(0x{mask:X} << {bf.Offset}))");
            }
        }
    }

    internal void EmitImports(AhkWriter w, Win32Type type)
    {
        // Restrict to types available in the registry to filter out nested types
        foreach (string fqn in type.Imports.GetTypes()
            .Where(fqn => _registry.Contains(fqn) && fqn != type.FQN))
        {
            string path = ImportResolver.GetIncludePath(type.Namespace, fqn);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }

        foreach (string apisFqn in type.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(type.Namespace, apisFqn);
            w.Import(path, type.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }

    internal static void EmitExtensions(AhkWriter w, Win32Type type)
    {
        if (type.Extensions.Count == 0) return;

        foreach (var ext in type.Extensions)
        {
            string code = ext.Code.Replace("$Class", type.Name)
                .Replace("$Namespace", type.Namespace)
                .Replace("$Arch", type.Arch.ToString());
            if (type is ComInterfaceType iface)
            {
                code = code.Replace("$CLSID", iface.CLSID?.ToString());
                code = code.Replace("$IID", iface.IID?.ToString());
            }

            string indentStr = w.CurrentIndent;
            string indented = indentStr + code.Replace("\n", "\n" + indentStr);
            w.RawLine(indented);
        }
    }

    /// <summary>
    /// A typed-property field whose metadata offset doesn't match natural layout
    /// and must be emitted as a DefineProp call after the struct body.
    /// </summary>
    internal record DeferredProp(string QualifiedClass, string Name, string TypeExpr, int Offset);
}
