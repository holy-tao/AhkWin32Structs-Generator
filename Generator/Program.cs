using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.Logging;

public class Program
{
    public static Dictionary<string, List<AhkExtension>> Extensions = [];

    public static string MetadataDir = "";

    public static ILogger Logger = null!;

    private static ILoggerFactory? _loggerFactory;

    public static int Main(string[] args)
    {
        var metadataDirArg = new Argument<DirectoryInfo>("metadata-dir", "Path to directory containing .winmd files and metadata");
        var outputDirArg = new Argument<DirectoryInfo>("output-dir", "Path to output directory for generated .ahk files");

        var namespaceOption = new Option<string[]>("--namespace", "Filter: only generate types in these namespaces (prefix match)")
        {
            AllowMultipleArgumentsPerToken = true
        };
        namespaceOption.AddAlias("-n");

        var assemblyOption = new Option<string[]>("--assembly", "Filter: only process these .winmd assemblies")
        {
            AllowMultipleArgumentsPerToken = true
        };
        assemblyOption.AddAlias("-a");

        var logLevelOption = new Option<LogLevel>("--log-level", () => LogLevel.Information, "Minimum log level");

        var rootCommand = new RootCommand("AhkWin32Structs Generator — generates AutoHotkey v2 projections of Win32 and WDK APIs")
        {
            metadataDirArg,
            outputDirArg,
            namespaceOption,
            assemblyOption,
            logLevelOption
        };

        int exitCode = 0;
        rootCommand.SetHandler(
            (metadataDir, outputDir, namespaceFilter, assemblyFilter, logLevel) =>
            {
                exitCode = RunGenerator(metadataDir, outputDir, namespaceFilter ?? [], assemblyFilter ?? [], logLevel);
            },
            metadataDirArg, outputDirArg, namespaceOption, assemblyOption, logLevelOption);

        rootCommand.Invoke(args);
        return exitCode;
    }

