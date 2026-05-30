namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Emits StructType as a complete .ahk file.
/// Port of legacy AhkStruct.ToAhk() and AhkStructMember.ToAhk().
/// Body emission methods are internal static so HandleEmitter can reuse them.
/// </summary>
public sealed class StructEmitter : ITypeEmitter
{
    private readonly TypeRegistry _registry;

    public StructEmitter(TypeRegistry registry)
    {
        _registry = registry;
    }

    public bool CanEmit(Win32Type type) => type is StructType and not HandleType;

    public EmitResult Emit(Win32Type type, string outputRoot)
    {
        var structType = (StructType)type;
        var w = new AhkWriter();

        EmitStruct(w, structType);

        string filePath = ImportResolver.GetFilePath(outputRoot, structType.Namespace, structType.CanonicalName);
        return new EmitResult(w.ToString(), filePath);
    }

    private void EmitStruct(AhkWriter w, StructType structType)
    {
        string pathToBase = ImportResolver.GetPathToBase(structType.Namespace);
        w.Require("AutoHotkey v2.0.0 64-bit");
        w.Include($"{pathToBase}Win32Struct.ahk");

        EmitImports(w, structType);

        w.BlankLine();

        DocCommentWriter.WriteTypeDoc(w, structType);

        using (w.Class(structType.Name, "Win32Struct"))
        {
            w.StaticField("sizeof", structType.Size.ToString());
            w.BlankLine();
            w.StaticField("packingSize", structType.PackingSize.ToString());

            EmitBody(w, structType, 0, [], structType.Name);
        }
    }

    /// <summary>
    /// Emit the body of a struct: nested class definitions, member properties, extensions, __New.
    /// Shared by StructEmitter and HandleEmitter.
    /// </summary>
    internal static void EmitBody(
        AhkWriter w,
        StructType structType,
        int embeddingOffset,
        List<EmittedField> emittedMembers,
        string parentClassName
    )
    {
        // 1. Nested class definitions (non-anonymous, non-Reserved named nested types)
        var nestedClassDefs = structType
            .Members.Where(m => m.IsNested && !m.IsAnonymous && m.Name is not "Reserved")
            .Where(m => m.EmbeddedStruct is not null)
            .Select(m => m.EmbeddedStruct!)
            .DistinctBy(s => s.Name);

        foreach (StructType nested in nestedClassDefs)
        {
            w.BlankLine();
            using (w.Class(nested.Name, "Win32Struct"))
            {
                w.StaticField("sizeof", nested.Size.ToString());
                w.StaticField("packingSize", nested.PackingSize.ToString());

                EmitBody(w, nested, 0, [], $"{parentClassName}.{nested.Name}");
            }
        }

        // 2. Members
        foreach (FieldMember field in structType.Members)
        {
            if (field.IsReserved || field.IsAlignment)
                continue;

            // Flatten anonymous unions
            if (field.IsNested && field.IsAnonymous)
            {
                if (field.EmbeddedStruct is null)
                    throw new InvalidOperationException(
                        $"{structType.Name}.{field.Name} is anonymous but has no EmbeddedStruct"
                    );

                EmitBody(w, field.EmbeddedStruct, field.Offset + embeddingOffset, emittedMembers, parentClassName);
                continue;
            }

            // Skip duplicates
            if (IsDuplicate(field, emittedMembers))
                continue;

            // Name deconfliction
            int suffix = 0;
            while (emittedMembers.Any(e => e.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)))
            {
                field.Name += ++suffix;
            }

            w.BlankLine();
            EmitMember(w, field, field.Offset + embeddingOffset, parentClassName);
            emittedMembers.Add(new EmittedField(field.Name, field.Offset, field.Bitfields, field.IsBitField));
        }

        // 3. Extensions
        EmitExtensions(w, structType);

