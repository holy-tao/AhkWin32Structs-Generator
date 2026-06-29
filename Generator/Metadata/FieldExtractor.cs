namespace AhkWin32.Generator.Metadata;

using System.Reflection;
using System.Reflection.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Result of extracting fields from a struct, including computed layout.
/// </summary>
public sealed record StructLayout(IReadOnlyList<FieldMember> Fields, int TotalSize, int PackingSize);

/// <summary>
/// Extracts fields from a TypeDefinition, decodes their signatures via SignatureDecoder,
/// and computes struct layout (offset, padding, alignment, total size).
/// </summary>
public sealed class FieldExtractor
{
    private readonly MetadataLoader _loader;
    private readonly ILogger _logger;

    /// <summary>
    /// Callback to recursively extract a referenced struct type.
    /// Provided by TypeExtractor to avoid circular dependencies.
    /// Parameters: (MetadataReader, TypeDefinition, isAnsi) → StructType?
    /// </summary>
    private readonly Func<MetadataReader, TypeDefinition, bool, StructType?> _extractStructCallback;

    public FieldExtractor(
        MetadataLoader loader,
        ILogger logger,
        Func<MetadataReader, TypeDefinition, bool, StructType?> extractStructCallback
    )
    {
        _loader = loader;
        _logger = logger;
        _extractStructCallback = extractStructCallback;
    }

    /// <summary>
    /// Extract all fields from a TypeDefinition and compute struct layout.
    /// Port of AhkStructFactory constructor layout algorithm.
    /// </summary>
    public StructLayout ExtractFields(
        MetadataReader reader,
        TypeDefinition typeDef,
        StructLayoutKind layoutKind,
        int packingSize,
        bool isUnion,
        bool isAnsi,
        string parentFQN,
        Dictionary<string, string>? apiFields = null
    )
    {
        List<FieldMember> members = [];
        int offset = 0;
        int maxAlignment = 1;

        foreach (FieldDefinitionHandle hField in typeDef.GetFields())
        {
            FieldDefinition fieldDef = reader.GetFieldDefinition(hField);
            string fieldName = reader.GetString(fieldDef.Name);

            // Decode signature with parent as resolution context for nested types
            var sigDecoder = new SignatureDecoder(reader, _loader, _logger, typeDef);
            ResolvedType resolvedType = fieldDef.DecodeSignature(sigDecoder, new SignatureGenericContext());

            // Decode field attributes
            FieldAttrs fieldAttrs = AttributeReader.DecodeFieldAttributes(reader, fieldDef);

            // Resolve embedded struct if applicable
            StructType? embeddedStruct = null;
            bool isNested = false;
            (resolvedType, embeddedStruct, isNested) = ResolveEmbeddedStruct(
                reader,
                typeDef,
                resolvedType,
                fieldName,
                isAnsi
            );

            // Compute field size
            int fieldSize = ComputeFieldSize(resolvedType, embeddedStruct, isAnsi);

            // Compute logical field size for alignment calculation
            int logicalFieldSize = ComputeLogicalFieldSize(resolvedType, embeddedStruct, fieldSize, isAnsi);

            // Alignment calculation
            int alignment = Math.Min(logicalFieldSize, packingSize);
            maxAlignment = Math.Max(maxAlignment, alignment);
            int padding = (alignment - (offset % alignment)) % alignment;
            offset += padding;

            // Set member offset
            int memberOffset = layoutKind == StructLayoutKind.Explicit ? fieldDef.GetOffset() : offset;

            // For unions, all fields overlap at offset 0 — don't advance
            if (!isUnion)
                offset += fieldSize;

            // Compute field-level flags
            MemberFlags fieldFlags = ComputeFieldFlags(fieldAttrs, fieldName, resolvedType, embeddedStruct);

            // Get documentation
            string? description = null;
            apiFields?.TryGetValue(fieldName, out description);

            FieldMember member = new()
            {
                Name = fieldName,
                Offset = memberOffset,
                Size = fieldSize,
                Type = resolvedType,
                Flags = fieldFlags,
                Description = description,
                DeprecationMessage = fieldAttrs.DeprecationMessage,
                EmbeddedStruct = embeddedStruct,
                Bitfields = fieldAttrs.Bitfields ?? [],
                IsNested = isNested,
            };

            members.Add(member);

            _logger.LogTrace(
                "Extracting field {ParentFQN}.{FieldName}: {ResolvedType} at offset {Offset} (size {Size})",
                parentFQN,
                fieldName,
                resolvedType.DisplayName,
                memberOffset,
                fieldSize
            );
            _logger.LogTrace(
                "Layout: alignment={Alignment}, padding={Padding}, logicalSize={LogicalSize}",
                alignment,
                padding,
                logicalFieldSize
            );
        }

        // Compute total size
        int totalSize = isUnion ? (members.Count > 0 ? members.Max(m => m.Size) : 0) : offset;

        // Cap packing size to max alignment seen
        packingSize = Math.Min(packingSize, maxAlignment);

        // Tail padding (based on totalSize, not running offset)
        int tailPadding = (maxAlignment - (totalSize % maxAlignment)) % maxAlignment;
        totalSize += tailPadding;

        _logger.LogDebug(
            "Computed layout for {FQN}: {FieldCount} fields, {TotalSize} bytes, packing {PackingSize}",
            parentFQN,
            members.Count,
            totalSize,
            packingSize
        );

        return new StructLayout(members, totalSize, packingSize);
    }

