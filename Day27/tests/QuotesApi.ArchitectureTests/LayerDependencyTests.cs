namespace QuotesApi.ArchitectureTests;

/// <summary>
/// The architecture, as executable rules.
/// </summary>
/// <remarks>
/// <para>
/// This file is the point of the whole conversion. "Modular monolith" is a claim about which
/// things are allowed to know about which other things, and a claim that lives only in a README
/// has already started decaying. Nobody adds a forbidden reference on purpose — they add it at
/// 5pm because the type they needed happened to be over there, and by the time anyone notices
/// there are forty of them and the boundary is gone.
/// </para>
/// <para>
/// Before this conversion the entire API was one project, so none of these rules could even be
/// expressed. Every test below now fails the build instead.
/// </para>
/// </remarks>
public class LayerDependencyTests
{
    private static readonly string[] Modules =
        ["Quotes", "Identity", "Jobs", "Messaging", "Resilience"];

    /// <summary>
    /// The single recorded exception to module isolation.
    /// </summary>
    /// <remarks>
    /// One shared <c>AppDbContext</c> was a deliberate choice, which means every module's
    /// Infrastructure transitively sees every module's entities. Naming the exception here is
    /// what keeps it an exception: it is one edge, asserted in one place, and any second
    /// compromise fails a test rather than joining it quietly.
    /// </remarks>
    private const string SharedPersistence = "QuotesApi.Persistence";

    [Fact]
    public void Every_project_is_classifiable()
    {
        // A project that does not fit the naming scheme silently escapes every rule below,
        // which is a worse failure than breaking one.
        var known = new[] { "Host", "SharedKernel", "Persistence" };

        foreach (var p in SolutionGraph.Projects)
        {
            var ok = SolutionGraph.IsModuleProject(p.Name)
                ? Modules.Contains(p.Module) &&
                  new[] { "Contracts", "Domain", "Application", "Infrastructure" }.Contains(p.Layer)
                : known.Contains(p.Layer);

            Assert.True(ok, $"{p.Name} does not fit the naming scheme, so no boundary rule applies to it.");
        }
    }

