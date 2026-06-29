namespace AhkWin32.Generator.Model;

/// <summary>
/// A pre-decoded constant value. All values are fully resolved from metadata blobs
/// during extraction — no BlobReader or MetadataReader access needed at emit time.
/// </summary>
public abstract record ConstantValue
{
    /// <summary>The AHK-formatted string representation of this value.</summary>
    public abstract string AsAhk { get; }

    /// <summary>The AHK type name for documentation (e.g., "Integer", "Float", "String").</summary>
    public abstract string AhkTypeName { get; }
}

/// <summary>
/// A primitive constant (integer, float, string, char, byte).
/// The value is pre-formatted as an AHK literal string.
/// </summary>
public sealed record PrimitiveConstantValue(string FormattedValue, string TypeName) : ConstantValue
{
    public override string AsAhk => FormattedValue;
    public override string AhkTypeName => TypeName;
}

/// <summary>
/// A GUID constant value.
/// </summary>
public sealed record GuidConstantValue(Guid Value) : ConstantValue
{
    public override string AsAhk => $"Guid(\"{{{Value:D}}}\")";
    public override string AhkTypeName => "Guid";
}

/// <summary>
/// A struct constant value with initialization data.
/// For handle constants: stores the handle value directly.
/// For other struct constants: stores field initialization sequence decoded from [ConstantAttribute].
/// </summary>
public sealed record StructConstantValue(
    /// <summary>Name of the struct type.</summary>
    string StructName,
    /// <summary>FQN of the struct type.</summary>
    string StructFQN,
    /// <summary>Whether the struct is a handle type.</summary>
    bool IsHandle,
    /// <summary>The raw handle value (for handle constants), or null for non-handles.</summary>
    string? HandleValue,
    /// <summary>
    /// Ordered initialization data for struct fields, decoded from [ConstantAttribute].
    /// Each entry is a pre-formatted AHK value string.
    /// Null for handle constants (which use HandleValue instead).
    /// </summary>
    IReadOnlyList<StructFieldInit>? FieldInits
) : ConstantValue
{
    public override string AsAhk => IsHandle ? $"{StructName}({{Value: {HandleValue}}}, false)" : StructName;

    public override string AhkTypeName => StructName;
}

/// <summary>
/// A single field initialization entry for a struct constant.
/// </summary>
public sealed record StructFieldInit(
    /// <summary>Path to the field (e.g., ["subStruct", "field"]).</summary>
    IReadOnlyList<string> FieldPath,
    /// <summary>Pre-formatted AHK value string.</summary>
    string Value,
    /// <summary>The kind of initialization.</summary>
    StructFieldInitKind Kind,
    /// <summary>For array fields: the 1-based index.</summary>
    int? ArrayIndex = null,
    /// <summary>For guid pointer fields: the GUID value.</summary>
    Guid? GuidValue = null
);

/// <summary>
/// Kind of struct field initialization.
/// </summary>
public enum StructFieldInitKind
{
    /// <summary>Simple assignment: prefix.field := value</summary>
    Direct,

    /// <summary>Array element: prefix.field[index] := value</summary>
    ArrayElement,

    /// <summary> A GUID embedded in the struct.</summary>
    Guid,

    /// <summary>GUID pointer: create static guid, assign .ptr</summary>
    GuidPointer,
}