    /// <summary>
    /// Resolve embedded struct references. Returns updated (resolvedType, embeddedStruct, isNested).
    /// Port of AhkStructMember constructor logic for Struct and Array fields.
    /// </summary>
    private (ResolvedType Type, StructType? Embedded, bool IsNested) ResolveEmbeddedStruct(
        MetadataReader reader,
        TypeDefinition parentTypeDef,
        ResolvedType resolvedType,
        string fieldName,
        bool isAnsi
    )
    {
        if (resolvedType is StructRef structRef)
        {
            return ResolveStructRefField(reader, parentTypeDef, structRef, fieldName, isAnsi);
        }

        if (resolvedType is ArrayType arrayType && arrayType.ElementType is StructRef arrayStructRef)
        {
            var (_, embedded, isNested) = ResolveStructRefField(
                reader,
                parentTypeDef,
                arrayStructRef,
                fieldName,
                isAnsi
            );
            if (embedded != null)
            {
                // Keep the ArrayType but with the embedded struct for size info
                return (resolvedType, embedded, isNested);
            }
            // If the struct ref was converted to pointer, update the array element type
            return (new ArrayType(new PointerType(null), arrayType.Length), null, false);
        }

        return (resolvedType, null, false);
    }

    /// <summary>
    /// Resolve a StructRef field — either build the embedded struct or convert to pointer.
    /// </summary>
    private (ResolvedType Type, StructType? Embedded, bool IsNested) ResolveStructRefField(
        MetadataReader reader,
        TypeDefinition parentTypeDef,
        StructRef structRef,
        string fieldName,
        bool isAnsi
    )
    {
        // System.Guid is the one external type we don't treat as opaque — it maps to the
        // hand-written Guid.ahk fixture at the projection root. It has no TypeDefinition in
        // the Win32 metadata, so skip the lookup below (which would fail and fall back to a
        // pointer) and keep the StructRef. Size/alignment are handled in ComputeFieldSize /
        // ComputeLogicalFieldSize; the emitters render it as the embedded `Guid` type.
        if (structRef.FQN == "System.Guid")
            return (structRef, null, false);

        // We need to find the actual TypeDefinition to check if it's nested and get its namespace
        // The StructRef.FQN gives us the namespace.name but we need the TypeDefinition for nested check
        // Pass parentTypeDef so nested types (e.g. _Anonymous_e__Struct) are resolved within
        // the correct parent, not just the first match in the global namespace.
        TypeDefinition? fieldTypeDef = FindTypeDefByFqn(reader, structRef.FQN, parentTypeDef);

        if (fieldTypeDef == null)
        {
            // Can't find the type definition — treat as pointer
            _logger.LogTrace(
                "Converting non-resolvable type {FQN} to pointer for field {FieldName}",
                structRef.FQN,
                fieldName
            );
            return (new PointerType(null), null, false);
        }

        bool isNested = fieldTypeDef.Value.IsNested;
        string fieldTypeNamespace = reader.GetString(fieldTypeDef.Value.Namespace);

        // If not nested and not in Windows.Win32, convert to pointer
        if (!isNested && !fieldTypeNamespace.StartsWith("Windows.Win32"))
        {
            _logger.LogTrace("Converting non-Win32 embedded type {FQN} to pointer", structRef.FQN);
            return (new PointerType(null), null, false);
        }

        // Recursively extract the embedded struct
        StructType? embedded = _extractStructCallback(reader, fieldTypeDef.Value, isAnsi);
        if (embedded == null)
        {
            _logger.LogTrace("Embedded struct extraction returned null for {FQN}, treating as pointer", structRef.FQN);
            return (new PointerType(null), null, false);
        }

        _logger.LogTrace(
            "Embedded struct {FieldName}: {EmbeddedFQN} (size {Size}, packing {PackingSize})",
            fieldName,
            structRef.FQN,
            embedded.Size,
            embedded.PackingSize
        );

        return (structRef, embedded, isNested);
    }

