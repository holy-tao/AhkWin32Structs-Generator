using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using AhkWin32.Generator.Emit;
using AhkWin32.Generator.Emit.Emitters;
using AhkWin32.Generator.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;
using IRArchitecture = AhkWin32.Generator.Model.Architecture;

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
        var validateIrOption = new Option<bool>("--validate-ir", "Run IR extraction and print diagnostic report (no code generation)");
        var emitIrOption = new Option<bool>("--emit-ir", "Run new IR emitter pipeline (for comparison against legacy)");

        var rootCommand = new RootCommand("AhkWin32Structs Generator — generates AutoHotkey v2 projections of Win32 and WDK APIs")
        {
            metadataDirArg,
            outputDirArg,
            namespaceOption,
            assemblyOption,
            logLevelOption,
            validateIrOption,
            emitIrOption
        };

        int exitCode = 0;
        rootCommand.SetHandler(
            (metadataDir, outputDir, namespaceFilter, assemblyFilter, logLevel, validateIr, emitIr) =>
            {
                exitCode = RunGenerator(metadataDir, outputDir, namespaceFilter ?? [], assemblyFilter ?? [], logLevel, validateIr, emitIr);
            },
            metadataDirArg, outputDirArg, namespaceOption, assemblyOption, logLevelOption, validateIrOption, emitIrOption);

        rootCommand.Invoke(args);
        return exitCode;
    }

    private static int RunGenerator(DirectoryInfo metadataDir, DirectoryInfo outputDir, string[] namespaceFilter, string[] assemblyFilter, LogLevel logLevel, bool validateIr = false, bool emitIr = false)
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

        // --- New IR extraction (--validate-ir) ---
        if (validateIr)
        {
            int irResult = RunIRValidation(MetadataDir, assemblyFilter, _loggerFactory!);
            _loggerFactory!.Dispose();
            return irResult;
        }

        // --- New IR emitter pipeline (--emit-ir) ---
        if (emitIr)
        {
            int emitResult = RunIREmission(MetadataDir, outputDir.FullName, namespaceFilter, assemblyFilter, _loggerFactory!);
            _loggerFactory!.Dispose();
            return emitResult;
        }

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

    private static int RunIREmission(string metadataDir, string outputDir, string[] namespaceFilter, string[] assemblyFilter, ILoggerFactory loggerFactory)
    {
        Logger.LogInformation("=== IR Emitter Pipeline ===");
        Stopwatch totalWatch = Stopwatch.StartNew();

        // Load documentation
        var docs = new DocumentationLoader(loggerFactory.CreateLogger<DocumentationLoader>());
        docs.Load(Path.Join(metadataDir, "apidocs.msgpack"));

        // Extract all types into TypeRegistry
        using var loader = new MetadataLoader(metadataDir, loggerFactory.CreateLogger<MetadataLoader>());
        loader.LoadPrimaryAssemblies(assemblyFilter.Length > 0 ? assemblyFilter : null);

        var extractor = new TypeExtractor(loader, docs, loggerFactory);
        TypeRegistry registry = extractor.ExtractAll();

        // Emit
        ITypeEmitter[] emitters = [
            new EnumEmitter(),
            new HandleEmitter(),
            new StructEmitter(),
            new ApiTypeEmitter(registry),
            new ComInterfaceEmitter(registry)
        ];
        var pipeline = new TypeEmissionPipeline(emitters, loggerFactory.CreateLogger<TypeEmissionPipeline>());
        var (emitted, _, errors) = pipeline.EmitAll(registry, outputDir,
            namespaceFilter.Length > 0 ? namespaceFilter : null);

        totalWatch.Stop();
        Logger.LogInformation("Total time: {Elapsed:F1}s", totalWatch.Elapsed.TotalSeconds);

        return -errors;
    }

    private static int RunIRValidation(string metadataDir, string[] assemblyFilter, ILoggerFactory loggerFactory)
    {
        Logger.LogInformation("=== IR Extraction Validation ===");

        // Load documentation into the new DocumentationLoader
        var docs = new DocumentationLoader(loggerFactory.CreateLogger<DocumentationLoader>());
        docs.Load(Path.Join(metadataDir, "apidocs.msgpack"));

        // Create MetadataLoader and extract
        using var loader = new MetadataLoader(metadataDir, loggerFactory.CreateLogger<MetadataLoader>());
        loader.LoadPrimaryAssemblies(assemblyFilter.Length > 0 ? assemblyFilter : null);

        var extractor = new TypeExtractor(loader, docs, loggerFactory);
        TypeRegistry registry = extractor.ExtractAll();

        // --- Diagnostic report ---
        Logger.LogInformation("=== Registry Summary ===");

        int structCount = registry.GetAll<StructType>().Count(t => t is not HandleType);
        int handleCount = registry.GetAll<HandleType>().Count();
        int enumCount = registry.GetAll<EnumType>().Count();
        int comCount = registry.GetAll<ComInterfaceType>().Count();
        int apiCount = registry.GetAll<ApiType>().Count();

        Logger.LogInformation("  Structs:        {Count}", structCount);
        Logger.LogInformation("  Handles:        {Count}", handleCount);
        Logger.LogInformation("  Enums:          {Count}", enumCount);
        Logger.LogInformation("  COM Interfaces: {Count}", comCount);
        Logger.LogInformation("  API Types:      {Count}", apiCount);
        Logger.LogInformation("  Total:          {Count}", registry.Count);

        // Architecture-specific types
        int archSpecific = registry.GetAll().Count(t => t.Arch != IRArchitecture.All);
        Logger.LogInformation("  Architecture-specific: {Count}", archSpecific);

        // --- Spot-checks ---
        Logger.LogInformation("=== Spot Checks ===");

        SpotCheckStruct(registry, "Windows.Win32.Foundation.RECT",
            expectedFields: 4, expectedSize: 16, expectedPacking: 4);
        SpotCheckStruct(registry, "Windows.Win32.Foundation.POINT",
            expectedFields: 2, expectedSize: 8, expectedPacking: 4);
        SpotCheckHandle(registry, "Windows.Win32.Foundation.HWND",
            expectedInvalidValues: []);
        SpotCheckHandle(registry, "Windows.Win32.Foundation.HANDLE",
            expectedInvalidValues: new long[] { 0, -1 });
        SpotCheckEnum(registry, "Windows.Win32.Foundation.WIN32_ERROR",
            expectFlags: false);

        // Look for WNDCLASSEXW if available
        var wndClass = registry.Resolve("Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW", IRArchitecture.All);
        if (wndClass is StructType wcs)
        {
            Logger.LogInformation("  WNDCLASSEXW: {Fields} fields, {Size} bytes, StructSizeField={SizeField}",
                wcs.Members.Count, wcs.Size, wcs.StructSizeFieldName ?? "null");
        }

        // Show a few handles with free functions
        Logger.LogInformation("=== Sample Handles with Free Functions ===");
        foreach (var handle in registry.GetAll<HandleType>().Where(h => h.FreeFunc != null).Take(5))
        {
            Logger.LogInformation("  {FQN}: Free={FuncName} in {Apis}, InvalidValues=[{Values}]",
                handle.FQN, handle.FreeFunc!.Name, handle.FreeFunc.ApisFQN,
                string.Join(", ", handle.InvalidValues));
        }

        // Show a few enums
        Logger.LogInformation("=== Sample Enums ===");
        foreach (var enumType in registry.GetAll<EnumType>().Take(3))
        {
            Logger.LogInformation("  {FQN}: {Count} constants, IsFlags={IsFlags}, Underlying={Type}",
                enumType.FQN, enumType.Constants.Count, enumType.IsFlags, enumType.UnderlyingTypeName);
        }

        // Spot-check COM interfaces
        Logger.LogInformation("=== Sample COM Interfaces ===");
        var iUnknown = registry.Resolve("Windows.Win32.System.Com.IUnknown", IRArchitecture.All);
        if (iUnknown is ComInterfaceType iu)
        {
            Logger.LogInformation("  IUnknown: {Methods} methods, VTableOffset={Offset}, Base={Base}",
                iu.Methods.Count, iu.VTableOffset, iu.BaseInterfaceName ?? "null");
        }
        else
        {
            Logger.LogWarning("  IUnknown not found or wrong type");
        }

        var iDispatch = registry.Resolve("Windows.Win32.System.Com.IDispatch", IRArchitecture.All);
        if (iDispatch is ComInterfaceType id)
        {
            Logger.LogInformation("  IDispatch: {Methods} methods, VTableOffset={Offset}, Base={Base}",
                id.Methods.Count, id.VTableOffset, id.BaseInterfaceName ?? "null");
        }

        // Spot-check API types
        Logger.LogInformation("=== Sample API Types ===");
        var foundationApis = registry.Resolve("Windows.Win32.Foundation.Apis", IRArchitecture.All);
        if (foundationApis is ApiType fa)
        {
            Logger.LogInformation("  Foundation.Apis: {Constants} constants, {Methods} methods",
                fa.Constants.Count, fa.Methods.Count);
            var closeHandle = fa.Methods.FirstOrDefault(m => m.Name == "CloseHandle");
            if (closeHandle != null)
                Logger.LogInformation("    CloseHandle: {Params} params, DLL={Dll}, SetsLastError={SLE}",
                    closeHandle.Parameters.Count - 1, closeHandle.DllName, closeHandle.SetsLastError);
        }
        else
        {
            Logger.LogWarning("  Foundation.Apis not found or wrong type");
        }

        // Check for any architecture-specific structs
        Logger.LogInformation("=== Architecture-Specific Types ===");
        foreach (var t in registry.GetAll().Where(t => t.Arch != IRArchitecture.All).Take(5))
        {
            Logger.LogInformation("  {FQN} [{Arch}]", t.FQN, t.Arch);
            if (t is StructType st)
                Logger.LogInformation("    {Fields} fields, {Size} bytes", st.Members.Count, st.Size);
        }

        Logger.LogInformation("=== Validation Complete ===");
        return 0;
    }

    private static void SpotCheckStruct(TypeRegistry registry, string fqn,
        int expectedFields, int expectedSize, int expectedPacking)
    {
        var type = registry.Resolve(fqn, IRArchitecture.All);
        if (type is not StructType st)
        {
            Logger.LogWarning("  {FQN}: NOT FOUND or not a struct", fqn);
            return;
        }

        string status = (st.Members.Count == expectedFields && st.Size == expectedSize && st.PackingSize == expectedPacking)
            ? "OK" : "MISMATCH";
        Logger.LogInformation("  {FQN}: {Status} — {Fields} fields (exp {ExpFields}), {Size} bytes (exp {ExpSize}), packing {Packing} (exp {ExpPacking})",
            fqn, status, st.Members.Count, expectedFields, st.Size, expectedSize, st.PackingSize, expectedPacking);

        if (status == "MISMATCH")
        {
            foreach (var field in st.Members)
                Logger.LogInformation("    {Name}: {Type} offset={Offset} size={Size}",
                    field.Name, field.Type.DisplayName, field.Offset, field.Size);
        }
    }

    private static void SpotCheckHandle(TypeRegistry registry, string fqn, long[] expectedInvalidValues)
    {
        var type = registry.Resolve(fqn, IRArchitecture.All);
        if (type is not HandleType ht)
        {
            Logger.LogWarning("  {FQN}: NOT FOUND or not a handle", fqn);
            return;
        }

        bool valuesMatch = ht.InvalidValues.OrderBy(v => v).SequenceEqual(expectedInvalidValues.OrderBy(v => v));
        string status = valuesMatch ? "OK" : "MISMATCH";
        Logger.LogInformation("  {FQN}: {Status} — InvalidValues=[{Actual}] (exp [{Expected}]), FreeFunc={Free}",
            fqn, status,
            string.Join(", ", ht.InvalidValues), string.Join(", ", expectedInvalidValues),
            ht.FreeFunc?.Name ?? "null");
    }

    private static void SpotCheckEnum(TypeRegistry registry, string fqn, bool expectFlags)
    {
        var type = registry.Resolve(fqn, IRArchitecture.All);
        if (type is not EnumType et)
        {
            Logger.LogWarning("  {FQN}: NOT FOUND or not an enum", fqn);
            return;
        }

        string status = et.IsFlags == expectFlags ? "OK" : "MISMATCH";
        Logger.LogInformation("  {FQN}: {Status} — {Count} constants, IsFlags={IsFlags} (exp {ExpFlags}), Underlying={Type}",
            fqn, status, et.Constants.Count, et.IsFlags, expectFlags, et.UnderlyingTypeName);
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