    [Fact]
    public void A_module_may_only_reach_another_module_through_its_Contracts()
    {
        // The rule the whole structure exists for. Reaching a Domain, Application or
        // Infrastructure project of another module means depending on its internals, and
        // internals are exactly what a module is allowed to change without telling anyone.
        var violations = new List<string>();

        foreach (var p in SolutionGraph.Projects.Where(x => SolutionGraph.IsModuleProject(x.Name)))
        {
            foreach (var r in p.References)
            {
                if (!SolutionGraph.IsModuleProject(r)) continue;

                var otherModule = SolutionGraph.ModuleOf(r);
                if (otherModule == p.Module) continue;               // inside its own module

                if (SolutionGraph.LayerOf(r) != "Contracts")
                {
                    violations.Add($"{p.Name} -> {r}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Cross-module references must land on a *.Contracts project:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Dependencies_point_inwards()
    {
        // Infrastructure -> Application -> Domain -> SharedKernel. Invert one arrow and the
        // layers keep their names and lose their value: a Domain that references Infrastructure
        // cannot be tested without the infrastructure it was supposed to be independent of.
        var rank = new Dictionary<string, int>
        {
            ["Contracts"] = 0, ["Domain"] = 1, ["Application"] = 2, ["Infrastructure"] = 3
        };

        var violations = new List<string>();

        foreach (var p in SolutionGraph.Projects.Where(x => SolutionGraph.IsModuleProject(x.Name)))
        {
            foreach (var r in p.References.Where(SolutionGraph.IsModuleProject))
            {
                if (SolutionGraph.ModuleOf(r) != p.Module) continue;  // cross-module: other test

                // Contracts referencing its own Domain is allowed, and is a real decision rather
                // than an oversight: Jobs publishes `IJobHandler`, whose signature names `Job`.
                // The alternative is a duplicate primitive-only job descriptor that every handler
                // has to map to and from, which buys nothing inside a single deployable.
                if (p.Layer == "Contracts" && SolutionGraph.LayerOf(r) == "Domain") continue;

                if (rank[SolutionGraph.LayerOf(r)] > rank[p.Layer])
                {
                    violations.Add($"{p.Name} -> {r}  (outward)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Layer references must point inwards:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void No_module_references_the_host()
    {
        // The host composes modules. A module that reaches back into the host makes the
        // composition circular and means the module can no longer be understood, tested or
        // reused without the application that happens to host it today.
        var offenders = SolutionGraph.Projects
            .Where(p => p.Name != "QuotesApi.Host" && p.References.Contains("QuotesApi.Host"))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These projects reference the host: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_the_host_references_an_Infrastructure_project()
    {
        // Infrastructure is a module's private half. If another module — or the shared kernel —
        // can name a concrete adapter, the port it sits behind has stopped being a seam.
        var offenders = new List<string>();

        foreach (var p in SolutionGraph.Projects.Where(x => x.Name != "QuotesApi.Host"))
        {
            foreach (var r in p.References)
            {
                if (SolutionGraph.LayerOf(r) == "Infrastructure" && SolutionGraph.ModuleOf(r) != p.Module)
                {
                    offenders.Add($"{p.Name} -> {r}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Only the host may reference another module's Infrastructure:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_shared_kernel_depends_on_nothing_in_this_solution()
    {
        // A shared kernel that depends on a module is not shared; it is that module with extra
        // steps, and it drags whatever it references into every project that touches it.
        var sharedKernel = SolutionGraph.Projects.Single(p => p.Name == "QuotesApi.SharedKernel");

        Assert.True(sharedKernel.References.Count == 0,
            "QuotesApi.SharedKernel must reference nothing, but references: "
            + string.Join(", ", sharedKernel.References));
    }

    [Fact]
    public void The_shared_database_is_the_only_compromise_and_it_is_bounded()
    {
        // Keeping one AppDbContext was chosen deliberately. This test is what stops that choice
        // from quietly becoming "modules may reference whatever they like".
        //
        // Two halves:
        //   1. only *.Infrastructure projects may reference Persistence
        //   2. Persistence itself may only reference Domain projects and the shared kernel
        //
        // If a second shared project ever appears, part 2 fails and the decision has to be made
        // again in the open.
        var wrongConsumers = SolutionGraph.Projects
            .Where(p => p.References.Contains(SharedPersistence))
            .Where(p => p.Name != "QuotesApi.Host" && SolutionGraph.LayerOf(p.Name) != "Infrastructure")
            .Select(p => p.Name)
            .ToArray();

        Assert.True(wrongConsumers.Length == 0,
            "Only Infrastructure projects and the host may reference the shared database: "
            + string.Join(", ", wrongConsumers));

        var persistence = SolutionGraph.Projects.Single(p => p.Name == SharedPersistence);
        var illegal = persistence.References
            .Where(r => r != "QuotesApi.SharedKernel" && SolutionGraph.LayerOf(r) != "Domain")
            .ToArray();

        Assert.True(illegal.Length == 0,
            "The persistence project may only reference Domain projects and the shared kernel, "
            + "but also references: " + string.Join(", ", illegal));
    }

    [Fact]
    public void Every_module_has_all_four_layers()
    {
        // Uniform shape is what makes the rules above checkable rather than case-by-case. A
        // module that quietly drops its Application layer has moved its use cases into
        // Infrastructure, next to the database code, which is where they stop being testable.
        foreach (var module in Modules)
        {
            var layers = SolutionGraph.Projects
                .Where(p => SolutionGraph.IsModuleProject(p.Name) && p.Module == module)
                .Select(p => p.Layer)
                .ToArray();

            foreach (var expected in new[] { "Contracts", "Domain", "Application", "Infrastructure" })
            {
                Assert.True(layers.Contains(expected),
                    $"Module {module} is missing its {expected} project.");
            }
        }
    }
}
