using System.Reflection.Metadata;

namespace AhkWin32.Generator.Model;

/// <summary>
/// A fully-resolved type with no metadata dependencies.
/// Carries all information needed for code generation: display name, DllCall type, width.
/// Replaces FieldInfo + SimpleFieldKind from the legacy model.
/// </summary>
public abstract record ResolvedType
{
    /// <summary>Human-readable name for documentation (e.g., "Integer", "Pointer&lt;RECT&gt;").</summary>
    public abstract string DisplayName { get; }

    /// <summary>AHK DllCall type string (e.g., "int", "ptr", "int*").</summary>
    public abstract string DllCallType { get; }

    /// <summary>
    /// AHK v2.1 type specifier - see https://www.autohotkey.com/docs/alpha/Structs.htm#type-specs
    /// </summary>
    public abstract string TypeSpecifier { get; }

    /// <summary>Size in bytes (64-bit). 0 if not statically known (StructRef, ArrayType).</summary>
    public abstract int Width { get; }
}

/// <summary>
/// A CLR primitive type (Int32, UInt64, Boolean, Single, Double, Byte, SByte, Char, etc.).
/// </summary>
public sealed record PrimitiveType(string Name) : ResolvedType
{
    public override string DisplayName => Name.ToLowerInvariant() switch
    {
        "single" or "double"    => "Float",
        "boolean"               => "Boolean",
        "void"                  => "Void",
        "intptr" or "uintptr"   => "Pointer",
        _                       => "Integer"
    };

    public override string DllCallType => Name.ToLowerInvariant() switch
    {
        "single"                                              => "float",
        "boolean" or "int32"                                  => "int",
        "double"                                              => "double",
        "int64"                                               => "int64",
        "uint32"                                              => "uint",
        "uint64"                                              => "uint",
        "int16"                                               => "short",
        "uint16"                                              => "ushort",
        "byte" or "sbyte" or "char"                           => "char",
        "uintptr" or "intptr" or "void" or "ptr" or "typehandle" => "ptr",
        _                                                     => "ptr" // pointer-sized NativeTypedef
    };

    public override string TypeSpecifier => Name.ToLowerInvariant() switch
    {
        "single"                                              => "Float32",
        "boolean" or "int32"                                  => "Int32",
        "double"                                              => "Float64",
        "int64"                                               => "Int64",
        "uint32"                                              => "UInt32",
        "uint64"                                              => "Int64",   // Ahk doesn't support u64s
        "int16"                                               => "Int16",
        "uint16"                                              => "UInt16",
        "byte" or "sbyte" or "char"                           => "Int8",
        "uchar"                                               => "UInt8",
        "uintptr" or "intptr" or "void" or "ptr" or "typehandle" => "IntPtr",

        // Should not use pointer-sized NativeTypedefs, we should use the typedefs themselves
        _ => throw new NotSupportedException($"Unknown primitive type '{Name}'")
    };

    public override int Width => Name.ToLowerInvariant() switch
    {
        "single" or "boolean" or "int32" or "uint32"                       => 4,
        "double" or "int64" or "uint64" or "intptr" or "uintptr" or "void" or "ptr" => 8,
        "int16" or "uint16" or "char"                                      => 2,
        "byte" or "sbyte"                                                  => 1,
        _                                                                  => 8
    };
}

/// <summary>
/// A pointer to another type. Pointee may be null for void*/opaque pointers.
/// </summary>
public sealed record PointerType(ResolvedType? Pointee) : ResolvedType
{
    public override string DisplayName => Pointee is null
        ? "Pointer"
        : $"Pointer<{Pointee.DisplayName}>";

    public override string DllCallType => "ptr";

    /// <summary>
    /// Gets the DllCall type with asterisk suffix when the pointee is a typed primitive.
    /// Used for method parameters where typed pointer marshalling is desired.
    /// Matches the old GetDllCallType(useNakedPointer: false) behavior.
    /// </summary>
    public string TypedDllCallType => Pointee switch
    {
        PointerType                                                          => "ptr*",
        ComRef                                                               => "ptr*",
        FunctionPointerType                                                  => "ptr*",
        PrimitiveType p when p.Name.Equals("Void", StringComparison.OrdinalIgnoreCase) => "ptr",
        PrimitiveType p                                                      => p.DllCallType + "*",
        NativeTypedefRef n                                                  => n.DllCallType + "*",
        EnumRef e                                                            => e.UnderlyingType.DllCallType + "*",
        HResultType                                                          => "int*",
        _                                                                    => "ptr"
    };