        // 4. __New for StructSizeField
        if (structType.StructSizeFieldName is not null)
        {
            w.BlankLine();
            w.Line($"__New(ptrOrObj := 0, parent := \"\"){{");
            w.Line($"    super.__New(ptrOrObj, parent)");
            w.Line($"    this.{structType.StructSizeFieldName} := {structType.Size}");
            w.Line("}");
        }
    }

    private static void EmitMember(AhkWriter w, FieldMember field, int offset, string parentClassName)
    {
        switch (field.Type)
        {
            case StructRef when field.EmbeddedStruct is not null:
                EmitEmbeddedTypeMember(
                    w,
                    field,
                    offset,
                    field.IsNested ? $"{parentClassName}.{field.EmbeddedStruct.Name}" : field.EmbeddedStruct.Name
                );
                break;
            case StructRef { FQN: "System.Guid" }:
                // System.Guid has no embedded StructType; it maps to the Guid.ahk fixture.
                EmitEmbeddedTypeMember(w, field, offset, "Guid");
                break;
            case HandleRef handleRef:
                // Handle-typed fields use lazy-init pattern (handles are struct types in metadata).
                // Handles are always top-level types, so no parent qualification needed.
                EmitEmbeddedTypeMember(w, field, offset, handleRef.Name);
                break;
            case ArrayType:
                EmitArrayMember(w, field, offset, parentClassName);
                break;
            case StringType:
                EmitStringMember(w, field, offset);
                break;
            case EnumRef enumRef:
                EmitNumericMember(w, field, offset, enumRef.UnderlyingType.DllCallType);
                break;
            case NativeTypedefRef nativeTypedef:
                EmitNumericMember(w, field, offset, nativeTypedef.Underlying.DllCallType);
                break;
            default:
                // PrimitiveType, PointerType, ComRef, HResultType, NtStatusType, FunctionPointerType
                EmitNumericMember(w, field, offset, field.Type.DllCallType);
                break;
        }
    }

    private static void EmitNumericMember(AhkWriter w, FieldMember field, int offset, string dllCallType)
    {
        DocCommentWriter.WriteFieldDoc(w, field);

        using (w.InstanceProperty(field.Name))
        {
            w.Line($"get => NumGet(this, {offset}, \"{dllCallType}\")");
            w.Line($"set => NumPut(\"{dllCallType}\", value, this, {offset})");
        }

        if (field.IsBitField)
            EmitBitfieldMembers(w, field);
    }

    private static void EmitBitfieldMembers(AhkWriter w, FieldMember field)
    {
        foreach (BitfieldMember bf in field.Bitfields)
        {
            if (bf.Name is "Reserved")
                continue;

            w.BlankLine();

            // Look up description from field's embedded docs if available
            // Bitfield members don't have individual descriptions in FieldMember,
            // so we pass null for description
            DocCommentWriter.WriteBitfieldDoc(w, field, bf, null);

            long mask = (1L << (int)bf.Length) - 1;

            using (w.InstanceProperty(bf.Name))
            {
                w.Line($"get => (this.{field.Name} >> {bf.Offset}) & 0x{mask:X}");
                w.Line(
                    $"set => this.{field.Name} := ((value & 0x{mask:X}) << {bf.Offset}) | (this.{field.Name} & ~(0x{mask:X} << {bf.Offset}))"
                );
            }
        }
    }

    private static void EmitEmbeddedTypeMember(AhkWriter w, FieldMember field, int offset, string qualifiedName)
    {
        DocCommentWriter.WriteFieldDoc(w, field);

        using (w.InstanceProperty(field.Name))
        {
            using (w.GetBlock())
            {
                w.Line($"if(!this.HasProp(\"__{field.Name}\"))");
                w.Line($"    this.__{field.Name} := {qualifiedName}({offset}, this)");
                w.Line($"return this.__{field.Name}");
            }
        }
    }

    private static void EmitArrayMember(AhkWriter w, FieldMember field, int offset, string parentClassName)
    {
        var arrayType = (ArrayType)field.Type;
        ResolvedType elementType = arrayType.ElementType;

        // Determine AHK element type and DllCall type for Win32FixedArray
        string ahkElementType;
        string dllCallType;

        switch (elementType)
        {
            case StructRef structRef:
                dllCallType = "";
                ahkElementType = field.IsNested ? $"{parentClassName}.{structRef.Name}" : structRef.Name;
                break;
            case NativeTypedefRef nativeTypedef:
                ahkElementType = "Primitive";
                dllCallType = nativeTypedef.Underlying.DllCallType;
                break;
            case EnumRef enumRef:
                ahkElementType = "Primitive";
                dllCallType = enumRef.UnderlyingType.DllCallType;
                break;
            default:
                // PrimitiveType, PointerType, ComRef, HandleRef, HResultType, NtStatusType, FunctionPointerType
                ahkElementType = "Primitive";
                dllCallType = elementType.DllCallType;
                break;
        }

        DocCommentWriter.WriteFieldDoc(w, field);

        using (w.InstanceProperty(field.Name))
        {
            using (w.GetBlock())
            {
                w.Line($"if(!this.HasProp(\"__{field.Name}ProxyArray\"))");
                w.Line(
                    $"    this.__{field.Name}ProxyArray := Win32FixedArray(this.ptr + {offset}, {arrayType.Length}, {ahkElementType}, \"{dllCallType}\")"
                );
                w.Line($"return this.__{field.Name}ProxyArray");
            }
        }
    }

    private static void EmitStringMember(AhkWriter w, FieldMember field, int offset)
    {
        var stringType = (StringType)field.Type;
        string encoding = stringType.Encoding == StringEncoding.Ansi ? "UTF-8" : "UTF-16";

        DocCommentWriter.WriteFieldDoc(w, field);

        using (w.InstanceProperty(field.Name))
        {
            w.Line($"get => StrGet(this.ptr + {offset}, {stringType.Length - 1}, \"{encoding}\")");
            w.Line($"set => StrPut(value, this.ptr + {offset}, {stringType.Length - 1}, \"{encoding}\")");
        }
    }

    internal void EmitImports(AhkWriter w, Win32Type type)
    {
        // Restrict to targets available in the registry to filter out nested types.
        // System.Guid isn't in the registry (it's the Guid.ahk fixture at the projection
        // root) but resolves to a valid include path — let it through.
        IEnumerable<string> imports = type
            .Imports.GetIncludeTargets()
            .Where(fqn => fqn == "System.Guid" || _registry.Contains(fqn));

        foreach (string import in imports)
        {
            w.Include(ImportResolver.GetIncludePath(type.Namespace, import));
        }
    }

    internal static void EmitExtensions(AhkWriter w, Win32Type type)
    {
        if (type.Extensions.Count == 0)
            return;

        foreach (var ext in type.Extensions)
        {
            if (!ext.CodeByVersion.TryGetValue(AhkVersion.v20, out string? rawCode))
                continue;

            string code = rawCode
                .Replace("$Class", type.Name)
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

    // --- Duplicate detection ---

    private static bool IsDuplicate(FieldMember field, List<EmittedField> emitted)
    {
        foreach (var existing in emitted)
        {
            if (field.IsBitField && existing.IsBitField)
            {
                // Bitfield fields are duplicates if they back the same bitfield list
                if (field.Bitfields.SequenceEqual(existing.Bitfields))
                    return true;
            }
            else
            {
                // Non-bitfield fields are duplicates if same offset + same name
                if (
                    field.Offset == existing.Offset
                    && field.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tracks emitted fields for duplicate detection and name deconfliction.
    /// </summary>
    internal record EmittedField(string Name, int Offset, IReadOnlyList<BitfieldMember> Bitfields, bool IsBitField);
}
