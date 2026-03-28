using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

public static class FieldSignatureDecoder
{
    /// <summary>
    /// Cache for loaded external (non-Win32Metadata) assemblies.
    /// Both PEReader and MetadataReader are stored to prevent the PEReader (which owns
    /// the underlying stream) from being garbage collected while the MetadataReader is in use.
    /// </summary>
    private static readonly Dictionary<string, (PEReader PeReader, MetadataReader MetadataReader)> _assemblyReaders = [];

    /// <summary>
    /// List of namespaces of types which we are external to Windows.Win32 and which we don't
    /// want to resolve. These are all WinRT types, the only place this is ever applicable is in
    /// the WinRT Interop Apis
    /// </summary>
    private static readonly string[] excludeNamespaces = ["Windows.UI", "Windows.Foundation", "Windows.Graphics", "Windows.Storage"];

    public static FieldInfo DecodeFieldType(MetadataReader reader, FieldDefinition fieldDef)
    {
        var blob = reader.GetBlobReader(fieldDef.Signature);
        byte header = blob.ReadByte();
        if (header != (byte)SignatureKind.Field)
            throw new BadImageFormatException("Not a field signature");

        return fieldDef.DecodeSignature(new FieldSignatureProvider(reader), new());
    }

    public static FieldInfo DecodeTypeDef(MetadataReader reader, TypeDefinitionHandle tdHandle)
    {
        var td = reader.GetTypeDefinition(tdHandle);
        string typeName = reader.GetString(td.Name);
        string typeNamespace = reader.GetString(td.Namespace);

        if(excludeNamespaces.Any(typeNamespace.StartsWith))
        {
            Program.Logger.LogDebug("Treating Win32 external {Namespace}.{TypeName} as pointer", typeNamespace, typeName);
            return new FieldInfo(SimpleFieldKind.Pointer, typeName, 0, td);
        }
        else if (typeName == "HRESULT")
        {
            return new FieldInfo(SimpleFieldKind.HRESULT, "HRESULT", 0, td, null, reader);
        }
        else if (typeName == "NTSTATUS")
        {
            return new FieldInfo(SimpleFieldKind.NTSTATUS, "NTSTATUS", 0, td, null, reader);
        }
        else if (IsNonHandleNativeTypedef(reader, td))
        {
            return DecodeNativeTypedef(reader, td);
        }
        else if (IsEnum(reader, tdHandle))
        {
            string underlying = GetEnumUnderlyingType(reader, tdHandle);
            return new FieldInfo(SimpleFieldKind.Primitive, underlying);
        }
        else if (IsUsedAsFunctionPointer(reader, tdHandle))
        {
            return new FieldInfo(SimpleFieldKind.Pointer, typeName, 0, td, null, reader);
        }
        else if (IsComInterface(reader, tdHandle))
        {
            return new FieldInfo(SimpleFieldKind.COM, typeName, 0, td, null, reader);
        }

        return new FieldInfo(SimpleFieldKind.Struct, typeName, 0, td, null, reader);
    }

    public static bool IsComInterface(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);

