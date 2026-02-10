using Microsoft.Windows.SDK.Win32Docs;
using MessagePack;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MessagePack.Resolvers;

public class Program
{
    public static Dictionary<string, ApiDetails> ApiDocs = [];

    public static Dictionary<string, List<AhkExtension>> Extensions = [];

    public static string MetadataDir = "";

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: AhkWin32Structs.exe <metadata-directory> <output-root>");
            return -1;
        }

        MetadataDir = args[0];
        string ahkOutputDir = args[1];

        Trace.TraceInformation("Starting AhkWin32Structs Generator...");
        Trace.TraceInformation($"\tMetadata Directory: {MetadataDir}");
        Trace.TraceInformation($"\tOutput Directory: {ahkOutputDir}");

        Trace.TraceInformation("Reading metadata...");

        Stopwatch stopwatch = new();
        stopwatch.Start();

        Trace.Listeners.Add(new TextWriterTraceListener(Path.Join(ahkOutputDir, $"generator-{DateTime.UtcNow:yyyyMMddHHmmssfff}.log"), "Generator Log"));
        Trace.Listeners.Add(new ConsoleTraceListener());

#if DEBUG
        Trace.AutoFlush = true;
#else
        Trace.AutoFlush = false;
#endif

        using FileStream apiDocFileStream = File.OpenRead(Path.Join(MetadataDir, "apidocs.msgpack"));

        ApiDocs = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(apiDocFileStream);
        Extensions = ExtensionReader.ReadExtensionFiles(Path.Join(MetadataDir, "extensions"));
        NetTypeMappings.Load(MetadataDir);
        PiidUtils.Load(MetadataDir);

        IEnumerable<FileStream> winmdFiles = CollectWinmdFiles(MetadataDir);

        StringBuilder versionInfo = new();
        versionInfo.AppendLine("[Assemblies]");

        Trace.TraceInformation("Generating bindings...");

        // Keep PEReaders alive to prevent corruption of MetadataReaders
        List<PEReader> peReaders = new();

        int total = 0, errors = 0;
        foreach(FileStream fileStream in winmdFiles)
        {
            PEReader peReader = new(fileStream);
            peReaders.Add(peReader); // Keep alive to prevent GC
            MetadataReader reader = peReader.GetMetadataReader();

            // Pull version info
            AssemblyName assemblyName = reader.GetAssemblyDefinition().GetAssemblyName();
            versionInfo.AppendLine($"{assemblyName.Name?.TrimEnd(".winmd")} = {assemblyName.Version}");
            Trace.TraceInformation($"Generating \t{assemblyName.Name?.TrimEnd(".winmd")} v{assemblyName.Version}... ");

            FieldSignatureDecoder.RegisterMetadataReader(assemblyName.Name!.TrimEnd(".winmd"), reader);

            (int fileTotal, int fileErrors) = GenerateBindings(reader, ahkOutputDir);

            Trace.TraceInformation($"Done processing {assemblyName.Name?.TrimEnd(".winmd")}. {fileTotal} files generated with {fileErrors} errors.");
            total += fileTotal;
            errors += fileErrors;
        }
    
        // Finalize version.ini with package info
        Trace.TraceInformation("Finalizing version.ini file...");
        versionInfo.AppendLine();
        versionInfo.AppendLine("[Packages]");

        Directory.EnumerateFiles(MetadataDir, "*.version")
            .Select(fullPath => new string[] {
                Path.GetFileNameWithoutExtension(fullPath),
                File.ReadAllText(fullPath).Trim()
            })
            .ToList()
            .ForEach(info => {
                versionInfo.AppendLine($"{info[0]} = {info[1]}");
                Trace.TraceInformation($"\t{info[0]}: {info[1]}");
            });

        File.WriteAllText(Path.Join(ahkOutputDir, "version.ini"), versionInfo.ToString());
        
        var elapsed = stopwatch.Elapsed;
        Trace.TraceInformation($"Done! Emitted {total} files with {errors} errors in {elapsed.Minutes} minutes {elapsed.Seconds} seconds.");
        Trace.Flush();
        return -errors;
    }

    private static (int total, int errors) GenerateBindings(MetadataReader mr, string outputDir)
    {
        int total = 0, errors = 0;

        // Look through TypeDefinitions
        foreach(TypeDefinitionHandle hTypeDef in mr.TypeDefinitions)
        {
            bool success = GenerateSingleBinding(mr, hTypeDef, outputDir);
            total++;
            if(!success)
                errors++;
        }

        // Look through assembly-level type exports (there are a couple of these in windows.winmd)
        foreach(ExportedTypeHandle hExported in mr.ExportedTypes)
        {
            ExportedType exported = mr.GetExportedType(hExported);
            string ns = mr.GetString(exported.Namespace);
            string name = mr.GetString(exported.Name);

            TypeDefinitionHandle forwarded = FieldSignatureDecoder.FindForwardedTypeRecursive(
                mr, hExported, ns, name, out MetadataReader targetReader
            );

            bool success = GenerateSingleBinding(targetReader, forwarded, outputDir);
            total++;
            if(!success)
                errors++;
        }

        return (total, errors);
    }

    private static bool GenerateSingleBinding(MetadataReader mr, TypeDefinitionHandle hTypeDef, string outputDir)
    {
        TypeDefinition typeDef = mr.GetTypeDefinition(hTypeDef);

        if (ShouldSkipType(mr, typeDef))
            return true;

        Trace.TraceInformation($"Generating {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}");
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            IAhkEmitter? emitter = ParseType(mr, typeDef);
            if (emitter == null)
            {
                Trace.TraceWarning($"Non-explicit skip for {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}");
                return true;
            }

            string filepath = emitter.GetDesiredFilepath(outputDir);
            string dirPath = Path.GetDirectoryName(filepath) ?? throw new NullReferenceException($"Null directory path: {filepath}");

            Directory.CreateDirectory(dirPath);
            File.WriteAllText(filepath, emitter.ToAhk());
            Trace.TraceInformation($"Wrote code to {filepath}");

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"{ex.GetType().Name} parsing {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}\n{ex}");
            return false;
        }
        finally
        {
            Trace.TraceInformation($"Took {stopwatch.Elapsed.Milliseconds} ms ({stopwatch.Elapsed.Seconds} seconds)");
        }
    }

    private static IEnumerable<FileStream> CollectWinmdFiles(string directoryPath)
    {
        Trace.TraceInformation($"Scanning '{directoryPath}' for .winmd files...");

        return Directory.EnumerateFiles(directoryPath)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".winmd")
            //.Where(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "windows")
            .Select(path => { Trace.TraceInformation($"\t{path}"); return path; })
            .Select(File.OpenRead);
    }

    private static IAhkEmitter? ParseType(MetadataReader mr, TypeDefinition typeDef)
    {
        if ((typeDef.Attributes & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface)
        {
            // COM Interface
            return new AhkComInterface(mr, typeDef);
        }

        var (baseTypeNamespace, baseTypeName) = GetEntityHandleName(mr, typeDef.BaseType);

        if ((typeDef.Attributes & TypeAttributes.WindowsRuntime) == 0 && 
            baseTypeName is "Object" && mr.StringComparer.Equals(typeDef.Name, "Apis"))
        {
            // This is the generic type that global Win32 functions and constants wind up in
            return new AhkApiType(mr, typeDef);
        }

        return baseTypeName switch
        {
            "Enum" => new AhkEnum(mr, typeDef),
            "Struct" or "ValueType" => AhkStruct.Get(mr, typeDef),
            "Object" => new AhkWinRTClass(mr, typeDef, "System", "Object"),
            "MulticastDelegate" => new AhkWinRTDelegate(mr, typeDef),
            _ => new AhkWinRTClass(mr, typeDef, baseTypeNamespace, baseTypeName)     // Class that extends another class
        };
    }

    private static bool ShouldSkipType(MetadataReader mr, TypeDefinition typeDef)
    {
        // Skip modules
        if(mr.StringComparer.Equals(typeDef.Name, "<Module>"))
        {
            Trace.TraceInformation($"Skipping module {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}");
            return true;
        }

        var (_, baseTypeName) = GetEntityHandleName(mr, typeDef.BaseType);

        // MultiCastDelegate means function pointer
        if (baseTypeName is "Attribute")
        {
            Trace.TraceInformation($"Skipping {baseTypeName} {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}");
            return true;
        }

        // Handled in their parents
        if (typeDef.IsNested)
        {
            Trace.TraceInformation($"Skipping nested type {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}");
            return true;
        }
            
        return false;
    }

    static (string typeNamespace, string typeName) GetEntityHandleName(MetadataReader reader, EntityHandle handle) 
    {
        if (handle.IsNil)
        {
            // This is legit for the base types of fundamental types like System.Object and System.ValueType
            return (string.Empty, string.Empty);
        }

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                TypeDefinition typeDef = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                return (reader.GetString(typeDef.Namespace),reader.GetString(typeDef.Name));
            case HandleKind.TypeReference:
                TypeReference typeRef = reader.GetTypeReference((TypeReferenceHandle)handle);
                return (reader.GetString(typeRef.Namespace),reader.GetString(typeRef.Name));
            default:
                throw new NotSupportedException(handle.Kind.ToString());
        }
    }

    private static string ToHex(BlobReader reader)
    {
        var sb = new StringBuilder();
        while (reader.RemainingBytes > 0)
        {
            sb.Append(reader.ReadByte().ToString("X2"));
            sb.Append(' ');
        }
        return sb.ToString();
    }
}