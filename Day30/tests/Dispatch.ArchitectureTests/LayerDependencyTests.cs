namespace Dispatch.ArchitectureTests;

/// <summary>
/// The architecture, as executable rules.
/// </summary>
/// <remarks>
/// <para>
/// This file is the point of the whole scaffold. "Clean architecture" and "modular monolith" are
/// claims about which things are allowed to know about which other things, and a claim that is
/// only written in a README is a claim that has already started decaying. Nobody adds a
/// forbidden reference on purpose — they add it at 5pm because the type they needed happened to
/// be over there, and by the time anyone notices there are forty of them and the boundary is
/// gone.
/// </para>
/// <para>
/// Every test below fails the build instead.
/// </para>
/// </remarks>
public class LayerDependencyTests
{
    // ==========================================================================================
    // Rule 1 — the dependency direction. Everything points inwards, towards Domain.
    // ==========================================================================================

    [Fact]
    public void Domain_depends_on_nothing_but_the_shared_kernel()
    {
        foreach (var project in SolutionGraph.Projects.Where(p => p.Layer == "Domain"))
        {
            var forbidden = project.References
                .Where(r => r != "Dispatch.SharedKernel")
                .ToArray();

            // The innermost layer. It holds the rules of the business, and those rules predate
            // every technical decision around them -- the database, the web framework, the
            // message broker. A Domain that references any of those cannot be reasoned about, or
            // tested, without them.
            Assert.True(
                forbidden.Length == 0,
                $"{project.Name} must reference only Dispatch.SharedKernel, but also references: "
                + string.Join(", ", forbidden));
        }
    }

    [Fact]
    public void Application_never_references_infrastructure()
    {
        foreach (var project in SolutionGraph.Projects.Where(p => p.Layer == "Application"))
        {
            var forbidden = project.References.Where(r => r.EndsWith(".Infrastructure", StringComparison.Ordinal));

            // The inversion that makes the whole thing work. Application DECLARES what it needs
            // (IWorkOrderRepository); Infrastructure PROVIDES it. Reverse this one arrow and the
            // layers still have their names but none of their value: swapping a database would
            // mean editing use cases.
            Assert.True(
                !forbidden.Any(),
                $"{project.Name} references infrastructure: {string.Join(", ", forbidden)}. "
                + "Ports are declared in Application and implemented in Infrastructure, never the reverse.");
        }
    }

    [Fact]
    public void Contracts_depend_on_nothing_but_the_shared_kernel()
    {
        foreach (var project in SolutionGraph.Projects.Where(p => p.Layer == "Contracts"))
        {
            var forbidden = project.References.Where(r => r != "Dispatch.SharedKernel").ToArray();

            // Contracts is what every other module references, so anything it drags in becomes a
            // transitive dependency of the entire solution. Keeping it to primitives and the
            // shared kernel is what stops one module's package choices becoming everyone's.
            Assert.True(
                forbidden.Length == 0,
                $"{project.Name} must stay dependency-free, but references: {string.Join(", ", forbidden)}");
        }
    }

    // ==========================================================================================
    // Rule 2 — the module boundary. Contracts is the only door.
    // ==========================================================================================

    [Fact]
    public void No_module_reaches_into_another_modules_internals()
    {
        var violations = new List<string>();

        foreach (var project in SolutionGraph.Projects.Where(p => SolutionGraph.IsModuleProject(p.Name)))
        {
            foreach (var reference in project.References.Where(SolutionGraph.IsModuleProject))
            {
                var otherModule = SolutionGraph.ModuleOf(reference);
                var otherLayer = SolutionGraph.LayerOf(reference);

                if (otherModule == project.Module)
                {
                    continue;   // a module may reference itself freely
                }

                if (otherLayer != "Contracts")
                {
                    violations.Add($"{project.Name} -> {reference}");
                }
            }
        }

        // THE rule of a modular monolith. Everything else is arrangement; this is the boundary.
        //
        // A module that can reach another module's Domain has no boundary at all -- it can depend
        // on internal types, so the other module can no longer change them, so the two are one
        // module wearing two folder names. Restricting the edge to Contracts is what keeps a
        // module's internals genuinely internal, and what makes extracting it later a build
        // change rather than an excavation.
        Assert.True(
            violations.Count == 0,
            "Cross-module references must target *.Contracts only. Found:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Only_the_host_composes_infrastructure()
    {
        var violations = SolutionGraph.Projects
            .Where(p => p.Name != "Dispatch.Api")
            .SelectMany(p => p.References
                .Where(r => r.EndsWith(".Infrastructure", StringComparison.Ordinal)
                            && SolutionGraph.ModuleOf(r) != p.Module)
                .Select(r => $"{p.Name} -> {r}"))
            .ToArray();

        // Infrastructure is where the concrete choices live -- the store, the transport, the
        // hosted services. Exactly one project is allowed to see all of them, and that project is
        // the composition root. Anywhere else, a reference to Infrastructure is a module reaching
        // for another module's database.
        Assert.True(
            violations.Length == 0,
            "Only Dispatch.Api may reference another module's Infrastructure. Found:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void The_shared_kernel_depends_on_nothing()
    {
        var sharedKernel = SolutionGraph.Projects.Single(p => p.Name == "Dispatch.SharedKernel");

        // Everything references it, so anything it references is referenced by everything. A
        // shared kernel that grows dependencies is how a package choice made once in one module
        // becomes mandatory in all of them.
        Assert.True(
            sharedKernel.References.Count == 0,
            "Dispatch.SharedKernel must have no project references, but has: "
            + string.Join(", ", sharedKernel.References));
    }

    // ==========================================================================================
    // Rule 3 — the shape of the solution itself.
    // ==========================================================================================

    [Fact]
    public void Every_module_has_the_same_four_layers()
    {
        var expected = new[] { "Contracts", "Domain", "Application", "Infrastructure" }.Order().ToArray();

        var modules = SolutionGraph.Projects
            .Where(p => SolutionGraph.IsModuleProject(p.Name))
            .GroupBy(p => p.Module);

        foreach (var module in modules)
        {
            // Uniformity is not tidiness for its own sake. A newcomer who learns where things
            // live in one module has learned it for all of them, and a module missing a layer is
            // usually a module that put something in the wrong one.
            Assert.Equal(expected, module.Select(p => p.Layer).Order().ToArray());
        }
    }

    [Fact]
    public void The_documented_cross_module_edges_are_the_only_ones()
    {
        // The complete inter-module dependency graph of the system, written down. If this test
        // fails, either somebody added a coupling that was never discussed -- or the design moved
        // and DESIGN.md has not caught up. Both are worth stopping for.
        var allowed = new HashSet<string>
        {
            "Dispatch.Scheduling.Application -> Dispatch.WorkManagement.Contracts",
            "Dispatch.Billing.Application -> Dispatch.WorkManagement.Contracts",
            "Dispatch.WorkManagement.Application -> Dispatch.Scheduling.Contracts"
        };

        var actual = SolutionGraph.Projects
            .Where(p => SolutionGraph.IsModuleProject(p.Name))
            .SelectMany(p => p.References
                .Where(r => SolutionGraph.IsModuleProject(r) && SolutionGraph.ModuleOf(r) != p.Module)
                .Select(r => $"{p.Name} -> {r}"))
            .ToHashSet();

        Assert.Equal(allowed.Order(), actual.Order());
    }
}
