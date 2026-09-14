using System.Text.RegularExpressions;

namespace QuotesApi.ArchitectureTests;

/// <summary>One project in the solution, classified by module and layer.</summary>
public sealed record ProjectNode(string Name, string Module, string Layer, IReadOnlyList<string> References)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reads every <c>.csproj</c> under <c>backend/src</c> and builds the reference graph the tests
/// assert on.
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
    // Lazy rather than `{ get; } = Load()`. Static auto-properties initialise in declaration
    // order, so a reordering would run Load() before Root was assigned and every test would fail
    // inside a TypeInitializationException — an error that names neither the cause nor the file.
    private static readonly Lazy<string> LazyRoot = new(FindRoot);
    private static readonly Lazy<IReadOnlyList<ProjectNode>> LazyProjects = new(Load);

    public static IReadOnlyList<ProjectNode> Projects => LazyProjects.Value;

    public static string Root => LazyRoot.Value;

    private static IReadOnlyList<ProjectNode> Load()
    {
        var nodes = new List<ProjectNode>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var text = File.ReadAllText(path);

            var references = Regex
                .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
                .ToArray();

            nodes.Add(new ProjectNode(name, ModuleOf(name), LayerOf(name), references));
        }

        if (nodes.Count == 0)
        {
            throw new InvalidOperationException($"No projects found under {Path.Combine(Root, "src")}.");
        }

        return nodes;
    }

    /// <summary>
    /// "QuotesApi.Quotes.Domain" -&gt; "Quotes". The host, the shared kernel and the persistence
    /// project are their own pseudo-modules, because the rules that apply to them are different.
    /// </summary>
    public static string ModuleOf(string projectName)
    {
        var parts = projectName.Split('.');

        return parts.Length switch
        {
            3 => parts[1],                    // QuotesApi.<Module>.<Layer>
            _ => parts[^1]                    // QuotesApi.Host / .SharedKernel / .Persistence
        };
    }

    public static string LayerOf(string projectName)
    {
        var parts = projectName.Split('.');
        return parts.Length == 3 ? parts[2] : parts[^1];
    }

    /// <summary>True for the four-layer module projects, false for Host/SharedKernel/Persistence.</summary>
    public static bool IsModuleProject(string projectName) => projectName.Split('.').Length == 3;

    private static string FindRoot()
    {
        // The solution lives in Day27/backend, but this test project lives in Day27/tests — so
        // walking straight up from the test output directory passes Day27 and never sees it.
        // Each ancestor is therefore checked both for the solution itself and for a `backend`
        // child holding it.
        //
        // "QuotesApi.sln*", not "QuotesApi.sln". The .NET 10 SDK writes the XML-based .slnx
        // format by default, so an exact-name check finds nothing and the whole suite fails with
        // an error about the test runner rather than about the architecture.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (directory.GetFiles("QuotesApi.sln*").Length > 0)
            {
                return directory.FullName;
            }

            var backend = new DirectoryInfo(Path.Combine(directory.FullName, "backend"));
            if (backend.Exists && backend.GetFiles("QuotesApi.sln*").Length > 0)
            {
                return backend.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not locate QuotesApi.sln/.slnx by walking up from the test output directory.");
    }
}
