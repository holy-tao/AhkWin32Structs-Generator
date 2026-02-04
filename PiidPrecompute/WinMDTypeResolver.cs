using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Tao.AHK.WindowsBindGen.PiidPrecompute;

/// <summary>
/// Resolves type references across multiple WinMD assemblies.
/// </summary>
public class WinMDTypeResolver : IWinRTTypeResolver, IDisposable
{
    private readonly Dictionary<string, (MetadataReader Reader, PEReader PE)> _assemblies = new();
    private readonly Dictionary<string, WinRTSignature> _typeCache = new();
    private WinRTSignatureTypeProvider? _provider;

    public void LoadAssembly(string path)
    {
        var peReader = new PEReader(File.OpenRead(path));
        var reader = peReader.GetMetadataReader();

        // Index by assembly name
        var assemblyDef = reader.GetAssemblyDefinition();
        var name = reader.GetString(assemblyDef.Name);
        _assemblies[name] = (reader, peReader);

        // Also index all exported types
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            if (!typeDef.IsNested && (typeDef.Attributes & System.Reflection.TypeAttributes.Public) != 0)
            {
                var ns = reader.GetString(typeDef.Namespace);
                var typeName = reader.GetString(typeDef.Name);
                var fullName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
                _typeCache[fullName] = null!; // Placeholder, will be resolved lazily
            }
        }
    }

    public void SetProvider(WinRTSignatureTypeProvider provider)
    {
        _provider = provider;
    }

    public WinRTSignature ResolveTypeReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var typeRef = reader.GetTypeReference(handle);
        var ns = reader.GetString(typeRef.Namespace);
        var name = reader.GetString(typeRef.Name);
        var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

        // Check for well-known types
        if (TryGetWellKnownType(fullName, out var signature))
        {
            return signature;
        }

        // Find the assembly containing this type
        var resolutionScope = typeRef.ResolutionScope;
        string? assemblyName = resolutionScope.Kind switch
        {
            HandleKind.AssemblyReference => reader.GetString(
                reader.GetAssemblyReference((AssemblyReferenceHandle)resolutionScope).Name),
            HandleKind.ModuleReference => reader.GetString(
                reader.GetModuleReference((ModuleReferenceHandle)resolutionScope).Name),
            _ => null
        };

        // Search all loaded assemblies for the type
        foreach (var (asmReader, _) in _assemblies.Values)
        {
            var typeDef = FindTypeDefinition(asmReader, ns, name);
            if (typeDef.HasValue)
            {
                return _provider?.GetTypeFromDefinition(asmReader, typeDef.Value, 0)
                    ?? throw new NullReferenceException(nameof(_provider));
            }
        }

        throw new InvalidOperationException($"Could not resolve type reference: {fullName}");
    }

    private static TypeDefinitionHandle? FindTypeDefinition(MetadataReader reader, string ns, string name)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            if (reader.GetString(typeDef.Namespace) == ns &&
                reader.GetString(typeDef.Name) == name)
            {
                return handle;
            }
        }
        return null;
    }

    private static bool TryGetWellKnownType(string fullName, out WinRTSignature signature)
    {
        signature = fullName switch
        {
            // Primitive types
            "System.Guid" or "Windows.Foundation.Guid" => new WinRTSignature.Primitive("g16"),
            "System.Object" or "Windows.Foundation.IInspectable" => new WinRTSignature.Primitive("cinterface(IInspectable)"),
            "System.String" => new WinRTSignature.Primitive("string"),
            
            // Foundation structs
            "Windows.Foundation.HResult" => new WinRTSignature.Struct(
                "Windows.Foundation.HResult",
                ImmutableArray.Create<WinRTSignature>(new WinRTSignature.Primitive("i4"))),
            "Windows.Foundation.DateTime" or "System.DateTimeOffset" => new WinRTSignature.Struct(
                "Windows.Foundation.DateTime",
                ImmutableArray.Create<WinRTSignature>(new WinRTSignature.Primitive("i8"))),
            "Windows.Foundation.TimeSpan" or "System.TimeSpan" => new WinRTSignature.Struct(
                "Windows.Foundation.TimeSpan",
                ImmutableArray.Create<WinRTSignature>(new WinRTSignature.Primitive("i8"))),
            "Windows.Foundation.Point" => new WinRTSignature.Struct(
                "Windows.Foundation.Point",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"))),
            "Windows.Foundation.Size" => new WinRTSignature.Struct(
                "Windows.Foundation.Size",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"))),
            "Windows.Foundation.Rect" => new WinRTSignature.Struct(
                "Windows.Foundation.Rect",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Vector2" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Vector2",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Vector3" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Vector3",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Vector4" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Vector4",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Matrix3x2" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Matrix3x2",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Matrix4x4" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Matrix4x4",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Plane" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Plane",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"))),
            "System.Numerics.Quaternion" => new WinRTSignature.Struct(
                "Windows.Foundation.Numerics.Quaternion",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"),
                    new WinRTSignature.Primitive("f4"), new WinRTSignature.Primitive("f4"))),

            // .NET generic collection interfaces → WinRT collection pinterface GUIDs
            // These return Guid signatures that will be wrapped in PInterface by GetGenericInstantiation
            
            // IEnumerable<T> → IIterable<T>
            "System.Collections.Generic.IEnumerable`1" => 
                new WinRTSignature.Guid(new System.Guid("faa585ea-6214-4217-afda-7f46de5869b3")),
            
            // IEnumerator<T> → IIterator<T>
            "System.Collections.Generic.IEnumerator`1" => 
                new WinRTSignature.Guid(new System.Guid("6a79e863-4300-459a-9966-cbb660963ee1")),
            
            // IList<T> → IVector<T>
            "System.Collections.Generic.IList`1" => 
                new WinRTSignature.Guid(new System.Guid("913337E9-11A1-4345-A3A2-4E7F956E222D")),
            
            // IReadOnlyList<T> → IVectorView<T>
            "System.Collections.Generic.IReadOnlyList`1" => 
                new WinRTSignature.Guid(new System.Guid("BBE1FA4C-B0E3-4583-BAEF-1F1B2E483E56")),
            
            // IDictionary<K,V> → IMap<K,V>
            "System.Collections.Generic.IDictionary`2" => 
                new WinRTSignature.Guid(new System.Guid("3C2925FE-8519-45C1-AA79-197B6718C1C1")),
            
            // IReadOnlyDictionary<K,V> → IMapView<K,V>
            "System.Collections.Generic.IReadOnlyDictionary`2" => 
                new WinRTSignature.Guid(new System.Guid("E480CE40-A338-4ADA-ADCF-272272E48CB9")),
            
            // KeyValuePair<K,V> → IKeyValuePair<K,V>
            "System.Collections.Generic.KeyValuePair`2" => 
                new WinRTSignature.Guid(new System.Guid("02B51929-C1C4-4A7E-8940-0312B5C18500")),
            
            // ICollection<T> → IVector<T> (WinRT doesn't have ICollection, maps to IVector)
            "System.Collections.Generic.ICollection`1" => 
                new WinRTSignature.Guid(new System.Guid("913337E9-11A1-4345-A3A2-4E7F956E222D")),
            
            // IReadOnlyCollection<T> → IVectorView<T>
            "System.Collections.Generic.IReadOnlyCollection`1" => 
                new WinRTSignature.Guid(new System.Guid("BBE1FA4C-B0E3-4583-BAEF-1F1B2E483E56")),

            // WinRT collection interfaces (native names)
            "Windows.Foundation.Collections.IIterable`1" => 
                new WinRTSignature.Guid(new System.Guid("FAA585EA-6214-4217-AFDA-7F46DE5869B3")),
            "Windows.Foundation.Collections.IIterator`1" => 
                new WinRTSignature.Guid(new System.Guid("6A79E863-4300-459A-9966-CBB660963EE1")),
            "Windows.Foundation.Collections.IVector`1" => 
                new WinRTSignature.Guid(new System.Guid("913337E9-11A1-4345-A3A2-4E7F956E222D")),
            "Windows.Foundation.Collections.IVectorView`1" => 
                new WinRTSignature.Guid(new System.Guid("BBE1FA4C-B0E3-4583-BAEF-1F1B2E483E56")),
            "Windows.Foundation.Collections.IMap`2" => 
                new WinRTSignature.Guid(new System.Guid("3C2925FE-8519-45C1-AA79-197B6718C1C1")),
            "Windows.Foundation.Collections.IMapView`2" => 
                new WinRTSignature.Guid(new System.Guid("E480CE40-A338-4ADA-ADCF-272272E48CB9")),
            "Windows.Foundation.Collections.IKeyValuePair`2" => 
                new WinRTSignature.Guid(new System.Guid("02B51929-C1C4-4A7E-8940-0312B5C18500")),
            "Windows.Foundation.Collections.IObservableVector`1" => 
                new WinRTSignature.Guid(new System.Guid("5917EB53-50B4-4A0D-B309-65862B3F1DBC")),
            "Windows.Foundation.Collections.IObservableMap`2" => 
                new WinRTSignature.Guid(new System.Guid("65DF2BF5-BF39-41B5-AEBC-5A9D865E472B")),
            
            // Foundation async interfaces
            "Windows.Foundation.IAsyncAction" => 
                new WinRTSignature.Guid(new System.Guid("5A648006-843A-4DA9-865B-9D26E5DFAD7B")),
            "Windows.Foundation.IAsyncOperation`1" => 
                new WinRTSignature.Guid(new System.Guid("9FC2B0BB-E446-44E2-AA61-9CAB8F636AF2")),
            "Windows.Foundation.IAsyncActionWithProgress`1" => 
                new WinRTSignature.Guid(new System.Guid("1F6DB258-E803-48A1-9546-EB7353398884")),
            "Windows.Foundation.IAsyncOperationWithProgress`2" => 
                new WinRTSignature.Guid(new System.Guid("B5D036D7-E297-498F-BA60-0289E76E23DD")),
            "Windows.Foundation.IReference`1" => 
                new WinRTSignature.Guid(new System.Guid("61C17706-2D65-11E0-9AE8-D48564015472")),
            "Windows.Foundation.IReferenceArray`1" => 
                new WinRTSignature.Guid(new System.Guid("61C17707-2D65-11E0-9AE8-D48564015472")),
            "System.Nullable`1" => 
                new WinRTSignature.Guid(new System.Guid("61C17706-2D65-11E0-9AE8-D48564015472")),
            
            // Event handlers
            "Windows.Foundation.EventHandler`1" => 
                new WinRTSignature.Guid(new System.Guid("9DE1C534-6AE1-11E0-84E1-18A905BCC53F")),
            "System.EventHandler`1" =>
                new WinRTSignature.Guid(new System.Guid("9DE1C534-6AE1-11E0-84E1-18A905BCC53F")),
            "Windows.Foundation.TypedEventHandler`2" =>
                new WinRTSignature.Guid(new System.Guid("9DE1C534-6AE1-11E0-84E1-18A905BCC53F")),
            "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken" => new WinRTSignature.Struct(
                "Windows.Foundation.EventRegistrationToken",
                ImmutableArray.Create<WinRTSignature>(new WinRTSignature.Primitive("i8"))),

            // Non-generic bindable interfaces
            "System.Collections.IEnumerable" => 
                new WinRTSignature.Guid(new System.Guid("036D2C08-DF29-41AF-8AA2-D774BE62BA6F")), // IBindableIterable
            "System.Collections.IList" => 
                new WinRTSignature.Guid(new System.Guid("393DE7DE-6FD0-4C0D-BB71-47244A113E93")), // IBindableVector
            "System.Collections.Specialized.INotifyCollectionChanged" => 
                new WinRTSignature.Guid(new System.Guid("28B167D5-1A31-465B-9B25-D5C3AE686C40")),
            "System.ComponentModel.INotifyPropertyChanged" => 
                new WinRTSignature.Guid(new System.Guid("CF75D69C-F2F4-486B-B302-BB4C09BAEBFA")),

            // System.Exception → Windows.Foundation.HResult (when used as type)
            "System.Exception" => new WinRTSignature.Struct(
                "Windows.Foundation.HResult",
                ImmutableArray.Create<WinRTSignature>(new WinRTSignature.Primitive("i4"))),

            // Uri mapping
            "System.Uri" => new WinRTSignature.RuntimeClass(
                "Windows.Foundation.Uri",
                new WinRTSignature.Guid(new System.Guid("9E365E57-48B2-4160-956F-C7385120BBFC"))), // IUriRuntimeClass

            // Type mapping
            "System.Type" => new WinRTSignature.Struct(
                "Windows.UI.Xaml.Interop.TypeName",
                ImmutableArray.Create<WinRTSignature>(
                    new WinRTSignature.Primitive("string"),
                    new WinRTSignature.Enum("Windows.UI.Xaml.Interop.TypeKind", new WinRTSignature.Primitive("i4")))),

            // IDisposable → IClosable
            "System.IDisposable" =>
                new WinRTSignature.Guid(new System.Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")),

            // ICommand
            "System.Windows.Input.ICommand" =>
                new WinRTSignature.Guid(new System.Guid("E5AF3542-CA67-4081-995B-709DD13792DF")),

            // Compiler-generated modifier types (used in C++ metadata, should be ignored)
            "System.Runtime.CompilerServices.IsConst" => new WinRTSignature.Primitive("cinterface(IInspectable)"),

            // Event handlers/delegates
            "System.ComponentModel.PropertyChangedEventHandler" =>
                new WinRTSignature.Guid(new System.Guid("50F19C16-0A22-4D8E-A089-1EA9951657D2")),
            "System.Collections.Specialized.NotifyCollectionChangedEventHandler" =>
                new WinRTSignature.Guid(new System.Guid("CA10B37C-F382-4591-8557-5E24965279B0")),

            // Event args
            "System.ComponentModel.PropertyChangedEventArgs" => new WinRTSignature.RuntimeClass(
                "Windows.UI.Xaml.Data.PropertyChangedEventArgs",
                new WinRTSignature.Guid(new System.Guid("4F33A9A0-5CF4-47A4-B16F-D7FAAF17457E"))),
            "System.Collections.Specialized.NotifyCollectionChangedEventArgs" => new WinRTSignature.RuntimeClass(
                "Windows.UI.Xaml.Interop.NotifyCollectionChangedEventArgs",
                new WinRTSignature.Guid(new System.Guid("4CF68D33-E3F2-4964-B85E-945B4F7E2F21"))),

            // Enums
            "System.AttributeTargets" => new WinRTSignature.Enum(
                "Windows.Foundation.Metadata.AttributeTargets",
                new WinRTSignature.Primitive("u4")),
            "System.Collections.Specialized.NotifyCollectionChangedAction" => new WinRTSignature.Enum(
                "Windows.UI.Xaml.Interop.NotifyCollectionChangedAction",
                new WinRTSignature.Primitive("i4")),

            _ => null!
        };

        return signature != null;
    }

    public void Dispose()
    {
        foreach (var (_, pe) in _assemblies.Values)
        {
            pe.Dispose();
        }
        _assemblies.Clear();
        GC.SuppressFinalize(this);
    }
}