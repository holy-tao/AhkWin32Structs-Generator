using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MetadataUtils;

namespace Tao.AHK.WindowsBindGen.PiidPrecompute;

#pragma warning disable CA2211

public class Program
{
    // TODO take these in as command-line arguments
    public static string MetadataDir = "", OutputPath = "";

    public static int Main(string[] args)
    {
        if(args.Length != 2)
        {
            Console.Error.WriteLine(@"Usage: .\PiidPrecompute <MetadatDir> <OutputDir>");
            return 1;
        }

        MetadataDir = args[0];
        OutputPath = args[1];

        using PEReader peReader = new(File.OpenRead(Path.Join(MetadataDir, "windows.winmd")));
        MetadataReader reader = peReader.GetMetadataReader();

        TypeMappings.Load(MetadataDir);

        GenericSignatureTypeProvider genericSignatureProvider = new();
        using WinMDTypeResolver resolver = new();
        resolver.LoadAssembly(Path.Join(MetadataDir, "windows.winmd"));
        WinRTSignatureTypeProvider winRTSignatureProvider = new(reader, resolver);
        resolver.SetProvider(winRTSignatureProvider);
        
        Stopwatch stopwatch = Stopwatch.StartNew();

        IEnumerable<string> closedGenericTypeSignatures = 
            PrecomputeFromTypeRefs(reader, genericSignatureProvider, winRTSignatureProvider)
            // .Concat(PrecomputeFromMethodParams(reader, genericSignatureProvider, winRTSignatureProvider))
            .Distinct();
        
        File.WriteAllLines(OutputPath, closedGenericTypeSignatures);

        stopwatch.Stop();

        Console.WriteLine($"Done! Discovered {closedGenericTypeSignatures.Count()} generic types in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.Seconds}s)");
        Console.WriteLine($"Output written to {OutputPath}");
        
        return 0;
    }

    /// <summary>
    /// Finds all generic interfaces in method parameters - it turns out that this does not yield any generics
    /// not already found by scanning TypeReferences
    /// </summary>
    private static IEnumerable<string> PrecomputeFromMethodParams(MetadataReader reader,
        GenericSignatureTypeProvider genericSignatureProvider, WinRTSignatureTypeProvider winRTSignatureProvider)
    {
        return reader.MethodDefinitions
            .Select(reader.GetMethodDefinition)
            .SelectMany(methodDef =>
            {
                try
                {
                    var keys = methodDef.DecodeSignature(genericSignatureProvider, new([], []));
                    var winRTSig = methodDef.DecodeSignature(winRTSignatureProvider, new([], []));

                    List<string> generics = [];

                    var pairs = winRTSig.ParameterTypes
                        .Zip(keys.ParameterTypes)
                        .Append((winRTSig.ReturnType, keys.ReturnType))
                        .OfType<(WinRTSignature.PInterface, string)>();

                    foreach (var pifacePair in pairs)
                    {
                        generics.Add($"{pifacePair.Item2}: {pifacePair.Item1.ComputeIid()}");
                    }

                    return generics;
                }
                catch (Exception ex)
                {
                    TypeDefinition declarer = reader.GetTypeDefinition(methodDef.GetDeclaringType());
                    string declarerNamespace = reader.GetString(declarer.Namespace);
                    string declarerName = reader.GetString(declarer.Name);
                    string methodName = reader.GetString(methodDef.Name);

                    Console.Error.WriteLine(
                        $"Warning: could not decode param(s) in method {declarerNamespace}.{declarerName}::{methodName}: {ex.Message}");
                    Debug.WriteLine(ex.StackTrace);
                    return [];
                }
            });
    }

    /// <summary>
    /// Finds all generic interface instantiations in interface implementations
    /// </summary>
    private static IEnumerable<string> PrecomputeFromTypeRefs(MetadataReader reader, 
        GenericSignatureTypeProvider genericSignatureProvider, WinRTSignatureTypeProvider winRTSignatureProvider)
    {
        return reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .SelectMany(typeDef => typeDef.GetInterfaceImplementations())
            .Select(reader.GetInterfaceImplementation)
            .Where(impl => impl.Interface.Kind is HandleKind.TypeSpecification)
            .Select(impl => reader.GetTypeSpecification((TypeSpecificationHandle)impl.Interface))
            .Select(typeSpec =>
            {
                try
                {
                    var signature = typeSpec.DecodeSignature(winRTSignatureProvider, GenericContext.Empty);
                    if (signature is WinRTSignature.PInterface pinterface)
                    {
                        var Key = typeSpec.DecodeSignature(genericSignatureProvider, new([], []));
                        return $"{Key}: {pinterface.ComputeIid()}";
                    }
                    else
                    {
                        // Not a generic instantiation
                        return "";
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Could not decode type spec: {ex.Message}");
                    Debug.WriteLine(ex.StackTrace);
                    return "";
                }
            })
            .Where(str => !string.IsNullOrWhiteSpace(str));
    }
}