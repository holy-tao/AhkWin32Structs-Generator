# Extension Definitions

Extensions are custom code added to generated types. Extensions can be added to Structs and Enums.

> [!Important]
> You must add tests for extension methods in the bindings project in addition to the extension definition files here.

## Extension Definition Files
Extensions are defined in [YAML](https://yaml.org/) files. The generator will read files in this directory (or the /extensions subdirectory of whichever directory is passed in as its metadata directory) with the extensions `.yml` and `.yaml`. The file format is as follows (example is a snippet of the [RECT / RECTL](./RECT.yml) extension definition):
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

### Aliases
The generator suports the following aliases, using the `$Name` convention (like bash or PowerShell - this is because `%%` is valid AHK syntax and would make parsing a nightmare). All aliases are case-sensitive:
- `$Class`: the name of the class to which the extensions are being added