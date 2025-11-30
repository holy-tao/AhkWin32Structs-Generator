using Microsoft.Windows.SDK.Win32Docs;
using MessagePack;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Diagnostics;
using System.Reflection;

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

        Console.WriteLine("Starting AhkWin32Structs Generator...");
        Console.WriteLine($"\tMetadata Directory: {MetadataDir}");
        Console.WriteLine($"\tOutput Directory: {ahkOutputDir}");

        Console.WriteLine("Reading metadata...");

        Stopwatch stopwatch = new();
        stopwatch.Start();

        using FileStream apiDocFileStream = File.OpenRead(Path.Join(MetadataDir, "apidocs.msgpack"));

        ApiDocs = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(apiDocFileStream);
        Extensions = ExtensionReader.ReadExtensionFiles(Path.Join(MetadataDir, "extensions"));

        IEnumerable<FileStream> winmdFiles = CollectWinmdFiles(MetadataDir);

        StringBuilder versionInfo = new();
        versionInfo.AppendLine("[Assemblies]");

        Console.WriteLine("Generating bindings...");

        int total = 0, errors = 0;
        foreach(FileStream fileStream in winmdFiles)
        {
            PEReader peReader = new(fileStream);
            MetadataReader reader = peReader.GetMetadataReader();
            
            // Pull version info
            AssemblyName assemblyName = reader.GetAssemblyDefinition().GetAssemblyName();
            versionInfo.AppendLine($"{assemblyName.Name?.TrimEnd(".winmd")} = {assemblyName.Version}");
            Console.Write($"\t{assemblyName.Name?.TrimEnd(".winmd")} v{assemblyName.Version}... ");

            (int fileTotal, int fileErrors) = GenerateBindings(reader, ahkOutputDir);

            Console.WriteLine($"done. {fileTotal} files generated with {fileErrors} errors.");
            total += fileTotal;
            errors += fileErrors;

            peReader.Dispose();
            fileStream.Dispose();
        }
    
        // Finalize version.ini with package info
        Console.WriteLine("Finalizing version.ini file...");
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
                Console.WriteLine($"\t{info[0]}: {info[1]}");
            });

        File.WriteAllText(Path.Join(ahkOutputDir, "version.ini"), versionInfo.ToString());
        
        Console.WriteLine($"Done! Emitted {total} files with {errors} errors in {stopwatch.Elapsed.TotalSeconds} seconds");
        return -errors;
    }

    private static (int total, int errors) GenerateBindings(MetadataReader mr, string outputDir)
    {
        int total = 0, errors = 0;

        foreach(TypeDefinitionHandle hTypeDef in mr.TypeDefinitions)
        {
            TypeDefinition typeDef = mr.GetTypeDefinition(hTypeDef);

            string typeNamespace = mr.GetString(typeDef.Namespace);
            string typeName = mr.GetString(typeDef.Name);

            if (ShouldSkipType(mr, hTypeDef))
                continue;

            try
            {
                IAhkEmitter? emitter = ParseType(mr, typeDef);
                if (emitter == null)
                {
                    Debug.WriteLine($"Non-explicit skip for {mr.GetString(typeDef.Namespace)}.{typeName}");
                    continue;
                }

                string filepath = emitter.GetDesiredFilepath(outputDir);
                string dirPath = Path.GetDirectoryName(filepath) ?? throw new NullReferenceException($"Null directory path: {filepath}");

                Directory.CreateDirectory(dirPath);
                File.WriteAllText(filepath, emitter.ToAhk());
                total++;
            }
            catch (Exception ex)
            {
                errors++;
                Console.Error.WriteLine($"{ex.GetType().Name} parsing {typeNamespace}.{typeName}: {ex.Message}");
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Console.Error.WriteLine();
            }

            if (total % 1000 == 0)
            {
                Debug.WriteLine($"Emitted: {total}");
            }
        }

        return (total, errors);
    }

    private static IEnumerable<FileStream> CollectWinmdFiles(string directoryPath)
    {
        Console.WriteLine($"Scanning '{directoryPath}' for .winmd files...");

        return Directory.EnumerateFiles(directoryPath)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".winmd")
            .Select(path => { Console.WriteLine($"\t{path}"); return path; })
            .Select(File.OpenRead)
            .ToList();
    }

    private static IAhkEmitter? ParseType(MetadataReader mr, TypeDefinition typeDef)
    {
        if ((typeDef.Attributes & TypeAttributes.Interface) != 0)
        {
            // COM Interface
            return new AhkComInterface(mr, typeDef);
        }

        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string typeName = mr.GetString(typeDef.Name);
        string baseTypeName = mr.GetString(baseTypeRef.Name);

        if (baseTypeName == "Object" && typeName == "Apis")
        {
            // This is the generic type that global functions and constants wind up in
            return new AhkApiType(mr, typeDef);
        }

        return baseTypeName switch
        {
            "Enum" => new AhkEnum(mr, typeDef),
            "Struct" or "ValueType" => AhkStruct.Get(mr, typeDef),
            _ => null
        };
    }

    private static bool ShouldSkipType(MetadataReader mr, TypeDefinitionHandle typeDefHandle)
    {
        TypeDefinition typeDef = mr.GetTypeDefinition(typeDefHandle);
        if(typeDef.BaseType.IsNil)
            return true;

        if (typeDef.BaseType.Kind != HandleKind.TypeReference)
            return false;

        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string baseTypeName = mr.GetString(baseTypeRef.Name);

        // MultiCastDelegate means function pointer
        if (baseTypeName is "MulticastDelegate" or "Attribute")
            return true;

        // Handled in their parents
        if (typeDef.IsNested)
            return true;

        return false;
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