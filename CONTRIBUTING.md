# Contributing
Contributions are welcome, though I'll admit that I haven't kept this repository as clean as I'd have liked and the generator is not terribly well documented. Some simple changes that can be made through configuration are documented below.

## Table of Contents
- [Table of Contents](#table-of-contents)
- [Running the Generator](#running-the-generator)
- [Manually generated metadata](#manually-generated-metadata)
  - [External (.NET) Type Mappings](#external-net-type-mappings)
  - [Extensions](#extensions)

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

Note that [`System.Guid`](https://learn.microsoft.com/en-us/dotnet/api/system.guid?view=net-10.0) has special handling and should never be included in a type mapping.

### Extensions
Extensions are custom code added to generated types. Extensions can be added to Structs and Enums.

> [!Important]
> You must add tests for extension methods in the bindings project in addition to the extension definition files here.

Extensions are defined in [YAML](https://yaml.org/) files. The generator will read files in the [/extensions](./metadata/extensions/) subdirectory of whichever directory is passed in as its metadata directory with the extensions `.yml` and `.yaml`. The file format is as follows (example is a snippet of the [RECT / RECTL](./metadata/RECT.yml) extension definition):
```yaml
# Fully qualified names of types to which the extensions should be added
add-to:
  - Windows.Win32.Foundation.RECT
  - Windows.Win32.Foundation.RECTL

# Fully qualified names of types for which #Include directives must be added
# The generator will resolve these to relative paths when it runs
requires:
  - Windows.Win32.Graphics.Gdi.Apis
  - Windows.Win32.Foundation.POINT

# The code to add to the generated type. Code is added to the body of the generated class
code: |
    height => this.top - this.bottom

    width => this.right - this.left

    area => this.width * this.height

    ...
```

#### Aliases
The generator suports the following aliases, using the `$Name` convention (like bash or PowerShell - this is because `%%` is valid AHK syntax and would make parsing a nightmare). All aliases are case-sensitive:
- `$Class`: the name of the class to which the extensions are being added