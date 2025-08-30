using Microsoft.Windows.SDK.Win32Docs;
using MessagePack;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

public class Program
{
    public static void Main(string[] args)
    {
        //TODO read in filepaths from command line or environment variables or something
        FileStream metaDataFileStream = File.OpenRead(@"C:\Programming\AhkWin32Structs\metadata\Windows.Win32.winmd");
        FileStream apiDocFileStream = File.OpenRead(@"C:\Programming\AhkWin32Structs\metadata\apidocs.msgpack");
        string ahkOutputDir = @"C:\Programming\AhkWin32Structs\ahk\";

        using PEReader peReader = new(metaDataFileStream);
        MetadataReader mr = peReader.GetMetadataReader();

        Dictionary<string, ApiDetails> apiDocs = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(apiDocFileStream);

        int cnt = 0;

        foreach (TypeDefinitionHandle hTypeDef in mr.TypeDefinitions)
        {
            TypeDefinition typeDef = mr.GetTypeDefinition(hTypeDef);

            if (typeDef.BaseType.Kind != HandleKind.TypeReference)
                continue;

            TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
            string baseTypeName = mr.GetString(baseTypeRef.Name);
            string typeName = mr.GetString(typeDef.Name);

            // MultiCastDelegate means function pointer, "Apis" is the generic type for all functions
            if (ShouldSkipType(mr, typeDef))
                continue;

            if (baseTypeName == "Enum")
            {
                //TODO
                //Console.WriteLine($"Skipping enum: {typeName}");
                continue;
            }

            try
            {
                AhkStruct ahkStruct = new(mr, typeDef, apiDocs);

                string filepath = ahkStruct.GetDesiredFilepath(ahkOutputDir);
                string dirPath = Path.GetDirectoryName(filepath) ?? throw new Exception($"Null directory path: {filepath}");

                Directory.CreateDirectory(dirPath);
                File.WriteAllText(filepath, ahkStruct.ToAhk());

                Console.WriteLine($"Wrote {filepath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{ex.GetType().Name} parsing {typeName}:");
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Console.WriteLine();
            }


            if (++cnt > 50) { return; }
        }
    }

    private static bool ShouldSkipType(MetadataReader mr, TypeDefinition typeDef)
    {
        // Probably an opaque pointer or handle type. In any case, an AHK we can just use an Integer
        if (typeDef.GetFields().Count == 0)
            return true;

        // This is the generic name that the floating "global" functions end up in. Maybe one day I'll
        // support them
        if (mr.GetString(typeDef.Name) == "Apis")
            return true;

        // TODO support union types
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

// For enums, value__ is the base type of the enum