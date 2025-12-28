# PiidPrecompute

This is a simple tool to precompute WinRT [**P**arameterized **I**nstance **ID**s](https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system) (PIIDs, not to be confused with **I**nstance **ID**entifiers) using the Windows Runtime itself. This allows the generator to treat PIIDs as "compile-time" constants and to avoid scanning the metadata (or some compressed version of it) at runtime. 

The trade-off is obviously flexibility. It's not possible for users of the bindings to create arbitrary generic instantiations - that information simply does not exist in the final AutoHotkey code. But this avoids recreating the WinRT type system in miniature in AutoHotkey, a language onto which it does not map well (Namespaces? _Delegates_? Not fun).

## Usage

The compiled executable is a command line tool similar to the generator, and can be called the same way.

```cmd
.\PiidPrecompute <MetadataDir> <OutputPath>
```

The output is a YAML file; if one already exists in the output directory, it is overwritten. This file is meant to be parsed as a `Dictionary<string, string>`. Sample outputs map the fully qualified type names, including backtick [*arity*](https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#class-based-projection), to piid strings:

```yaml
Windows.Foundation.Collections.IVector`1<Windows.Data.Json.IJsonValue>: d44662bc-dce3-59a8-9272-4b210f33908b
Windows.Foundation.Collections.IIterable`1<Windows.Data.Json.IJsonValue>: 57e85ad6-f566-5d78-ab75-92378d4f067c
Windows.Foundation.Collections.IMap`2<Windows.Win32.System.WinRT.HSTRING,Windows.Data.Json.IJsonValue>: c9d9a725-786b-5113-b4b7-9b61764c220b
Windows.Foundation.Collections.IIterable`1<Windows.Foundation.Collections.IKeyValuePair`2<Windows.Win32.System.WinRT.HSTRING,Windows.Data.Json.IJsonValue>>: 2b47863d-54c0-5740-b3c6-6829951b2aa3
```

### Usage Notes

The tool scans the `Windows.winmd` file in a metadata directory for all referenced closed generic instantiations and emits a file mapping them to their Guids by calling [`RoGetParameterizedTypeInstanceIID`](https://learn.microsoft.com/en-us/windows/win32/api/roparameterizediid/nf-roparameterizediid-rogetparameterizedtypeinstanceiid). That file can then be consumed by the generator as a lookup table.

> [!Important]
> This lookup needs to be regenerated every time the the Windows Runtime or Windows SDK is updated. Probably it should be run all the time as part of the binding generation pipeline.

Note that *breaking* changes are not a concern here, we just need to ensure that *new* APIs are covered - per Microsoft's WinRT [versioning documentation](https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#versioning):
> Windows system structs, delegates, and interfaces are immutable once defined. They may never be modified in any subsequent Windows release.
>
> Windows system enums and runtime classes are additively versionable. Enums may add new enum values in subsequent Windows releases. Classes may add new implemented interfaces (including static, activation factory, composition factory, overridable, and protected interfaces) in subsequent Windows releases.