    private static int RunGenerator(DirectoryInfo metadataDir, DirectoryInfo outputDir, string[] namespaceFilter, string[] assemblyFilter, LogLevel logLevel)
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(logLevel);
            builder.AddSimpleConsole(opts =>
            {
                opts.SingleLine = true;
                opts.TimestampFormat = "HH:mm:ss ";
            });
        });
        Logger = _loggerFactory.CreateLogger("Generator");

        MetadataDir = metadataDir.FullName;
        string ahkOutputDir = outputDir.FullName;

        Logger.LogInformation("Starting AhkWin32Structs Generator...");
        Logger.LogInformation("Metadata Directory: {MetadataDir}", MetadataDir);
        Logger.LogInformation("Output Directory: {OutputDir}", ahkOutputDir);

        if (namespaceFilter.Length > 0)
            Logger.LogInformation("Namespace filter: {Namespaces}", string.Join(", ", namespaceFilter));
        if (assemblyFilter.Length > 0)
            Logger.LogInformation("Assembly filter: {Assemblies}", string.Join(", ", assemblyFilter));

        Stopwatch totalStopwatch = Stopwatch.StartNew();

        // Load API documentation
        Stopwatch phaseWatch = Stopwatch.StartNew();
        DocumentationUtils.Load(Path.Join(MetadataDir, "apidocs.msgpack"));
        phaseWatch.Stop();
        Logger.LogInformation("Loaded API documentation in {Elapsed:F1}s", phaseWatch.Elapsed.TotalSeconds);

        // Load extensions
        phaseWatch.Restart();
        Extensions = ExtensionReader.ReadExtensionFiles(Path.Join(MetadataDir, "extensions"));
        phaseWatch.Stop();
        Logger.LogInformation("Loaded {Count} extension mappings in {Elapsed:F1}s", Extensions.Count, phaseWatch.Elapsed.TotalSeconds);

        // Collect .winmd files
        phaseWatch.Restart();
        IEnumerable<FileStream> winmdFiles = CollectWinmdFiles(MetadataDir, assemblyFilter);
        phaseWatch.Stop();
        Logger.LogInformation("Collected .winmd files in {Elapsed:F1}s", phaseWatch.Elapsed.TotalSeconds);

        StringBuilder versionInfo = new();
        versionInfo.AppendLine("[Assemblies]");

        Logger.LogInformation("Generating bindings...");

        int total = 0, errors = 0;
        foreach (FileStream fileStream in winmdFiles)
        {
            PEReader peReader = new(fileStream);
            MetadataReader reader = peReader.GetMetadataReader();

            // Pull version info
            AssemblyName assemblyName = reader.GetAssemblyDefinition().GetAssemblyName();
            versionInfo.AppendLine($"{assemblyName.Name?.TrimEnd(".winmd")} = {assemblyName.Version}");

            Stopwatch assemblyWatch = Stopwatch.StartNew();
            (int fileTotal, int fileErrors) = GenerateBindings(reader, ahkOutputDir, namespaceFilter);
            assemblyWatch.Stop();

            Logger.LogInformation("{Assembly} v{Version}: {Total} files, {Errors} errors in {Elapsed:F1}s",
                assemblyName.Name?.TrimEnd(".winmd"), assemblyName.Version,
                fileTotal, fileErrors, assemblyWatch.Elapsed.TotalSeconds);

            total += fileTotal;
            errors += fileErrors;

            peReader.Dispose();
            fileStream.Dispose();
        }

        // Finalize version.ini with package info
        Logger.LogInformation("Finalizing version.ini...");
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
                Logger.LogInformation("Package {Name}: {Version}", info[0], info[1]);
            });

        File.WriteAllText(Path.Join(ahkOutputDir, "version.ini"), versionInfo.ToString());

        totalStopwatch.Stop();
        Logger.LogInformation("Done! Emitted {Total} files with {Errors} errors in {Elapsed:F1}s",
            total, errors, totalStopwatch.Elapsed.TotalSeconds);

        _loggerFactory.Dispose();

        return -errors;
    }

    private static (int total, int errors) GenerateBindings(MetadataReader mr, string outputDir, string[] namespaceFilter)
    {
        int total = 0, errors = 0;

        foreach (TypeDefinitionHandle hTypeDef in mr.TypeDefinitions)
        {
            TypeDefinition typeDef = mr.GetTypeDefinition(hTypeDef);

            string typeNamespace = mr.GetString(typeDef.Namespace);
            string typeName = mr.GetString(typeDef.Name);

            // Apply namespace filter before any processing
            if (namespaceFilter.Length > 0 &&
                !namespaceFilter.Any(prefix => typeNamespace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (ShouldSkipType(mr, hTypeDef))
                continue;

            try
            {
                IAhkEmitter? emitter = ParseType(mr, typeDef);
                if (emitter == null)
                {
                    Logger.LogDebug("Non-explicit skip: {Namespace}.{TypeName}", typeNamespace, typeName);
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
                Logger.LogError(ex, "Failed to parse {Namespace}.{TypeName}", typeNamespace, typeName);
            }

            if (total > 0 && total % 1000 == 0)
            {
                Logger.LogDebug("Progress: {Total} files emitted", total);
            }
        }

        return (total, errors);
    }

    private static List<FileStream> CollectWinmdFiles(string directoryPath, string[] assemblyFilter)
    {
        Logger.LogInformation("Scanning {Directory} for .winmd files...", directoryPath);

        return Directory.EnumerateFiles(directoryPath)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".winmd")
            .Where(path => assemblyFilter.Length == 0 || assemblyFilter.Any(filter =>
                Path.GetFileNameWithoutExtension(path).Equals(filter, StringComparison.OrdinalIgnoreCase)))
            .Select(path => { Logger.LogDebug("Found: {Path}", path); return path; })
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

        if (typeDef.BaseType.IsNil)
        {
            return mr.StringComparer.Equals(typeDef.Name, "<Module>");
        }

        if (typeDef.BaseType.Kind is not HandleKind.TypeReference)
            return false;

        TypeReference baseTypeRef = mr.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string baseTypeName = mr.GetString(baseTypeRef.Name);

        // MultiCastDelegate means function pointer
        if (baseTypeName is "MulticastDelegate" or "Attribute" or "<Module>")
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
