using Microsoft.Windows.SDK.Win32Docs;
using MessagePack;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Diagnostics;

public class Program
{
    public static void Main(string[] args)
    {
        Stopwatch stopwatch = new();
        stopwatch.Start();

        //TODO read in filepaths from command line or environment variables or something
        using FileStream metaDataFileStream = File.OpenRead(@"C:\Programming\AhkWin32Structs\metadata\Windows.Win32.winmd");
        using FileStream apiDocFileStream = File.OpenRead(@"C:\Programming\AhkWin32Structs\metadata\apidocs.msgpack");
        string ahkOutputDir = @"C:\Programming\AhkWin32Structs\ahk\";

        using StreamWriter errFileStream = new(File.Create(Path.Combine(ahkOutputDir, "errors.txt")));

        using PEReader peReader = new(metaDataFileStream);
        MetadataReader mr = peReader.GetMetadataReader();

        Dictionary<string, ApiDetails> apiDocs = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(apiDocFileStream);

        int total = 0;

        foreach (TypeDefinitionHandle hTypeDef in mr.TypeDefinitions)
        {
            TypeDefinition typeDef = mr.GetTypeDefinition(hTypeDef);

            if (typeDef.BaseType.Kind != HandleKind.TypeReference)
                continue;

            string typeNamespace = mr.GetString(typeDef.Namespace);
            if (ShouldSkipType(mr, hTypeDef, out string typeName, out string baseTypeName))
                continue;

            try
            {
                IAhkEmitter? emitter = ParseType(mr, typeDef, apiDocs);
                if (emitter == null)
                {
                    Console.WriteLine($">>> Skipped {baseTypeName}: {typeName}");
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
                Console.Error.WriteLine($"!!! {ex.GetType().Name} parsing {typeNamespace}.{typeName}: {ex.Message}");

                errFileStream.WriteLine($"{ex.GetType().Name} parsing {typeNamespace}.{typeName}: {ex.Message}");
                errFileStream.WriteLine(ex.Message);
                errFileStream.WriteLine(ex.StackTrace);
                errFileStream.WriteLine();

                errFileStream.Flush();
            }

            if (total % 1000 == 0)
            {
                Console.WriteLine($"Emitted: {total}");
            }
        }

        Console.WriteLine($"Done! Emitted {total} files in {stopwatch.Elapsed.TotalSeconds} seconds");
    }

    private static IAhkEmitter? ParseType(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string typeName = mr.GetString(typeDef.Name);
        string baseTypeName = mr.GetString(baseTypeRef.Name);

        if (typeName == "Apis")
        {
            // This is the generic type that global functions and constants wind up in
            return new AhkApiType(mr, typeDef, apiDocs);
        }

        return baseTypeName switch
        {
            "Enum" => new AhkEnum(mr, typeDef, apiDocs),
            "Struct" or "ValueType" => AhkStruct.Get(mr, typeDef, apiDocs),
            _ => null
        };
    }

    private static bool ShouldSkipType(MetadataReader mr, TypeDefinitionHandle typeDefHandle, out string typeName, out string baseTypeName)
    {
        TypeDefinition typeDef = mr.GetTypeDefinition((TypeDefinitionHandle)typeDefHandle);
        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        baseTypeName = mr.GetString(baseTypeRef.Name);
        typeName = mr.GetString(typeDef.Name);

        // Probably an opaque pointer or handle type. In any case, an AHK we can just use an Integer
        if (typeDef.GetFields().Count == 0)
            return true;

        // MultiCastDelegate means function pointer
        if (baseTypeName == "MultiCastDelegate")
            return true;

        if (typeDef.IsNested)
            return true;

        if (FieldSignatureDecoder.IsPseudoPrimitive(mr, typeDefHandle, out FieldInfo? _))
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

// For enums, value__ is the base type of the enum