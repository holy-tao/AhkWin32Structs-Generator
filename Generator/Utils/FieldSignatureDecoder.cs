using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public static class FieldSignatureDecoder
{
    /// <summary>
    /// Cache for loaded external (non-Win32Metadata) assemblies
    /// </summary>
    private static readonly Dictionary<string, MetadataReader> _assemblyReaders = [];

    private static readonly Dictionary<(string ns, string name), (MetadataReader reader, TypeDefinitionHandle handle)> _typeCache = [];

    private static readonly TraceSource traceSource = new("AssemblyLoader");

    public static FieldInfo DecodeFieldType(MetadataReader reader, FieldDefinition fieldDef)
    {
        var blob = reader.GetBlobReader(fieldDef.Signature);
        byte header = blob.ReadByte();
        if (header != (byte)SignatureKind.Field)
            throw new BadImageFormatException("Not a field signature");

        return fieldDef.DecodeSignature(new FieldSignatureProvider(reader), new());
    }

    public static FieldInfo DecodeTypeDef(MetadataReader reader, TypeDefinitionHandle handle) =>
        DecodeTypeDef(reader, reader.GetTypeDefinition(handle));
    
    /// <summary>
    /// Decodes a TypeDefinition into a FieldInfo usable for AHK code generation
    /// </summary>
    /// <param name="reader">Metadata reader for tdHandle's assembly</param>
    /// <param name="tdHandle">Handle to the TypeDefinition to decoded</param>
    /// <returns>The decoded TypeDefinition</returns>
    /// <exception cref="TypeAccessException">If the type cannot be decoded</exception>
    public static FieldInfo DecodeTypeDef(MetadataReader reader, TypeDefinition td)
    {
        string typeName = reader.GetString(td.Name).Split('`').First();
        string typeNamespace = reader.GetString(td.Namespace);
        
        if(NetTypeMappings.TryGetMappedType($"{typeNamespace}.{typeName}", out var mappedType))
        {
            return DecodeTypeDef(mappedType.Value.reader, mappedType.Value.handle);
        }

        if (!string.IsNullOrWhiteSpace(typeNamespace) && !typeNamespace.StartsWith("Windows") && typeName is not "Guid")
        {
            // Non-Windows type that isn't accounted for - not necessarily a fatal error, but we should log it
            string? asmName = reader.GetAssemblyDefinition().GetAssemblyName().Name;
            Trace.TraceWarning($"Unexpected non-Windows type {asmName}!{typeNamespace}.{typeName} - generation may fail or produce incorrect results");
            Trace.TraceWarning("If possible, this type should be mapped to a Win32 or WinRT type in type-mappings.yml");
        }

        else if (typeName == "HRESULT")
        {
            return new FieldInfo(SimpleFieldKind.HRESULT, "HRESULT", 0, td, null, reader);
        }
        else if (IsNonHandleNativeTypedef(reader, td))
        {
            return DecodeNativeTypedef(reader, td);
        }
        else if (IsEnum(reader, td))
        {
            string underlying = GetEnumUnderlyingType(reader, td);
            return new FieldInfo(SimpleFieldKind.Primitive, underlying);
        }
        else if (IsUsedAsFunctionPointer(reader, td))
        {
            return new FieldInfo(SimpleFieldKind.Pointer, typeName, 0, td, null, reader);
        }
        else if (IsInterface(td))
        {
            return new FieldInfo(SimpleFieldKind.COM, typeName, 0, td, null, reader);
        }
        else if (IsStruct(reader, td))
        {
            return new FieldInfo(SimpleFieldKind.Struct, typeName, 0, td, null, reader);
        }
        else if (IsClass(reader, td))
        {
            return new FieldInfo(SimpleFieldKind.Class, typeName, 0, td, null, reader); 
        }

        throw new TypeAccessException($"Could not decode {typeNamespace}.{typeName}");
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

    public static bool IsInterface(TypeDefinition td) => (td.Attributes & TypeAttributes.Interface) != 0;

    public static bool IsEnum(MetadataReader reader, TypeDefinition td)
    {
        if (IsInterface(td)) 
            return false;
        if (td.Attributes.HasFlag(TypeAttributes.ExplicitLayout) || td.Attributes.HasFlag(TypeAttributes.SequentialLayout))
            return false;

        // Check base type
        if(td.BaseType.IsNil == false && td.BaseType.Kind is HandleKind.TypeReference && 
            reader.GetString(reader.GetTypeReference((TypeReferenceHandle)td.BaseType).Name) is "Enum")
            return true;

        // Fall back to heuristic - single field with name "value__"
        var fields = td.GetFields();
        return fields.Count == 1 && reader.GetString(reader.GetFieldDefinition(fields.Single()).Name) is "value__";
    }

    public static bool IsStruct(MetadataReader reader, TypeDefinition td) =>
        !IsInterface(td) &&
        !IsEnum(reader, td) && 
        (td.Attributes.HasFlag(TypeAttributes.ExplicitLayout) || td.Attributes.HasFlag(TypeAttributes.SequentialLayout));

    public static bool IsClass(MetadataReader reader, TypeDefinition td) =>
        !IsInterface(td) &&
        !IsEnum(reader, td) &&
        !IsStruct(reader, td);

    public static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinitionHandle handle)
        => GetEnumUnderlyingType(reader, reader.GetTypeDefinition(handle));

    public static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinition td)
    {
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

    public static bool IsUsedAsFunctionPointer(MetadataReader reader, TypeDefinitionHandle defHandle) =>
        IsUsedAsFunctionPointer(reader, reader.GetTypeDefinition(defHandle));

    /// <summary>
    /// Some function pointers (notably the LPFN*PROC callbacks) are represented in the metadata as empty
    /// structs. So when we encounter one we need to check its attributes to see if it's a function pointer
    /// </summary>
    public static bool IsUsedAsFunctionPointer(MetadataReader reader, TypeDefinition typeDef)
    {
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
            mr.GetString(typeDef.Name).Split('`').First(),
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
    /// <exception cref="TypeLoadException">If the type cannot be found</exception>
    public static TypeDefinitionHandle ResolveTypeReference(MetadataReader reader, TypeReferenceHandle trHandle, out MetadataReader targetReader)
    {
        var tr = reader.GetTypeReference(trHandle);
        string name = reader.GetString(tr.Name);
        string ns = reader.GetString(tr.Namespace);

        if(_typeCache.TryGetValue((ns, name), out var cached))
        {
            targetReader = cached.reader;
            return cached.handle;
        }

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
                        _typeCache[(ns, name)] = (targetReader, nestedHandle);
                        return nestedHandle;
                    }
                }

                string parentNs = reader.GetString(parentTd.Namespace);
                string parentName = reader.GetString(parentTd.Name);
                throw new TypeLoadException($"Could not resolve reference to '{ns}.{name}' under '{parentNs}.{parentName}'");

            case HandleKind.AssemblyReference:
                AssemblyReference asmRef = reader.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                string asmName = reader.GetString(asmRef.Name);
                MetadataReader extReader = LoadAssemblyReader(asmName);

                return FindTypeDefinition(extReader, ns, name, out targetReader);

            // !!NOTE: ModuleReference is technically possible, not supported (yet). Win32Metadata only has one module

            default:
                throw new TypeLoadException($"Cannot resolve '{ns}.{name}' in resolution scope '{tr.ResolutionScope}'");
        }
    }

    public static (MetadataReader reader, TypeDefinition typeDef) ResolveTypeReference(MetadataReader reader, TypeReferenceHandle trHandle)
    {
        TypeDefinitionHandle hFound = ResolveTypeReference(reader, trHandle, out var foundReader);
        return (foundReader, reader.GetTypeDefinition(hFound));
    }

    /// <summary>
    /// Tries to load a MetadataReader for the given assembly
    /// </summary>
    /// <param name="assemblyName">Name of the assembly to load - "Windows.Wdk", "mscorlib", "System.InteropServices", etc</param>
    /// <returns></returns>
    /// <exception cref="DllNotFoundException">If the assembly cannot be found</exception>
    public static MetadataReader LoadAssemblyReader(string assemblyName)
    {
        assemblyName = assemblyName.TrimEnd(".winmd").TrimEnd(".dll");
        if (_assemblyReaders.TryGetValue(assemblyName, out MetadataReader? cached))
            return cached;

        string baseDir = AppContext.BaseDirectory;

        List<string> probeNames = [
            $"{assemblyName}.dll",
            $"{assemblyName}.winmd",
            Path.Combine(Program.MetadataDir, $"{assemblyName}.winmd"),
            Path.Combine(Program.MetadataDir, $"{assemblyName}.dll"),
            Path.Combine(baseDir, $"{assemblyName}.dll"),
            Path.Combine(baseDir, $"{assemblyName}.winmd")
        ];

        // Probe typical Windows SDK metadata locations
        string sdkRoot = Environment.GetEnvironmentVariable("WindowsSdkDir") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                "Windows Kits", "10");
        if (Directory.Exists(sdkRoot))
        {
            // Common pattern for Windows 10/11 SDKs
            string refsPath = Path.Combine(sdkRoot, "References");
            if (Directory.Exists(refsPath))
            {
                foreach (var versionDir in Directory.GetDirectories(refsPath))
                {
                    probeNames.Add(Path.Combine(versionDir, $"{assemblyName}.winmd"));
                    probeNames.Add(Path.Combine(versionDir, $"{assemblyName}.dll"));

                    string asmDir = Path.Combine(versionDir, assemblyName);
                    if (!Directory.Exists(asmDir))
                        continue;

                    // Search all subdirectories of the version/AssemblyName directory
                    probeNames.Add(Path.Combine(asmDir, $"{assemblyName}.winmd"));
                    probeNames.Add(Path.Combine(asmDir, $"{assemblyName}.dll"));
                    foreach(string subDir in Directory.GetDirectories(asmDir, "*", SearchOption.AllDirectories))
                    {
                        probeNames.Add(Path.Combine(subDir, $"{assemblyName}.winmd"));
                        probeNames.Add(Path.Combine(subDir, $"{assemblyName}.dll"));
                    }
                }
            }

            // UnionMetadata folder (used by some SDKs)
            string unionMeta = Path.Combine(sdkRoot, "UnionMetadata");
            if (Directory.Exists(unionMeta))
            {
                probeNames.Add(Path.Combine(unionMeta, $"{assemblyName}.winmd"));
                probeNames.Add(Path.Combine(unionMeta, $"{assemblyName}.dll"));
            }
        }
        else
        {
            Trace.TraceWarning($"Failed to find the Windows SDK root - checked {sdkRoot}");
        }

        // Probe the global assembly cache - some WinRT assemblies forward .NET types here
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string gacBase = Path.Combine(windir, "Microsoft.NET", "assembly");
        if (Directory.Exists(gacBase))
        {
            foreach(string subdir in new string[]{"GAC_64", "GAC_MSIL"} /* Exclude 32-bit assemblies (GAC_32)*/)
            {
                IEnumerable<string> asmDirs = Directory.GetDirectories(Path.Join(gacBase, subdir))
                    .Where(dir => (Path.GetFileName(dir)?.Equals(assemblyName, StringComparison.InvariantCultureIgnoreCase)) ?? false);

                foreach(string asmDir in asmDirs)
                {
                    // Find the most recent version directory
                    string mostRecentVersionDir = Directory.GetDirectories(asmDir)
                        .OrderByDescending(dir => Directory.GetLastWriteTimeUtc(Path.Combine(asmDir, dir)))
                        .FirstOrDefault(string.Empty);
                    if (!string.IsNullOrWhiteSpace(mostRecentVersionDir))
                    {
                        probeNames.Add(Path.Combine(mostRecentVersionDir, $"{assemblyName}.winmd"));
                        probeNames.Add(Path.Combine(mostRecentVersionDir, $"{assemblyName}.dll"));
                    }
                }
            }
        }
        else
        {
            Trace.TraceWarning($"Failed to find the Global Assembly Cache - checked {gacBase}");            
        }

        traceSource.TraceEvent(TraceEventType.Verbose, 1, $"Probing {probeNames.Count} paths for assembly:");
        probeNames.ForEach(name =>traceSource.TraceEvent(TraceEventType.Verbose, 1, $"\t{name}"));

        string found = probeNames.FirstOrDefault(File.Exists, string.Empty);

        if (!string.IsNullOrWhiteSpace(found))
        {
            PEReader peReader = new(File.OpenRead(found));
            var reader = peReader.GetMetadataReader();

            _assemblyReaders[assemblyName.TrimEnd(".winmd").TrimEnd(".dll")] = reader;

            Trace.TraceInformation($"Loaded assembly '{assemblyName}' from '{found}'");
            return reader;
        }
        
        Trace.TraceError($"Failed to load assembly '{assemblyName}'. Searched {probeNames.Count} paths: {string.Join("\n\t", probeNames)}");
        throw new DllNotFoundException($"Failed to load assembly '{assemblyName}'");
    }

    /// <summary>
    /// Register a metadata reader that was loaded by another process
    /// </summary>
    /// <param name="asmName"></param>
    /// <param name="reader"></param>
    public static void RegisterMetadataReader(string asmName, MetadataReader reader)
    {
        asmName = asmName.TrimEnd(".winmd").TrimEnd(".dll");
        if (!_assemblyReaders.ContainsKey(asmName))
        {
            _assemblyReaders[asmName] = reader;
        }
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
    public static TypeDefinitionHandle FindTypeDefinition(MetadataReader reader, string ns, string name, out MetadataReader targetReader)
    {
        if(_typeCache.TryGetValue((ns, name), out var cached))
        {
            targetReader = cached.reader;
            return cached.handle;
        }

        string? asmName = reader.GetAssemblyDefinition().GetAssemblyName().Name;
        //Debug.WriteLine($"Looking for {ns}.{name} in {asmName}");

        // Try normal type definitions (in the current assembly) first
        foreach (var tdHandle in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(tdHandle);
            if (reader.StringComparer.Equals(td.Name, name) &&
                reader.StringComparer.Equals(td.Namespace, ns))
            {
                targetReader = reader;
                _typeCache[(ns, name)] = (reader, tdHandle);
                return tdHandle;
            }
        }

        // Next, check for type forwarders (ExportedTypes). There are very few of these, and the JSON generator just hardcodes them
        // https://github.com/marlersoft/win32jsongen/blob/main/Generator/TypeRefDecoder.cs#L72
        // E.g. System.Guid goes netstandard -> System.Runtime -> System.Private.CoreLib
        foreach (var exportedHandle in reader.ExportedTypes)
        {
            ExportedType exported = reader.GetExportedType(exportedHandle);
            //Debug.WriteLine($"\t{reader.GetString(exported.Namespace)}.{reader.GetString(exported.Name)}: {exported.Implementation.Kind}");

            if (reader.StringComparer.Equals(exported.Name, name, true) &&
                reader.StringComparer.Equals(exported.Namespace, ns, true))
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

                    default:
                        throw new NotSupportedException(exported.Implementation.Kind.ToString());
                }
            }
        }

        throw new TypeLoadException($"Could not resolve reference to '{ns}.{name}' in assembly '{asmName}'");
    }

    /// <summary>
    /// Find a type definition by assembly, namespace, and name
    /// </summary>
    /// <param name="asmName">The assembly you expect the type to be in, not including file extensions - e.g "Windows.Win32", "Windows.Wdk"</param>
    /// <param name="ns">Namespace of the type</param>
    /// <param name="name">Name of the type</param>
    /// <returns></returns>
    public static TypeDefinitionHandle FindTypeDefinition(string asmName, string ns, string name, out MetadataReader foundReader)
    {
         MetadataReader reader = LoadAssemblyReader(asmName);
         return FindTypeDefinition(reader, ns, name, out foundReader);
    }

    public static TypeDefinitionHandle FindForwardedTypeRecursive(MetadataReader reader, EntityHandle handle, string ns, string name, out MetadataReader targetReader)
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
                throw new NotSupportedException($"Unsupported type forwarder target: {exported.Implementation.Kind}");
        }
    }
}
