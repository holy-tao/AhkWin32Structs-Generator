using System.Diagnostics;
using YamlDotNet.Serialization;

class ExtensionReader
{
    public static Dictionary<string, List<AhkExtension>> ReadExtensionFiles(string extensionDirectoryPath)
    {
        IDeserializer deserializer = GetDeserializer();
        Dictionary<string, List<AhkExtension>> extensions = [];

        foreach (string path in Directory.EnumerateFiles(extensionDirectoryPath))
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not ".yml" or ".yaml")
            {
                Debug.WriteLine($"\tSkipping {Path.GetExtension(path)} file in extensions directory: {Path.GetFileName(path)}");
                continue;
            }

            AhkExtension ext = deserializer.Deserialize<AhkExtension>(File.ReadAllText(path));
            foreach (string fqn in ext.AppliesTo)
            {
                if (!extensions.TryGetValue(fqn, out List<AhkExtension>? extsForType))
                {
                    extsForType = [];
                    extensions.Add(fqn, extsForType);
                }

                extsForType.Add(ext);
            }
        }

        return extensions;
    }

    static IDeserializer GetDeserializer()
    {
        return new DeserializerBuilder().Build();
    }
}