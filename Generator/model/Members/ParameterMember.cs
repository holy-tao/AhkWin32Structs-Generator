namespace AhkWin32.Generator.Model.Members;

using AhkWin32.Generator.Model;

/// <summary>
/// A method parameter. Fully resolved with no metadata dependencies.
/// </summary>
public sealed class ParameterMember
{
    /// <summary>Parameter name (after reserved word deconfliction).</summary>
    public required string Name { get; init; }

    /// <summary>The resolved type of this parameter.</summary>
    public required ResolvedType Type { get; init; }

    /// <summary>1-based sequence number (0 = return value).</summary>
    public required int SequenceNumber { get; init; }

    /// <summary>Direction flags (In, Out, Optional).</summary>
    public ParameterDirection Direction { get; init; }

    /// <summary>Custom parameter attributes (Reserved, Constant, SizedBuffer, etc.).</summary>
    public ParameterFlags Attributes { get; init; }

    /// <summary>
    /// For [IgnoreIfReturn] parameters: the values that indicate the return should be ignored.
    /// Null if not applicable.
    /// </summary>
    public IReadOnlyList<string>? IgnoreIfReturnValues { get; init; }

    /// <summary>
    /// For [RAIIFree] parameters: reference to the free function.
    /// </summary>
    public FreeFuncRef? RAIIFree { get; init; }

    /// <summary>
    /// For [FreeWith] parameters: reference to the free function.
    /// </summary>
    public FreeFuncRef? FreeWith { get; init; }

    /// <summary>
    /// For [MemorySize(BytesParamIndex = N)] parameters: the 0-based index of the
    /// parameter that specifies the buffer size. -1 if not applicable.
    /// </summary>
    public int SizedBufferBytesParamIndex { get; init; } = -1;

    /// <summary>Documentation description for this parameter.</summary>
    public string? Description { get; init; }

    // --- Direction convenience ---
    public bool IsIn => Direction.HasFlag(ParameterDirection.In);
    public bool IsOut => Direction.HasFlag(ParameterDirection.Out);
    public bool IsOptional => Direction.HasFlag(ParameterDirection.Optional);

    // --- Attribute convenience ---
    public bool IsReserved => Attributes.HasFlag(ParameterFlags.Reserved);
    public bool IsConstant => Attributes.HasFlag(ParameterFlags.Constant);
    public bool IsSizedBuffer => Attributes.HasFlag(ParameterFlags.SizedBuffer);
    public bool IsComOutPtr => Attributes.HasFlag(ParameterFlags.ComOutPtr);
    public bool IsRetVal => Attributes.HasFlag(ParameterFlags.RetVal);
    public bool ScriptOwned => !Attributes.HasFlag(ParameterFlags.DoNotRelease);
    public bool HasIgnoreIfReturn => Attributes.HasFlag(ParameterFlags.HasIgnoreIfReturn);
    public bool HasRAIIFreeAttr => Attributes.HasFlag(ParameterFlags.HasRAIIFree);
    public bool HasFreeWithAttr => Attributes.HasFlag(ParameterFlags.HasFreeWith);

    // --- Type-checking convenience (pattern matching on ResolvedType) ---
    public bool IsPtr => Type is PointerType;
    public bool IsPrimitive => Type is PrimitiveType;
    public bool IsHRESULT => Type is HResultType;
    public bool IsNtStatus => Type is NtStatusType;
    public bool IsCom => Type is ComRef;
    public bool IsStruct => Type is StructRef;
    public bool IsHandle => Type is HandleRef;

    public bool IsPtrToPrimitive => Type is PointerType { Pointee: PrimitiveType or PointerType or NativeTypedefType or HResultType or EnumRef or FunctionPointerType };
    public bool IsPtrToCom => Type is PointerType { Pointee: ComRef };
    public bool IsPtrToStruct => Type is PointerType { Pointee: StructRef };
    public bool IsPtrToHandle => Type is PointerType { Pointee: HandleRef };
    public bool IsPtrToString => Type is PointerType { Pointee: StringType };

    /// <summary>
    /// Get the type name from the resolved type (for NativeTypedef, Handle, Struct, COM types).
    /// Returns null for primitive/pointer types without a named referent.
    /// </summary>
    public string? TypeDefName => Type switch
    {
        NativeTypedefType n => n.Name,
        HandleRef h         => h.Name,
        StructRef s         => s.Name,
        ComRef c            => c.Name,
        _                   => null
    };

    /// <summary>Get the pointee type, if this is a pointer.</summary>
    public ResolvedType? Pointee => (Type as PointerType)?.Pointee;
}
