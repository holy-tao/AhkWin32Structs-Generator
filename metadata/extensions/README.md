# Extension Definitions

Extensions are custom code added to generated types. Extensions can be added to Structs and Enums.

> [!Important]
> You must add tests for extension methods in the bindings project in addition to the extension definition files here.

## Extension Definition Files
Extensions are defined in [YAML](https://yaml.org/) files. The generator will read files in this directory (or the /extensions subdirectory of whichever directory is passed in as its metadata directory) with the extensions `.yml` and `.yaml`. The file format is as follows (example is a snippet of the [RECT / RECTL](./RECT.yml) extension definition):
```yaml
# Fully qualified names of types to which the extensions should be added.
add-to:
  - Windows.Win32.Foundation.RECT
  - Windows.Win32.Foundation.RECTL

# Imports the extension code needs.
#   - Value is `null` / empty list  -> whole-file include of that FQN.
#   - Value is a list of names      -> import those specific functions from the
#                                      named Apis FQN. In v2.1 this becomes
#                                      `#Import "..." { Name1, Name2 }` merged
#                                      with any other imports the type already
#                                      needed; in v2.0 it falls back to a
#                                      whole-file include of the Apis file.
imports:
  Windows.Win32.Foundation.POINT:
  Windows.Win32.Graphics.Gdi.Apis:
    - IsRectEmpty
    - IntersectRect

# Code to add to the generated type's body. Must supply both `v20` and `v21`;
# either may be the literal value `skip` to opt out for that target. When the
# two bodies are identical, use a YAML anchor (`&shared` / `*shared`).
#
# v2.0 reaches free functions through their Apis class (`Gdi.IsRectEmpty(...)`).
# v2.1 imports them by name and calls them bare (`IsRectEmpty(...)`).
code:
  v20: |
    isEmpty => Gdi.IsRectEmpty(this)
    ...
  v21: |
    isEmpty => IsRectEmpty(this)
    ...
```

See [CONTRIBUTING.md](../../CONTRIBUTING.md#writing-extensions) for the full schema, more examples (shared body via anchor, opting out of a version), and the rationale behind the per-version split.

### Aliases
The generator suports the following aliases, using the `$Name` convention (like bash or PowerShell - this is because `%%` is valid AHK syntax and would make parsing a nightmare). All aliases are case-sensitive:
- `$Class`: the name of the class to which the extensions are being added