        // All COM interfaces have the Interface flag
        if ((td.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Interface)
            return false;

        // Most (nearly all) COM interfaces have the [Guid] attribute
        if (CustomAttributeDecoder.GetAllNames(reader, td).Contains("GuidAttribute"))
            return true;

        if (!td.BaseType.IsNil)
        {
            // Fallback - check to see if interface derives from IUnknown or IDispatch
            string baseName = td.BaseType.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)td.BaseType).Name),
                HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)td.BaseType).Name),
                _ => throw new NotSupportedException($"Unknown base type while checking for COM: {td.BaseType.Kind}")
            };

            if (baseName == "IUnknown" || baseName == "IDispatch")
                return true;
        }
        else
        {
            // Fallback 2 - If BaseType is Nil and the interface is abstract, it's COM 
            // (base interfaces like IUnknown and caller-supplied interfaces like IOleUILinkInfoW)
            if (td.Attributes.HasFlag(TypeAttributes.Abstract))
                return true;
        }

        return false;
    }

    public static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        var baseHandle = td.BaseType;
        if (baseHandle.Kind == HandleKind.TypeReference)
        {
            var tr = reader.GetTypeReference((TypeReferenceHandle)baseHandle);
            return reader.StringComparer.Equals(tr.Namespace, "System") &&
                   reader.StringComparer.Equals(tr.Name, "Enum");
        }
        return false;
    }

    public static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        foreach (var fieldHandle in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fieldHandle);
            if (reader.StringComparer.Equals(fd.Name, "value__"))
            {
                return fd.DecodeSignature(new FieldSignatureProvider(reader), new()).TypeName;
            }
        }
        return "Int32";
    }

    /// <summary>
    /// Some function pointers (notably the LPFN*PROC callbacks) are represented in the metadata as empty
    /// structs. So when we encounter one we need to check its attributes to see if it's a function pointer
    /// </summary>
    public static bool IsUsedAsFunctionPointer(MetadataReader reader, TypeDefinitionHandle defHandle)
    {
        TypeDefinition typeDef = reader.GetTypeDefinition(defHandle);
        return CustomAttributeDecoder.GetAllNames(reader, typeDef).Contains("UnmanagedFunctionPointerAttribute");
    }

    public static bool IsNonHandleNativeTypedef(MetadataReader mr, TypeDefinition typeDef)
    {
        return CustomAttributeDecoder.GetAllNames(mr, typeDef).Contains("NativeTypedefAttribute")
            && !AhkStruct.TypeIsHandle(mr, typeDef)
            && typeDef.GetFields().Count == 1;
    }

    public static FieldInfo DecodeNativeTypedef(MetadataReader mr, TypeDefinition typeDef)
    {
        FieldDefinition fieldDef = mr.GetFieldDefinition(typeDef.GetFields().First());

        return new FieldInfo(
            SimpleFieldKind.NativeTypedef,
            mr.GetString(typeDef.Name),
            0,
            typeDef,
            fieldDef.DecodeSignature(new FieldSignatureProvider(mr, typeDef), new()),
            mr
        );
    }

    /// <summary>
    /// Resolve a type reference to its type definition
    /// </summary>
    /// <param name="reader">Reader for the type reference</param>
    /// <param name="trHandle">Type reference to resolve</param>
    /// <param name="targetReader">Reader for the resolved type definition. If the resolved type is in 
    ///     a different assembly than the type refernce, this will differ from reader
    /// </param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public static TypeDefinitionHandle ResolveTypeReference(MetadataReader reader, TypeReferenceHandle trHandle, out MetadataReader targetReader)
    {
        var tr = reader.GetTypeReference(trHandle);
        string name = reader.GetString(tr.Name);
        string ns = reader.GetString(tr.Namespace);

        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.ModuleDefinition:
                // type is in this module
                return FindTypeDefinition(reader, ns, name, out targetReader);

            case HandleKind.TypeReference:
                // nested type - resolve parent and then check its nested types
                var parentHandle = (TypeReferenceHandle)tr.ResolutionScope;
                var parentTdHandle = ResolveTypeReference(reader, parentHandle, out targetReader);
                var parentTd = reader.GetTypeDefinition(parentTdHandle);

                foreach (var nestedHandle in parentTd.GetNestedTypes())
                {
                    var nestedTd = reader.GetTypeDefinition(nestedHandle);
                    if (reader.StringComparer.Equals(nestedTd.Name, name))
                    {
                        return nestedHandle;
                    }
                }

                string parentNs = reader.GetString(parentTd.Namespace);
                string parentName = reader.GetString(parentTd.Name);
                throw new NullReferenceException($"Could not resolve reference to '{ns}.{name}' under '{parentNs}.{parentName}'");

            case HandleKind.AssemblyReference:
                AssemblyReference asmRef = reader.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                string asmName = reader.GetString(asmRef.Name);
                MetadataReader extReader = LoadAssemblyReader(asmName);

                return FindTypeDefinition(extReader, ns, name, out targetReader);

            // !!NOTE: ModuleReference is technically possible, not supported (yet). Win32Metadata only has one module

            default:
                throw new NotSupportedException($"Cannot resolve '{ns}.{name}' in resolution scope '{tr.ResolutionScope}'");
        }
    }

    private static MetadataReader LoadAssemblyReader(string assemblyName)
    {
        if (_assemblyReaders.TryGetValue(assemblyName, out var cached))
            return cached.MetadataReader;

        string baseDir = AppContext.BaseDirectory;
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string sdkRoot = Environment.GetEnvironmentVariable("WindowsSdkDir") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                "Windows Kits", "10");

        List<string> probeNames = [
            $"{assemblyName}.dll",
            $"{assemblyName}.winmd",
            Path.Combine(baseDir, $"{assemblyName}.dll"),
            Path.Combine(baseDir, $"{assemblyName}.winmd"),
            Path.Combine(runtimeDir, $"{assemblyName}.dll"),
            Path.Combine(runtimeDir, $"{assemblyName}.winmd"),
            Path.Combine(Program.MetadataDir, assemblyName),
            Path.Combine(Program.MetadataDir, $"{assemblyName}.winmd"),
            Path.Combine(Program.MetadataDir, $"{assemblyName}.dll")
        ];

        // Probe typical Windows SDK metadata locations
        if (Directory.Exists(sdkRoot))
        {
            // Common pattern for Windows 10/11 SDKs
            string refsPath = Path.Combine(sdkRoot, "References");
            if (Directory.Exists(refsPath))
            {
                foreach (var versionDir in Directory.GetDirectories(refsPath))
                {
                    probeNames.Add(Path.Combine(versionDir, $"{assemblyName}.winmd"));

                    string asmDir = Path.Combine(versionDir, assemblyName);
                    if (!Directory.Exists(asmDir))
                        continue;

                    // Search all subdirectories of the version/AssemblyName directory
                    probeNames.Add(Path.Combine(asmDir, $"{assemblyName}.winmd"));
                    foreach(string subDir in Directory.GetDirectories(asmDir, "*", SearchOption.AllDirectories))
                    {
                        probeNames.Add(Path.Combine(subDir, $"{assemblyName}.winmd"));
                    }
                }
            }

            // UnionMetadata folder (used by some SDKs)
            string unionMeta = Path.Combine(sdkRoot, "UnionMetadata");
            if (Directory.Exists(unionMeta))
            {
                probeNames.Add(Path.Combine(unionMeta, $"{assemblyName}.winmd"));
            }
        }

        foreach (var path in probeNames)
        {
            if (!File.Exists(path))
                continue;

            PEReader peReader = new(File.OpenRead(path));
            var reader = peReader.GetMetadataReader();

            _assemblyReaders[assemblyName] = (peReader, reader);

            Program.Logger.LogDebug("Loaded external assembly {AssemblyName} from {Path}", assemblyName, path);
            return reader;
        }

        throw new DllNotFoundException($"Failed to load external assembly '{assemblyName}'");
    }

    /// <summary>
    /// Find a TypeDefinition by namespace and name within an assembly, following exports
    /// if necessary
    /// </summary>
    /// <param name="reader">Reader for the assembly to look in</param>
    /// <param name="ns">Namespace of the TypeDefinition</param>
    /// <param name="name">Name of the TypeDefinition</param>
    /// <param name="targetReader">Output variable where the reader for the assembly where the located
    /// TypeDefinition was actually found. If it was forwarded, this will differ from the input reader
    /// </param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    private static TypeDefinitionHandle FindTypeDefinition(MetadataReader reader, string ns, string name, out MetadataReader targetReader)
    {
        // Try normal type definitions (in the current assembly) first
        foreach (var tdHandle in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(tdHandle);
            if (reader.StringComparer.Equals(td.Name, name) &&
                reader.StringComparer.Equals(td.Namespace, ns))
            {
                targetReader = reader;
                return tdHandle;
            }
        }

        // Next, check for type forwarders (ExportedTypes). There are very few of these, and the JSON generator just hardcodes them
        // https://github.com/marlersoft/win32jsongen/blob/main/Generator/TypeRefDecoder.cs#L72
        // E.g. System.Guid goes netstandard -> System.Runtime -> System.Private.CoreLib
        foreach (var exportedHandle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(exportedHandle);
            if (reader.StringComparer.Equals(exported.Name, name) &&
                reader.StringComparer.Equals(exported.Namespace, ns))
            {
                switch (exported.Implementation.Kind)
                {
                    case HandleKind.AssemblyReference:
                        var targetAsmRef = reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation);
                        var targetAsmName = reader.GetString(targetAsmRef.Name);
                        targetReader = LoadAssemblyReader(targetAsmName);

                        return FindTypeDefinition(targetReader, ns, name, out targetReader);

                    case HandleKind.ExportedType:
                        // Nested forwarded type — follow recursively
                        return FindForwardedTypeRecursive(reader, exported.Implementation, ns, name, out targetReader);
                }
            }
        }

        string? asmName = reader.GetAssemblyDefinition().GetAssemblyName().Name;
        throw new TypeLoadException($"Could not resolve reference to '{ns}.{name}' in assembly '{asmName}'");
    }

    private static TypeDefinitionHandle FindForwardedTypeRecursive(MetadataReader reader, EntityHandle handle, string ns, string name, out MetadataReader targetReader)
    {
        var exported = reader.GetExportedType((ExportedTypeHandle)handle);

        switch (exported.Implementation.Kind)
        {
            case HandleKind.AssemblyReference:
                var targetAsmRef = reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation);
                var targetAsmName = reader.GetString(targetAsmRef.Name);
                targetReader = LoadAssemblyReader(targetAsmName);

                return FindTypeDefinition(targetReader, ns, name, out targetReader);

            case HandleKind.ExportedType:
                return FindForwardedTypeRecursive(reader, exported.Implementation, ns, name, out targetReader);

            default:
                throw new InvalidOperationException($"Invalid type forwarder target: {exported.Implementation.Kind}");
        }
    }
}
