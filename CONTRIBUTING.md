# Contributing
Contributions are welcome, though I'll admit that I haven't kept this repository as clean as I'd have liked and the generator is not terribly well documented. This file documents the required ongoing maintenance tasks and changes that can be made through configuration.

## Table of Contents
- [Table of Contents](#table-of-contents)
- [Running the Generator](#running-the-generator)
  - [Pipeline Overview](#pipeline-overview)
  - [Generator Flags](#generator-flags)
  - [Validating Generated AHK code](#validating-generated-ahk-code)
- [Manual Configuration](#manual-configuration)
  - [Extensions](#extensions)
  - [Overrides](#overrides)
  - [Reserved Names](#reserved-names)
- [Maintenance](#maintenance)
  - [Updating the Metadata](#updating-the-metadata)

## Running the Generator
The generator compiles to a command-line program that can be run like so:

```cmd
AhkWin32Structs.exe <metadataDirectory> <outputDirectory>
```

Ideally, the output directory should be the root of a local clone of the [bindings repository](https://github.com/holy-tao/AhkWin32Projection). This is included as a submodule of the generator by default. If you've initialized the submodule, you can run the generator in release mode by simply runing [`Run-Generator-Release.ps1`](./Scripts/Run-Generator-Release.ps1). In vscode, the generator can be run in debug mode using the launch option "Build and Generate AHK".

The metadata directory should point to a directory containing the metadata you want to generate bindings for. The folder structure of that directory is as follows:

```
metadata/
├── extensions/
│   ├── ext1.yml
│   ├── ext2.yml
│   └── <etc>
├── overrides/
│   ├── override1.yml
│   └── <etc>
├── <.winmd files with metadata>
├── <.version files with version strings>
├── ahk-reserved-names.yml
└── apidocs.msgpack
```

The generator will generate bindings for all .winmd files in the metadata directory. The .version files are only used for generating `version.ini`; this is because the assembly versions of the metadata files are usually nonsense (v0.0.0.0 / v255.255.255.255). The extensions subdirectory can contain any number of [extension definitions](#extensions) as .yml or .yaml files (all others are ignored); similarly, the overrides file can contain zero or more [override definitions](#overrides) as .yml or .yaml files. The [reserved names file](#reserved-names) is used to prevent duplicate declaration errors and parameter shadowing of AHK builtins, which can cause unexpected and hard to debug behavior.

### Pipeline Overview

The generator is a three-phase pipeline:

1. **Extract**: `.winmd` -> TypeRegistry (a pure intermediate representation)
2. **Transform**: TypeRegistry -> modified TypeRegistry
   - This is where extensions and overrides are applied (filtering is done at emission)
3. **Emit**: Write `.ahk` files from the TypeRegistry

#### Extract
Reads `.winmd` files and API documentation, decoding every type definition, field, method, parameter, and attribute into a pure-data intermediate representation (IR). The result is a `TypeRegistry` — a dictionary of `Win32Type` objects keyed by fully qualified name and target architecture. At this point, the IR has zero references to the underlying `MetadataReader`; all names, sizes, offsets, and documentation are resolved into plain data.

#### Transform
Applies modifications to the IR before code generation. Transforms run in order:

1. **Overrides** — YAML-driven one-off corrections (skip types, mark parameters as reserved, set struct-size fields, clone methods between API types). See [Overrides](#overrides).
2. **Extensions** — YAML-driven code injection (add helper methods, properties, or custom constructors to generated types). See [Extensions](#extensions).

Both are configured via files in the metadata directory and are applied by mutating the `TypeRegistry` in place. This is the integration point for any manual configuration. Note that reserved name deconfliction happens during *extraction*.

#### Emit
Walks the `TypeRegistry` and generates `.ahk` files. Each type kind has a dedicated emitter (`EnumEmitter`, `StructEmitter`, `HandleEmitter`, `ApiTypeEmitter`, `ComInterfaceEmitter`). Emission is split into two sub-phases for performance: first, all types are emitted to in-memory strings in parallel; then, files are written to disk with parallel I/O. A `version.ini` file is also written with assembly and package version information, in case consumers want it.

### Generator Flags

The generator has some optional flags that can control its behavior.

| Flag | Description |
| ---- | ----------- |
| `--namespace` / `-n` | Filter by namespace prefix (prefix match). Can be specified multiple times. Example: `-n Windows.Win32.Foundation`. Useful when debugging to avoid running a full emit. |
| `--assembly` / `-a` | Filter by assembly name (exact match on the `.winmd` filename without extension). Can be specified multiple times. |
| `--log-level` | Minimum log level. One of `Trace`, `Debug`, `Information` (default), `Warning`, `Error`, `Critical`. |
| `--log-file` | Write log output to a file in addition to the console. |
| `--max-parallelism` | Maximum degree of parallelism for extraction and emission. Defaults to the CPU core count. Set to `-1` for no limit. |

### Validating Generated AHK code

Validating outputs is difficult due to the sheer scale of this project. 

The projection project has a suite of tests you can run to verify that the basics are working. These will catch changes that totally break generation of e.g. all functions or structs, but cannot comprehensively test the generated bindings.

You can also run [`ValidateAhk.ps1`](./Validator/ValidateAhk.ps1) to validate the AutoHotkey for sytax errors and the following load-time [warnings](https://www.autohotkey.com/docs/v2/lib/_Warn.htm):

- [`VarUnset`](https://www.autohotkey.com/docs/v2/lib/_Warn.htm#VarUnset)
- [`Unreachable`](https://www.autohotkey.com/docs/v2/lib/_Warn.htm#Unreachable)

The most common of these is a `VarUnset` warning or load-time error caused by malformed or missing `#Include` statements for required types. 

The generator should eventually enable [`LocalSameAsGlobal`](https://www.autohotkey.com/docs/v2/lib/_Warn.htm#LocalSameAsGlobal) warnings to ensure that no built-ins or Win32 types are shadowed incorrectly.

The script works by running AutoHotkey64.exe with the [`/Validate`](https://www.autohotkey.com/docs/v2/Scripts.htm#cmd) flag. As such, it is extremely slow. You may also see a series of duplicate warnings or errors, since alerts caused in one file will generally also get flagged in all files that include it.

> [!NOTE]
> A version of the validator script also runs over all modified `.ahk` files included in pull requests opened against the bindings repository in GitHub actions.

There are a also few vscode launch configurations that you can use to validate files in a specific directory, which can speed things up considerably when spot checking generator changes.

## Manual Configuration

Some of the generator's behavior can be modified with manual build.

### Extensions
Extensions are custom code added to generated types. Extensions can be added to Structs, Enums, and COM Interfaces.

> [!Important]
> You must add tests for extension methods in the bindings project in addition to the extension definition files here.

Extensions run the gamut from nifty helpers (see [`COLORREF`](./metadata/extensions/COLORREF.yml)) to core parts of the projection like [`NTSTATUS`](./metadata/extensions/NTSTATUS.yml) error checking code. As such, some parts of the generator rely on extensions to be in place, and other extensions may rely on each other. All this to say, extension changes are code changes and must be tested.

#### Writing Extensions 

Extensions are defined in [YAML](https://yaml.org/) files. The generator will read files in the [/extensions](./metadata/extensions/) subdirectory of whichever directory is passed in as its metadata directory with the extensions `.yml` and `.yaml`. The definition has three parts:

| Name | Type | Description |
| ---- | ---- | ----------- |
| `add-to` | sequence | The fully qualified names of all types to which code must be added
| `requires` | sequence | The fully qualified names of all types which must be included in generated files for the extension to work. This should include all types required by your extension, even if the types it extends already include them, as this may change in the future. The generator will not produce duplicate `#Include` statements. <br><br>To indicate that *nothing* needs to be imported, specify an empty sequence: `[]` or omit the key.
| `code` | string | The actual code to add to the class. Oftentimes this can be written directly into the relevant file and copy/pasted into extension YAML without modification. This code is added to the end of the files specified in `add-to` without modification. <br><br> See [yaml multiline strings](https://yaml-multiline.info/) for details on the syntax, or just use the pipe (`\|`) and don't worry about it. 

A single extension file can only include one extension definition, and said definition can apply to as many types as you want. Note it is not currently possible to extend existing methods like `__New`, though you can add such methods to types that don't already have them.

<details>

<summary><b>Example: the extension for BSTR's helper methods</b></summary>

```yaml
add-to:
  - Windows.Win32.Foundation.BSTR
requires:
  - Windows.Win32.Foundation.Apis
code: |
    /**
     * @readonly The length of the allocated string in characters, not including the null terminator
     * @type {Integer}
     */
    length => Foundation.SysStringLen(this)

    /**
     * @readonly The length of the allocated string in bytes, not including the null terminator
     * @type {Integer}
     */
    byteLength => Foundation.SysStringByteLen(this)

    /**
     * Creates a new BSTR from an existing AHK string
     * @param {String | Integer} str the string to allocate, or a pointer to a string to allocate
     */
    static Alloc(str){
        return Foundation.SysAllocString(str)
    }

    /**
     * Changes the contents of the BSTR, resizing it if necessary
     * @param {String} str the new contents of the BSTR
     */
    ReAlloc(str){
        result := Foundation.SysReallocString(this, str)
        if(result == 0)
            throw MemoryError("Not enough memory to reallocate string")
    }

    /**
     * Gets the value of the BSTR as a native AHK string
     * @returns {String}
     */
    ToString(){
        return StrGet(this.value, this.length, "UTF-16")
    }
```

</details>

#### Aliases
The generator suports the following aliases, using the `$Name` convention (like bash or PowerShell - this is because `%%` is valid AHK syntax and would make parsing a nightmare). All aliases are case-sensitive.


Alias | Scope | Description
------|------------|--------------
`$Class` | All | The name of the class to which the extensions are being added. This can be used for documentation, or to access the static members of the class on which the type is being added.
`$Namespace` | All | The namespace of the type to which the extension is being added. Only useful for documentation.
`$CLSID` and `$IID` | COM Interfaces | For COM interfaces which have them, the CLSID or IID in the form `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`. These values are *unqoted*. If the interface does not have the specified identifier, it is blank in the emitted code.
`$Arch` | All | The architecture(s) that the type applies to. Only useful for a limited set of architecture-specific structs. This will be something like `X86`, `Arm64`, or in almost all cases, `All`.


### Overrides
Overrides are one-off corrections to the extracted metadata. They run in the transform phase *before* extensions, modifying the intermediate representation (IR) in the `TypeRegistry`. Override files live in the [`/overrides`](./metadata/overrides/) subdirectory of the metadata directory and use `.yml` or `.yaml` extensions. Overrides are intentionally restrictive, intended for cases where the metadata isn't optimal for AutoHotkey projection specifically. Most problems with the metadata should be written up as issues against the win32metadata repository.

Each file is a YAML list of type-scoped entries. The `type` key is always required and must be a fully qualified type name from the metadata. All other keys are optional:

| Key | Scope | Description |
| --- | ----- | ----------- |
| `skip` | type | `true` to remove the type from generation entirely. Use with caution; references to it will be broken. |
| `struct-size-field` | type (struct) | Name of the field the emitter should auto-initialize to `sizeof` in `__New()` (equivalent to `[StructSizeFieldAttribute]`). A warning is logged if the type is not a struct. |
| `fields.<name>.add-attributes` | field | List of `MemberFlags` to add (e.g., `Reserved`, `Deprecated`). |
| `methods.<name>.skip` | method | `true` to remove a single method from an `Apis` type. |
| `methods.<name>.parameters.<name>.add-attributes` | parameter | List of `ParameterFlags` to add (e.g., `Reserved`). |
| `add-methods` | type (Apis) | List of `{from, name}` entries that clone a method from another `Apis` type into this one. Useful when a function logically belongs in multiple namespaces. |

<details>

<summary><b>Example: clone FreeLibrary into LibraryLoader</b></summary>

```yaml
# FreeLibrary lives in Foundation.Apis but LoadLibrary is in LibraryLoader.Apis.
# This override makes FreeLibrary available in both.
- type: Windows.Win32.System.LibraryLoader.Apis
  add-methods:
    - from: Windows.Win32.Foundation.Apis
      name: FreeLibrary
```

</details>

<details>

<summary><b>Example: mark a parameter as Reserved</b></summary>

```yaml
# dwFlags exists but has no defined flags — hide it from the user.
- type: Windows.Win32.Security.Cryptography.Apis
  methods:
    BCryptCloseAlgorithmProvider:
      parameters:
        dwFlags:
          add-attributes: [Reserved]
```

</details>

<details>

<summary><b>Example: set struct size field</b></summary>

```yaml
# The metadata is missing [StructSizeFieldAttribute] for this struct.
- type: Windows.Win32.UI.WindowsAndMessaging.SOME_STRUCT
  struct-size-field: lStructSize
```

</details>

### Reserved Names

The generator has a list of reserved names that it is impossible or unwise for types and parameter names to shadow. These are listed in [ahk-reserved-names.yml](./metadata/ahk-reserved-names.yml). These are arbitrary strings, but in practice generally fall into one of two categories:
- AutoHotkey reserved words (`is`, `not`, `if`, etc.)
- The names of built-in classes (see [Built-In Classes](https://www.autohotkey.com/docs/v2/ObjList.htm) in the AHK docs)

If a type name conflicts with one of these names, it is prefixed with "Win32" or "Wdk", depending on its namespace - e.g. `String` becomes `Win32String`. If a method parameter conflicts with one of these names, it is prefixed with an underscore.

The generator also adds the names of all loaded types to the reserved parameter name list to prevent method paramters from accidentally shadowing Win32 types. This is common with handles, e.g. parameters like `hwnd` would shadow the generated `HWND` type.

## Maintenance

### Updating the Metadata

Routine updates to the metadata are automated via GitHub actions. The action will automatically regenerate the bindings and submit a pull request into the AHK repository with the changes. In most cases, review is trivial and no real action is required beyond merging the pull request. However, you should retitle the pull request to reflect the actual changes - e.g. "Update Win32 metadata to \<version\>". 

Automated metadata updates pull packages off of NuGet - see below for details.

> [!NOTE]
> The NuGet package versions will sometimes lag behind the versions on GitHub, particularly for the Win32 and WDK metadata. If this is a problem, it's perfectly fine to update the `.winmd` files manually, just be sure to also update the relevant `.version` file to prevent automated update from running unnecessarily.

You can update the metadata manually using the scripts in the [`Scripts`](./Scripts/) directory. These scripts pull the latest `.winmd` files out of the relevant NuGet packages and should be safe to run on your base machine or in a ci/cd pipeline.

| Script | NuGet Package | Notes |
| ------ | ------------- | ----------- |
| [`Get-Win32Metadata`](./Scripts/Get-Win32Metadata.ps1) | [`Microsoft.Windows.SDK.Win32Metadata`](https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/) | The Win32 metadata is updated the most frequently of the three by far, sometimes multiple times a month. | 
| [`Get-WdkMetadata`](./Scripts/Get-WdkMetadata.ps1) | [`Microsoft.Windows.WDK.Win32Metadata`](https://www.nuget.org/packages/Microsoft.Windows.WDK.Win32Metadata/) | The WDK metadata is updated very infrequently and is a community project; Microsoft has little involvement. | 
| [`Get-Win32Docs`](./Scripts/Get-Win32Docs.ps1) | [`Microsoft.Windows.SDK.Win32Docs`](https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Docs/) | The Win32 docs are updated even less frequently than the metadata. Note also that WDK documentation is not included, it should be in a later update