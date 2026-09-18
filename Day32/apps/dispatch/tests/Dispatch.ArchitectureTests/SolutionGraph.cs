using System.Text.RegularExpressions;

namespace Dispatch.ArchitectureTests;

/// <summary>One project in the solution, classified by module and layer.</summary>
public sealed record ProjectNode(string Name, string Module, string Layer, IReadOnlyList<string> References)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reads every <c>.csproj</c> in the solution and builds the reference graph the tests assert on.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately parses the project files rather than using reflection over loaded assemblies.
/// The C# compiler drops a project reference that the code never actually uses, so reflection
/// answers "what does this depend on today" — useful, but it would let somebody <em>declare</em>
/// a forbidden reference and stay green until the first line of code used it. The declaration is
/// the architectural decision, so the declaration is what gets tested.
/// </para>
/// <para>
/// <see cref="CompiledDependencyTests"/> covers the reflection half.
/// </para>
/// </remarks>
public static class SolutionGraph
{
    // Lazy, not `{ get; } = Load()`.
    //
    // Static auto-properties initialise in declaration order, so an earlier `Projects` ran before
    // `Root` was assigned and every test failed with a null path inside a TypeInitializationException
    // -- an error message that names neither the real cause nor the file. Lazy removes the ordering
    // question entirely rather than relying on nobody ever reordering two lines.
    private static readonly Lazy<string> LazyRoot = new(FindRoot);
    private static readonly Lazy<IReadOnlyList<ProjectNode>> LazyProjects = new(Load);

    public static IReadOnlyList<ProjectNode> Projects => LazyProjects.Value;

    public static string Root => LazyRoot.Value;

    private static IReadOnlyList<ProjectNode> Load()
    {
        var nodes = new List<ProjectNode>();

        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var text = File.ReadAllText(path);

            var references = Regex
                .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
                .ToArray();

            nodes.Add(new ProjectNode(name, ModuleOf(name), LayerOf(name), references));
        }

        return nodes;
    }

    /// <summary>
    /// "Dispatch.WorkManagement.Domain" -&gt; "WorkManagement". The host and shared kernel are
    /// their own pseudo-modules, because the rules that apply to them are different.
    /// </summary>
    public static string ModuleOf(string projectName)
    {
        var parts = projectName.Split('.');

        return parts.Length switch
        {
            3 => parts[1],                    // Dispatch.<Module>.<Layer>
            _ => parts[^1]                    // Dispatch.Api / Dispatch.SharedKernel
        };
    }

    public static string LayerOf(string projectName)
    {
        var parts = projectName.Split('.');
        return parts.Length == 3 ? parts[2] : parts[^1];
    }

    public static bool IsModuleProject(string projectName) => projectName.Split('.').Length == 3;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // "Dispatch.sln*", not "Dispatch.sln". The .NET 10 SDK writes the XML-based .slnx format
        // by default, so an exact-name check finds nothing and the whole suite fails with an
        // error about the test runner rather than about the architecture.
        while (directory is not null && directory.GetFiles("Dispatch.sln*").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate Dispatch.sln/.slnx by walking up from the test output directory.");
    }
}
