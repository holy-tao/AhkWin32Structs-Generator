namespace AhkWin32.Generator;

using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AhkWin32.Generator.Emit;
using AhkWin32.Generator.Emit.Emitters;
using AhkWin32.Generator.Infrastructure;
using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Transform;
using Microsoft.Extensions.Logging;

public class Program
{
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

        var versionOption = new Option<string>("--ahk-version", () => "2.0", "AutoHotkey version to emit for (2.0 or 2.1)")
            .FromAmong("2.0", "2.1");
        versionOption.AddAlias("-v");

        var logLevelOption = new Option<LogLevel>("--log-level", () => LogLevel.Information, "Minimum log level");
        var logFileOption = new Option<FileInfo?>("--log-file", "Write log output to a file");
        var maxParallelismOption = new Option<int>("--max-parallelism",
            () => Environment.ProcessorCount,
            $"Maximum degree of parallelism for extraction and emission (default: CPU count)");

        var rootCommand = new RootCommand("AhkWin32Structs Generator — generates AutoHotkey v2 projections of Win32 and WDK APIs")
        {
            metadataDirArg,
            outputDirArg,
            versionOption,
            namespaceOption,
            assemblyOption,
            logLevelOption,
            logFileOption,
            maxParallelismOption
        };

        int exitCode = 0;
        rootCommand.SetHandler(
            (metadataDir, outputDir, ahkVersion, namespaceFilter, assemblyFilter, logLevel, logFile, maxParallelism) =>
            {
                AhkVersion resolvedVersion = ahkVersion switch
                {
                    "2.0" => AhkVersion.v20,
                    "2.1" => AhkVersion.v21,
                    _ => throw new NotImplementedException($"Unknown AHK version \"{ahkVersion}\"")
                };
                exitCode = RunGenerator(metadataDir, outputDir, resolvedVersion, namespaceFilter ?? [], assemblyFilter ?? [], logLevel, logFile, maxParallelism);
            },
            metadataDirArg, outputDirArg, versionOption, namespaceOption, assemblyOption, logLevelOption, logFileOption, maxParallelismOption);

