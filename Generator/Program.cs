using Microsoft.Windows.SDK.Win32Docs;
using MessagePack;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

public class Program
{
    public static void Main(string[] args)
    {
        if(args.Length != 2)
        {
            Console.Error.WriteLine("Usage: AhkWin32Structs.exe <metadata-directory> <output-root>");
            return;
        }

        Console.WriteLine("Parsing metadata and generating AHK scripts...");

        Stopwatch stopwatch = new();
        stopwatch.Start();

        using FileStream metaDataFileStream = File.OpenRead(Path.Join(args[0], "Windows.Win32.winmd"));
        using FileStream apiDocFileStream = File.OpenRead(Path.Join(args[0], "apidocs.msgpack"));
        string ahkOutputDir = args[1];

        using PEReader peReader = new(metaDataFileStream);
        MetadataReader mr = peReader.GetMetadataReader();

        CreateVersionFile(mr, args[0], ahkOutputDir);
        
        Dictionary<string, ApiDetails> apiDocs = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(apiDocFileStream);

        int total = 0;

        foreach (TypeDefinitionHandle hTypeDef in mr.TypeDefinitions)
        {
            TypeDefinition typeDef = mr.GetTypeDefinition(hTypeDef);

            string typeNamespace = mr.GetString(typeDef.Namespace);
            if (ShouldSkipType(mr, hTypeDef))
                continue;

            try
            {
                IAhkEmitter? emitter = ParseType(mr, typeDef, apiDocs);
                if (emitter == null)
                {
                    Debug.WriteLine($">>> Skipped {mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}");
                    continue;
                }

                string filepath = emitter.GetDesiredFilepath(ahkOutputDir);
                string dirPath = Path.GetDirectoryName(filepath) ?? throw new NullReferenceException($"Null directory path: {filepath}");

                Directory.CreateDirectory(dirPath);
                File.WriteAllText(filepath, emitter.ToAhk());
                total++;
            }
            catch (Exception ex)
            {
                string typeName = mr.GetString(typeDef.Name);

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

        Console.WriteLine($"Done! Emitted {total} files in {stopwatch.Elapsed.TotalSeconds} seconds");
    }

    private static IAhkEmitter? ParseType(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        if ((typeDef.Attributes & TypeAttributes.Interface) != 0)
        {
            // COM Interface
            return new AhkComInterface(mr, typeDef, apiDocs);
        }

        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string typeName = mr.GetString(typeDef.Name);
        string baseTypeName = mr.GetString(baseTypeRef.Name);

        if (baseTypeName == "Object" && typeName == "Apis")
        {
            // This is the generic class that global functions and constants wind up in
            return new AhkApiType(mr, typeDef, apiDocs);
        }

        return baseTypeName switch
        {
            "Enum" => new AhkEnum(mr, typeDef, apiDocs),
            "Struct" or "ValueType" => AhkStruct.Get(mr, typeDef, apiDocs),
            _ => null
        };
    }

    private static bool ShouldSkipType(MetadataReader mr, TypeDefinitionHandle typeDefHandle)
    {
        TypeDefinition typeDef = mr.GetTypeDefinition(typeDefHandle);

        if (typeDef.BaseType.Kind != HandleKind.TypeReference)
            return false;

        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string baseTypeName = mr.GetString(baseTypeRef.Name);

        // Placeholder for a COM CLSID that we shouldn't generate a file for
        if (baseTypeName == "ValueType" && typeDef.GetFields().Count == 0)
            return true;

        // Function pointer, no analagous concept in AHK - we emit these as primitives elsewhere
        if (baseTypeName == "MulticastDelegate")
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

    private static void CreateVersionFile(MetadataReader mr, string metadataDirectory, string outputDirectory)
    {
        StringBuilder sb = new();

        sb.AppendLine("# The metadata version as reported in the actual metadata file");
        sb.AppendLine($"metadata = {mr.MetadataVersion}");
        sb.AppendLine();

        sb.AppendLine("# NuGet package versions used to generate files");
        sb.AppendLine("[Packages]");

        Directory.EnumerateFiles(metadataDirectory, "*.version")
            .Select(fullPath => new string[] {
                Path.GetFileNameWithoutExtension(fullPath),
                File.ReadAllText(fullPath).Trim()
            })
            .ToList()
            .ForEach(info => {
                sb.AppendLine($"{info[0]} = {info[1]}");
                Console.WriteLine($"\t{info[0]}: {info[1]}");
            });

        File.WriteAllText(Path.Join(outputDirectory, "version.ini"), sb.ToString());
    }
}

// For enums, value__ is the base type of the enum