using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;

/// <summary>
/// Utilities for loading and retrieving parameterized interface IDs
/// </summary>
public static class PiidUtils
{
    private static Dictionary<string, string> piids = [];

    public static void Load(string metadataDir)
    {        
        string fullpath = Path.Join(metadataDir, "piids.yml");
        Stopwatch stopwatch = Stopwatch.StartNew();
        Trace.TraceInformation($"Loading piids from {fullpath}");

        string yamlContent = File.ReadAllText(fullpath);

        IDeserializer deserializer = new DeserializerBuilder().Build();
        piids = deserializer.Deserialize<Dictionary<string, string>>(yamlContent);
        
        stopwatch.Stop();
        Trace.TraceInformation($"Loaded {piids.Count} piids in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.Seconds}s)");
    }

    public static bool TryGetPiid(string fullTypeName, [NotNullWhen(true)] out Guid? piid)
    {
        if(piids.TryGetValue(fullTypeName, out string? piidStr))
        {
            piid = new Guid(piidStr);
            return true;
        }

        piid = null;
        return false;
    }

    public static bool TryGetPiid(FieldInfo fieldInfo, [NotNullWhen(true)] out Guid? piid)
    {
        return TryGetPiid(fieldInfo.GetFullTypeSignature(), out piid);
    }
}