    /// <summary>
    /// Always throw - these are used for function calls but can't be struct members.
    /// </summary>
    public override string TypeSpecifier => throw new InvalidOperationException(
        "PointerTypes cannot be embedded in structures and thus do not have type specifiers");

    public override int Width => 8;
}

/// <summary>
/// A fixed-length array of elements (e.g., BYTE[16]).
/// </summary>
public sealed record ArrayType(ResolvedType ElementType, int Length) : ResolvedType
{
    public override string DisplayName => $"Array<{ElementType.DisplayName}>";
    public override string DllCallType => "ptr";
    public override string TypeSpecifier => $"{ElementType.TypeSpecifier}[{Length}]";
    public override int Width => Length * ElementType.Width;
}

/// <summary>
/// A fixed-length character string buffer (e.g., WCHAR[260]).
/// Distinguished from ArrayType because it uses StrGet/StrPut instead of array proxy.
/// </summary>
public sealed record StringType(int Length, StringEncoding Encoding) : ResolvedType
{
    public override string DisplayName => "String";
    public override string DllCallType => "ptr";
    public override string TypeSpecifier => throw new NotSupportedException(
        "Not supported for v2.1 - Use fixed-size UCHAR or CHAR arrays instead");
    public override int Width => Length * (Encoding == StringEncoding.Ansi ? 1 : 2);
}

/// <summary>
/// Reference to a struct type by FQN. Resolved at emit time via TypeRegistry.
/// Width must be looked up from StructType.Size — accessing it here is an error.
/// </summary>
public sealed record StructRef(string FQN, string Name) : ResolvedType
{
    public override string DisplayName => Name;
    public override string DllCallType => "ptr";
    public override string TypeSpecifier => Name;
    public override int Width => throw new InvalidOperationException(
        $"Width of StructRef '{FQN}' must be resolved from the TypeRegistry");
}

/// <summary>
/// Reference to an enum type. Carries the underlying primitive type for DllCall purposes.
/// </summary>
public sealed record EnumRef(string FQN, string Name, PrimitiveType UnderlyingType) : ResolvedType
{
    public override string DisplayName => Name;
    public override string DllCallType => UnderlyingType.DllCallType;
    public override string TypeSpecifier => Name;
    public override int Width => UnderlyingType.Width;
}

/// <summary>
/// Reference to a COM interface type by FQN.
/// </summary>
public sealed record ComRef(string FQN, string Name) : ResolvedType
{
    public override string DisplayName => Name;
    public override string DllCallType => "ptr";
    public override string TypeSpecifier => Name;
    public override int Width => 8;
}

/// <summary>
/// Reference to a handle type by FQN.
/// </summary>
public sealed record HandleRef(string FQN, string Name) : ResolvedType
{
    public override string DisplayName => Name;
    public override string DllCallType => "ptr";
    public override string TypeSpecifier => Name;
    public override int Width => 8;
}

/// <summary>
/// HRESULT — a 32-bit error code with special semantics.
/// </summary>
public sealed record HResultType() : ResolvedType
{
    public override string DisplayName => "HRESULT";
    public override string DllCallType => "int";
    public override string TypeSpecifier => "HRESULT";
    public override int Width => 4;
}

/// <summary>
/// NTSTATUS — a 32-bit NT status code with special semantics.
/// </summary>
public sealed record NtStatusType() : ResolvedType
{
    public override string DisplayName => "NTSTATUS";
    public override string DllCallType => "int";
    public override string TypeSpecifier => "NTSTATUS";
    public override int Width => 4;
}

/// <summary>
/// A function pointer type (delegates, callbacks).
/// Treated as a pointer in code generation.
/// </summary>
public sealed record FunctionPointerType(string Name, string Signature) : ResolvedType
{
    public override string DisplayName => $"Pointer<{Name}>";
    public override string DllCallType => "ptr";
    public override string TypeSpecifier => "IntPtr";   // TODO we can have function pointer types now!
    public override int Width => 8;
}

/// <summary>
/// A NativeTypedef — a named alias for another type (e.g., DWORD -> UInt32).
/// Carries both the alias name and the underlying resolved type.
/// </summary>
public sealed record NativeTypedefRef(string Name, string FQN, ResolvedType Underlying) : ResolvedType
{
    public override string DisplayName => Name;
    public override string DllCallType => Underlying.DllCallType;
    public override string TypeSpecifier => Name;
    public override int Width => Underlying.Width;
}