    /// <summary>
    /// Try to find a TypeDefinition by FQN within the current reader.
    /// </summary>
    private static TypeDefinition? FindTypeDefByFqn(MetadataReader reader, string fqn, TypeDefinition? parent = null)
    {
        int lastDot = fqn.LastIndexOf('.');
        if (lastDot < 0)
            return null;

        string ns = fqn[..lastDot];
        string name = fqn[(lastDot + 1)..];

        // Check parent context for nested types first if parent is passed
        foreach (TypeDefinitionHandle tdHandle in parent?.GetNestedTypes() ?? [])
        {
            TypeDefinition td = reader.GetTypeDefinition(tdHandle);
            if (reader.StringComparer.Equals(td.Name, name))
            {
                return td;
            }
        }

        // Check all type definitions in the reader
        foreach (TypeDefinitionHandle tdHandle in reader.TypeDefinitions)
        {
            TypeDefinition td = reader.GetTypeDefinition(tdHandle);
            if (reader.StringComparer.Equals(td.Name, name) && reader.StringComparer.Equals(td.Namespace, ns))
            {
                return td;
            }
        }

        return null;
    }

    /// <summary>
    /// Compute the actual byte size of a field.
    /// </summary>
    private static int ComputeFieldSize(ResolvedType type, StructType? embeddedStruct, bool isAnsi)
    {
        return type switch
        {
            StructRef when embeddedStruct != null => embeddedStruct.Size,
            // System.Guid has no embedded StructType (it's the Guid.ahk fixture) — 16 bytes.
            StructRef { FQN: "System.Guid" } => 16,
            ArrayType a when embeddedStruct != null => a.Length * embeddedStruct.Size,
            ArrayType a => a.Width,
            StringType s => s.Width,
            _ => type.Width,
        };
    }

    /// <summary>
    /// Compute the logical field size used for alignment calculation.
    /// This determines how the field aligns within the struct.
    /// </summary>
    private static int ComputeLogicalFieldSize(
        ResolvedType type,
        StructType? embeddedStruct,
        int fieldSize,
        bool isAnsi
    )
    {
        return type switch
        {
            // Array = element width (for struct arrays, use element's packing size)
            ArrayType { ElementType: StructRef } when embeddedStruct != null => embeddedStruct.PackingSize,
            ArrayType a => a.ElementType.Width,

            // String = character width
            StringType => isAnsi ? 1 : 2,

            // Embedded struct = packing size of the embedded struct
            StructRef when embeddedStruct != null => embeddedStruct.PackingSize,

            // System.Guid aligns to 4 (its largest member is the 4-byte Data1), matching
            // the real Win32 GUID and the Guid.ahk fixture.
            StructRef { FQN: "System.Guid" } => 4,

            // Everything else = field size
            _ => fieldSize,
        };
    }

    /// <summary>
    /// Compute MemberFlags for a field from its attributes and naming conventions.
    /// Port of AhkStructMember.GetFlags.
    /// </summary>
    private static MemberFlags ComputeFieldFlags(
        FieldAttrs attrs,
        string fieldName,
        ResolvedType resolvedType,
        StructType? embeddedStruct
    )
    {
        MemberFlags flags = attrs.Flags; // Already has Deprecated, Reserved, NativeBitField

        if (fieldName.StartsWith("___MISSING_ALIGNMENT__"))
            flags |= MemberFlags.Alignment;

        // Check type name for union/anonymous conventions
        string typeName = resolvedType switch
        {
            StructRef s => s.Name,
            ArrayType { ElementType: StructRef s } => s.Name,
            _ => "",
        };

        if (typeName.EndsWith("_e__Union") || (embeddedStruct?.IsUnion ?? false))
            flags |= MemberFlags.Union;

        if (typeName.StartsWith("_Anonymous"))
            flags |= MemberFlags.Anonymous;

        return flags;
    }
}