        rootCommand.Invoke(args);
        return exitCode;
    }

    private static int RunGenerator(
        DirectoryInfo metadataDir,
        DirectoryInfo outputDir,
        AhkVersion ahkVersion,
        string[] namespaceFilter,
        string[] assemblyFilter,
        LogLevel logLevel,
        FileInfo? logFile,
        int maxParallelism = 0)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(logLevel);
            builder.AddSimpleConsole(opts =>
            {
                opts.SingleLine = true;
                opts.TimestampFormat = "HH:mm:ss ";
            });
            if (logFile != null)
                builder.AddProvider(new FileLoggerProvider(logFile.FullName, logLevel));
        });
        var logger = loggerFactory.CreateLogger("Generator");

        string metadataPath = metadataDir.FullName;
        string outputPath = outputDir.FullName;

        logger.LogInformation("Starting AhkWin32Structs Generator...");
        logger.LogInformation("Metadata Directory: {MetadataDir}", metadataPath);
        logger.LogInformation("Output Directory: {OutputDir}", outputPath);
        logger.LogInformation("Emitting for AutoHotkey v{version}", ahkVersion.ToFriendlyString());

        // -1 is no hard max, which we want to allow
        // See https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.paralleloptions.maxdegreeofparallelism?view=net-10.0
        if (maxParallelism == 0 || maxParallelism < -1)
        {
            logger.LogWarning("Invalid --max-parallelism ({ARG}), using CPU count ({CPUS})",
                maxParallelism, Environment.ProcessorCount);
            maxParallelism = Environment.ProcessorCount;
        }
        logger.LogInformation("Max parallelism: {MaxParallelism}", maxParallelism);

        if (namespaceFilter.Length > 0)
            logger.LogInformation("Namespace filter: {Namespaces}", string.Join(", ", namespaceFilter));
        if (assemblyFilter.Length > 0)
            logger.LogInformation("Assembly filter: {Assemblies}", string.Join(", ", assemblyFilter));

        Stopwatch totalWatch = Stopwatch.StartNew();

        // Load documentation
        var docs = new DocumentationLoader(loggerFactory.CreateLogger<DocumentationLoader>());
        docs.Load(Path.Join(metadataPath, "apidocs.msgpack"));

        // Load reserved names config
        var reservedNames = ReservedNameConfig.Load(Path.Join(metadataPath, "ahk-reserved-names.yml"));

        // Extract all types into TypeRegistry
        using var loader = new MetadataLoader(metadataPath, loggerFactory.CreateLogger<MetadataLoader>());
        loader.LoadPrimaryAssemblies(assemblyFilter.Length > 0 ? assemblyFilter : null);

        var extractor = new TypeExtractor(loader, docs, loggerFactory, reservedNames, maxParallelism);
        TypeRegistry registry = extractor.ExtractAll();

        // Transforms
        var overrideApplier = new OverrideApplier(
            new OverrideReader(loggerFactory.CreateLogger<OverrideReader>(), maxParallelism),
            loggerFactory.CreateLogger<OverrideApplier>());
        overrideApplier.Apply(registry, Path.Join(metadataPath, "overrides"));

        var extensionApplier = new ExtensionApplier(
            new ExtensionReader(loggerFactory.CreateLogger<ExtensionReader>(), maxParallelism),
            loggerFactory.CreateLogger<ExtensionApplier>());
        extensionApplier.Apply(registry, Path.Join(metadataPath, "extensions"));

        // Emit
        ITypeEmitter[] emitters = [
            ahkVersion is AhkVersion.v21 ? new EnumEmitter21() : new EnumEmitter(),
            ahkVersion is AhkVersion.v21 ? new HandleEmitter21() : new HandleEmitter(),
            ahkVersion is AhkVersion.v21 ? new StructEmitter21(registry) : new StructEmitter(registry),
            ahkVersion switch {
                AhkVersion.v20 => new ApiTypeEmitter(registry),
                AhkVersion.v21 => new ApiTypeEmitter21(registry),
                _ => throw new NotImplementedException($"Unknown AHK version \"{ahkVersion}\"")
            },
            new ComInterfaceEmitter(registry, ahkVersion)
        ];
        if (ahkVersion is AhkVersion.v21)
            emitters = [.. emitters, new ApiConstantsEmitter21(), new NativeTypedefEmitter21()];
        var pipeline = new TypeEmissionPipeline(emitters, loggerFactory.CreateLogger<TypeEmissionPipeline>(), maxParallelism);
        var (emitted, _, errors) = pipeline.EmitAll(registry, outputPath,
            namespaceFilter.Length > 0 ? namespaceFilter : null);

        // Write version.ini
        WriteVersionInfo(loader, metadataPath, outputPath, logger);

        totalWatch.Stop();
        logger.LogInformation("Done! Total time: {Elapsed:F1}s", totalWatch.Elapsed.TotalSeconds);

        return -errors;
    }

    private static void WriteVersionInfo(MetadataLoader loader, string metadataDir, string outputDir, ILogger logger)
    {
        logger.LogInformation("Writing version.ini...");

        var versionInfo = new StringBuilder();
        versionInfo.AppendLine("[Assemblies]");

        foreach (var (name, _, reader) in loader.GetPrimaryAssemblies())
        {
            string displayName = name.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase)
                ? name[..^".winmd".Length] : name;
            AssemblyName asmName = reader.GetAssemblyDefinition().GetAssemblyName();
            versionInfo.AppendLine($"{displayName} = {asmName.Version}");
        }

        versionInfo.AppendLine();
        versionInfo.AppendLine("[Packages]");

        foreach (string path in Directory.EnumerateFiles(metadataDir, "*.version"))
        {
            string packageName = Path.GetFileNameWithoutExtension(path);
            string version = File.ReadAllText(path).Trim();
            versionInfo.AppendLine($"{packageName} = {version}");
            logger.LogInformation("Package {Name}: {Version}", packageName, version);
        }

        File.WriteAllText(Path.Join(outputDir, "version.ini"), versionInfo.ToString());
    }
}
