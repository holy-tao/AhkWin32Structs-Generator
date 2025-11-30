
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;
using System.Reflection;

/// <summary>
/// Type for the special "Apis" type that contains functions and constants
/// </summary>
class AhkApiType : AhkType
{
    List<AhkConstant> constants = [];
    List<AhkMethod> methods = [];

    public AhkApiType(MetadataReader mr, TypeDefinition typeDef) : base(mr, typeDef)
    {
        ApiDetails? apiDetails = DocumentationUtils.GetApiDetails(mr, typeDef);

        constants = typeDef.GetFields()
            .Select(mr.GetFieldDefinition)
            .Where(fd => !mr.StringComparer.Equals(fd.Name, "value__"))
            .Select(fd => new AhkConstant(mr, fd, apiDetails))
            .ToList();


        methods = typeDef.GetMethods()
            .Select(handle =>
            {
                MethodDefinition methodDefinition = mr.GetMethodDefinition(handle);

                try
                {
                    AhkMethod method = new(mr, methodDefinition);
                    return method;
                }
                catch (Exception ex)
                {
                    string methodName = mr.GetString(methodDefinition.Name);

                    Console.Error.WriteLine($"{ex.GetType().Name} parsing {Namespace}.{Name}::{methodName}: {ex.Message}");
                    Console.Error.WriteLine(ex.Message);
                    Console.Error.WriteLine(ex.StackTrace);
                    Console.Error.WriteLine();

                    return null;
                }
            })
            .OfType<AhkMethod>()
            .DistinctBy(method => method.Name)
            .ToList();
    }

    public override void ToAhk(StringBuilder sb)
    {
        HeadersToAhk(sb);

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {GetName()} {{");
        sb.AppendLine();

        AppendConstants(sb);
        sb.AppendLine();
        AppendMethods(sb);

        sb.AppendLine("}");
    }

    private void HeadersToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine($"#Include {GetPathToBase()}Win32Handle.ahk");
        if(constants.Any(c => c.NeedsGuid()))
            sb.AppendLine($"#Include {GetPathToBase()}Guid.ahk");

        AppendImports(sb);
        sb.AppendLine();
    }

    public override List<string> GetReferencedTypes()
    {
        var imports = base.GetReferencedTypes();
        methods.ForEach(m => imports.AddRange(m.GetReferencedTypes()));
        constants.ForEach(c => imports.AddRange(c.GetReferencedTypes()));

        return [.. imports.Distinct()];
    }

    private void AppendConstants(StringBuilder sb)
    {
        sb.AppendLine(";@region Constants");

        foreach (AhkConstant constant in constants)
        {
            sb.AppendLine();
            constant.ToAhk(sb);
        }

        sb.AppendLine(";@endregion Constants");
    }

    private void AppendMethods(StringBuilder sb)
    {
        sb.AppendLine(";@region Methods");

        foreach (AhkMethod method in methods)
        {
            method.ToAhk(sb);
            sb.AppendLine();            
        }

        sb.AppendLine(";@endregion Methods");
    }

    private string GetName()
    {
        // We don't want the name to just be "Apis"
        return Namespace.Split(".").Last();
    }
}