using System.Reflection;
using System.Runtime.Loader;
using Minus.Core;

namespace Minus;

public static class ToolboxLoader
{
    public static IReadOnlyList<ITool> Load(string requested)
    {
        var path = Resolve(requested)
            ?? throw new FileNotFoundException($"toolbox not found: {requested}");

        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

        var tools = new List<ITool>();
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(ITool).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;

            tools.Add((ITool)Activator.CreateInstance(type)!);
        }

        if (tools.Count == 0)
            throw new InvalidOperationException(
                $"toolbox '{Path.GetFileName(path)}' contains no public ITool implementations with a parameterless constructor.");

        return tools;
    }

    // Resolution order:
    //   1. as-given (absolute or relative to CWD)
    //   2. <BaseDirectory>/toolboxes/<name>
    //   3. each of the above with ".dll" appended
    private static string? Resolve(string requested)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            requested,
            Path.Combine(baseDir, "toolboxes", requested),
            requested + ".dll",
            Path.Combine(baseDir, "toolboxes", requested + ".dll"),
        };

        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
