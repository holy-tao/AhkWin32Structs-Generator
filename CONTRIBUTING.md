# Contributing
Contributions are welcome, though I'll admit that I haven't kept this repository as clean as I'd have liked and the generator is not terribly well documented. Some simple changes that can be made through configuration are documented below.

## Table of Contents
- [Table of Contents](#table-of-contents)
- [Running the Generator](#running-the-generator)
- [Manually generated metadata](#manually-generated-metadata)
  - [External (.NET) Type Mappings](#external-net-type-mappings)
  - [Extensions](#extensions)
- [Maintenance](#maintenance)
  - [Generating Parameterized Interface IDs (PIIDs)](#generating-parameterized-interface-ids-piids)
  - [Updating the ApiDocs](#updating-the-apidocs)
  - [Updating the Metadata](#updating-the-metadata)

## Running the Generator
The generator compiles to a command-line program that can be run like so:

```cmd
AhkWin32Structs.exe <metadataDirectory> <outputDirectory>
```

Ideally, the output directory should be the root of a local clone of the [bindings repository](https://github.com/holy-tao/AhkWin32Projection). The metadata directory should point to a directory containing the metadata you want to generate bindings for. The folder structure of that directory is as follows:

```
metadata/
├── extensions/
│   ├── ext1.yml
│   ├── ext2.yml
│   └── <etc>
├── <.winmd files with metadata>
├── <.version files with version strings>
├── apidocs.msgpack
├── piids.yml
└── type-mappings.yml
```

The generator will generate bindings for all .winmd files in the metadata directory. The .version files are only used for generating `version.ini`; this is because the assembly versions of the metadata files are usually nonsense (v0.0.0.0 / v255.255.255.255). The extensions subdirectory can contain any number of [extension definitions](#extensions) as .yml or .yaml files (all others are ignored); type-mappings.yml contains any [external type mappings](#external-net-type-mappings) required for the generator.

In addition to the actual bindings, the generator will produce a file called `generation.log` in the output directory which will include a log of every file generated, notes on where external assemblies are loaded from, and any errors encountered during generation.

## Manually generated metadata

Some of the generator's behavior can be modified with manual build.

### External (.NET) Type Mappings
Windows metadata, especially WinRT metadata, may reference types not in the WinRT or Win32 namespaces. The WinRT metadata does this a lot, since it was generated originally to enable .NET interop. If these types have equivalent WinRT or Win32 concepts, they can be mapped to those types using [type-mappings.yml](./metadata/type-mappings.yml), for example:

```yml
System.Collections.Generic.IEnumerable:
  Assembly: Windows
  Namespace: Windows.Foundation.Collections
  Name: IIterable`1
```

This will cause the generator to treat all instances of and references to the .NET type [`IEnumerable`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ienumerable-1?view=dotnet-uwp-10.0&preserve-view=true) as instances or references to the WinRT type [`IIterable`](https://learn.microsoft.com/en-us/uwp/api/windows.foundation.collections.iiterable-1?view=winrt-26100). See also [.NET mappings of WinRT types in C#/WinRT - Windows Apps | Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/platform/csharp-winrt/net-mappings-of-winrt-types)

> [!IMPORTANT]
> Mappings for generic types _must_ include the backtick number indicating the number of generic arguments, but the fqn entry _must not_ include it. In the above example, `Name` must include the ``IIterable`1``, but the fqn for the type we are mapping _from_, `IEnumerable`, does not contain the generic argument count.

Note that `Assembly` does not include any file extensions (.dll / .winmd). Currently, it's only possible to map from one type to another type, the generator has no mechanism to map primitives to types or vice versa (e.g. `System.Object -> Windows.Win32.System.WinRT.IInspectable`); such mappings wil require code changes to the genererator.

> [!CAUTION]
> [`System.Guid`](https://learn.microsoft.com/en-us/dotnet/api/system.guid?view=net-10.0) has special handling and must never be included in a type mapping.

Type mappings can also be used to force the generator to use a type from one assembly even when it is redefined elsewhere, for example `RECT` and `HRESULT`, which are defined both in the Win32 and WinRT metadata. The Windows Runtime types reference the WinRT version, but to avoid polluting the namespace and causing duplicate declaration errors, we redirect all references to these structs to the Win32 versions. Note that this won't stop the generator for generating a file for the type, but it will prevent consumers from automatically `#Include`-ing them.

### Extensions
Extensions are custom code added to generated types. Extensions can be added to Structs, Enums, COM Interfaces, and Windows Runtime classes.

> [!Important]
> You must add tests for extension methods in the bindings project in addition to the extension definition files here.

Extensions run the gamut from nifty helpers (see [`COLORREF`](./metadata/extensions/COLORREF.yml)) to core parts of the projection - the [code](./metadata/extensions/IIterator.yml) that makes `IIterator` objects iterable in native AHK for-loops lives in an extension file, as do `IUnknown::As` (a core utilty that enables casting between interfaces and WinRT classes) and the core [`BSTR`](./metadata/extensions/BSTR.yml) and [`HSTRING`](./metadata/extensions/HSTRING.yml) methods. As such, some parts of the generator rely on extensions to be in place, and other extensions may rely on each other. All this to say, extension changes are code changes and must be tested.

#### Writing Extensions 

Extensions are defined in [YAML](https://yaml.org/) files. The generator will read files in the [/extensions](./metadata/extensions/) subdirectory of whichever directory is passed in as its metadata directory with the extensions `.yml` and `.yaml`. The definition has three parts:

| Name | Type | Description |
| ---- | ---- | ----------- |
| `add-to` | sequence | The fully qualified names of all types to which code must be added
| `requires` | sequence | The fully qualified names of all types which must be included in generated files for the extension to work. This should include all types required by your extension, even if the types it extends already include them, as this may change in the future. The generator will not produce duplicate `#Include` statements. <br><br>To indicate that *nothing* needs to be imported, specify an empty sequence: `[]`.
| `code` | string | The actual code to add to the class. Oftentimes this can be written directly into the relevant file and copy/pasted into extension YAML without modification. This code is added to the end of the files specified in `add-to` without modification. <br><br> See [yaml multiline strings](https://yaml-multiline.info/) for details on the syntax, or just use the pipe (`\|`) and don't worry about it. 

A single extension file can only include one extension definition, though said definition can apply to as many types as you want. Note it is not currently possible to add extensions to nested structs or to extend existing methods like `__New`.

<details>

<summary>Example: the extension for AsyncAction / Operation Await()</summary>

```yaml
add-to:
  - Windows.Foundation.IAsyncAction
  - Windows.Foundation.IAsyncActionWithProgress
  - Windows.Foundation.IAsyncOperation
  - Windows.Foundation.IAsyncOperationWithProgress
requires: 
  - Windows.Foundation.IAsyncInfo
code: |
    /**
     * Synchronously waits until the $Class is complete. Best for actions that you expect to complete very
     * quickly or which *must* finish before some other action can continue.
     *
     * @param {Integer} timeout If greater than zero, the maximum number of seconds to wait
     * @param {Integer} interval The number of milliseconds to wait between checks (default: 10)
     * @returns {Generic} The result of the $Class
     */
    Await(timeout := 0, interval := 10) {
        info := this.As(IAsyncInfo)

        start := A_Now
        while(!info.Status) {
            if(timeout > 0 && DateDiff(start, A_Now, "seconds") > timeout) {
                throw TimeoutError(type(this) " timed out", -1, timeout)
            }
            sleep(10)
        }

        return this.GetResults()
    }
```

</details>

#### Aliases
The generator suports the following aliases, using the `$Name` convention (like bash or PowerShell - this is because `%%` is valid AHK syntax and would make parsing a nightmare). All aliases are case-sensitive:
- `$Class`: the name of the class to which the extensions are being added. This can be used for documentation, or to access the static members of the class on which the type is being added.

## Maintenance

### Generating Parameterized Interface IDs (PIIDs)

The Windows Runtime handles generics (also known as [parameterized types](https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#parameterized-types), since the type itself is partially defined by some parameters) by assigning generic interfaces parameterized interface ids according to a [strict set of rules](https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#guid-generation-for-parameterized-types). These `piid`s are Guids which can the be queried for using `QueryInterface` like any normal COM interface IID.

The projection currently has no mechanism for generating these piids at runtime; this does mean that it's not possible for consumers to query for arbitrary generic types like `IReference<String>`. Instead, piids are pregenerated using the [PiidPrecompute](./PiidPrecompute/) module of the generator and treated as "compile" time constants. The piid generator will loop over all of the type specifications in `windows.winmd` to discover all generic types that are actually used in the metadata, then precompute their piids. The output is [`piids.yml`](./metadata/piids.yml), which the generator parses into a lookup table.

After updating the WinRT metadata, piids may need to be recalculated if new parameterized types were added. See the [README](./PiidPrecompute/README.md) for the piid generator for details. Generation can be run automatically on a local checkout using [`Precompute-Piids.ps1`](./Scripts/Precompute-Piids.ps1).

### Updating the ApiDocs

`apidocs.msgpack` is generated using a fork of Microsoft's documentation scraper modified to work with the WinRT documentation. To update the documentation, simply re-run the generator on an updated version of the documentation repositories.

> [!IMPORTANT]
> TAO TODO UPDATE WITH LINK TO FORK AND BETTER INSTRUCTIONS

### Updating the Metadata

Routine updates to the metadata are automated via GitHub actions. The action will automatically regenerate the bindings and submit a pull request into the AHK repository with the changes. Automated metadata updates us the NuGet packages (see below).

> [!NOTE]
> The NuGet package versions will sometimes lag behind the versions on GitHub, particularly for the Win32 and WDK metadata. If this is a problem, it's perfectly fine to update the `.winmd` files manually, just be sure to also update the relevant `.version` file to prevent automated update from running unnecessarily.

You can update the metadata manually using the scripts in the [`Scripts`](./Scripts/) directory. These scripts pull the latest `.winmd` files out of the relevant NuGet packages and should be safe to run on your base machine or in a ci/cd pipeline.

| Script | NuGet Package | Notes |
| ------ | ------------- | ----------- |
| [`Get-Win32Metadata`](./Scripts/Get-Win32Metadata.ps1) | [`Microsoft.Windows.SDK.Win32Metadata`](https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/) | The Win32 metadata is updated the most frequently of the three by far, sometimes multiple times a month. | 
| [`Get-WdkMetadata`](./Scripts/Get-WdkMetadata.ps1) | [`Microsoft.Windows.WDK.Win32Metadata`](https://www.nuget.org/packages/Microsoft.Windows.WDK.Win32Metadata/) | The WDK metadata is updated very infrequently and is a community project; Microsoft has little involvement. | 
| [`Get-WinRtMetadata`](./Scripts/Get-WinRtMetadata.ps1) | [`Microsoft.Windows.SDK.Contracts`](https://www.nuget.org/packages/Microsoft.Windows.SDK.Contracts/) | Updating the Windows Runtime metadata requires an additional step to combine the downloaded .winmd files into the single `Windows.winmd` file you see in this repository. This requires [mdmerge.exe](https://learn.microsoft.com/en-us/windows/win32/midl/mdmerge-and-metadata-files) to be installed on your system; it is installed with the Windows SDK and is typically located in a subdirectory of `C:\Program Files (x86)\Windows Kits\10\bin\`. <br><br> WinRT metadata updates are extremely infrequent and will generally be bundled with Windows OS updates. |