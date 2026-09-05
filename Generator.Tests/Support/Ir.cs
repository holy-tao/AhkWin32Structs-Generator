namespace AhkWin32.Generator.Tests.Support;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;

/// <summary>
/// Terse builders for the IR model types used in tests. The real Model types use
/// <c>required init</c> properties, so constructing them inline is noisy; these helpers fill
/// the boilerplate (identity, names, layout) and let a test express only what it cares about.
/// </summary>
internal static class Ir
{
    /// <summary>Build a <see cref="StructType"/> from an FQN and an ordered list of members.</summary>
    public static StructType Struct(string fqn, params FieldMember[] members) => Struct(fqn, Architecture.All, members);

    /// <summary>Build a <see cref="StructType"/> for a specific architecture variant.</summary>
    public static StructType Struct(string fqn, Architecture arch, params FieldMember[] members)
    {
        string name = TailName(fqn);
        return new StructType
        {
            Identity = new TypeIdentity(fqn, arch),
            Name = name,
            CanonicalName = name,
            AssemblyName = "Test.Assembly",
            MetadataVersion = "Test.Assembly v1.0.0",
            Size = 0,
            PackingSize = 0,
            LayoutKind = StructLayoutKind.Sequential,
            Members = members,
            IsNested = false,
        };
    }

    /// <summary>
    /// Build a scalar <see cref="FieldMember"/> of the given resolved type. Size is left at 0 —
    /// the transforms under test key off <see cref="FieldMember.Type"/>, not byte sizes, and some
    /// resolved types (StructRef) deliberately throw on <c>Width</c>.
    /// </summary>
    public static FieldMember Field(string name, ResolvedType type) =>
        new()
        {
            Name = name,
            Offset = 0,
            Size = 0,
            Type = type,
        };

    /// <summary>
    /// Build a field that embeds a (nested, unregistered) struct by value. Mirrors how the
    /// extractor inlines anonymous/nested structs via <see cref="FieldMember.EmbeddedStruct"/>.
    /// </summary>
    public static FieldMember EmbeddedField(string name, StructType embedded) =>
        new()
        {
            Name = name,
            Offset = 0,
            Size = 0,
            Type = StructRefTo(embedded.FQN),
            EmbeddedStruct = embedded,
        };

    /// <summary>An <see cref="ApiType"/> carrying the given methods (no constants).</summary>
    public static ApiType Api(string fqn, params MethodMember[] methods)
    {
        string name = TailName(fqn);
        return new ApiType
        {
            Identity = TypeIdentity.Universal(fqn),
            Name = name,
            CanonicalName = name,
            AssemblyName = "Test.Assembly",
            MetadataVersion = "Test.Assembly v1.0.0",
            Constants = [],
            Methods = [.. methods],
        };
    }

    /// <summary>A minimal <see cref="MethodMember"/> with the given name and a single Void return slot.</summary>
    public static MethodMember Method(string name, string ns, params ParameterMember[] parameters)
    {
        ParameterMember[] all =
        [
            new ParameterMember
            {
                Name = "returnValue",
                Type = Prim("Void"),
                SequenceNumber = 0,
            },
            .. parameters,
        ];
        return new MethodMember
        {
            Name = name,
            Namespace = ns,
            Parameters = all,
        };
    }

    /// <summary>A method parameter (1-based sequence numbers are assigned by position here).</summary>
    public static ParameterMember Param(string name, ResolvedType type, int sequence = 1) =>
        new()
        {
            Name = name,
            Type = type,
            SequenceNumber = sequence,
        };

    /// <summary>
    /// Build an <see cref="EnumType"/> from an FQN and constant names. Values are assigned by
    /// position — the transforms under test key off names, not values.
    /// </summary>
    public static EnumType Enum(string fqn, params string[] constantNames)
    {
        string name = TailName(fqn);
        return new EnumType
        {
            Identity = TypeIdentity.Universal(fqn),
            Name = name,
            CanonicalName = name,
            AssemblyName = "Test.Assembly",
            MetadataVersion = "Test.Assembly v1.0.0",
            Constants = [.. constantNames.Select((n, i) => Const(n, i))],
            IsFlags = false,
            UnderlyingTypeName = "Int32",
        };
    }

    /// <summary>A minimal Int32 <see cref="ConstantMember"/>.</summary>
    public static ConstantMember Const(string name, int value) =>
        new()
        {
            Name = name,
            Value = new PrimitiveConstantValue(value.ToString(), "Integer (Int32)"),
            Type = Prim("Int32"),
        };

    public static PrimitiveType Prim(string name) => new(name);

    public static PointerType Ptr(ResolvedType? pointee) => new(pointee);

    public static StructRef StructRefTo(string fqn) => new(fqn, TailName(fqn));

    public static ArrayType ArrayOf(ResolvedType element, int length) => new(element, length);

    private static string TailName(string fqn) => fqn.Contains('.') ? fqn[(fqn.LastIndexOf('.') + 1)..] : fqn;
}
