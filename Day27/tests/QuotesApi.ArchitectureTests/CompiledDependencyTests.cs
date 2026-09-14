using System.Reflection;

namespace QuotesApi.ArchitectureTests;

/// <summary>
/// The same boundaries, checked against what the compiler actually produced.
/// </summary>
/// <remarks>
/// <see cref="LayerDependencyTests"/> reads the project files, which is where the architectural
/// <em>intent</em> is declared. This reads the emitted assemblies, which is what the code
/// <em>does</em>. They catch different mistakes: a forbidden reference added to a csproj shows up
/// there before anyone uses it, and a NuGet package that drags a framework into a domain project
/// transitively only shows up here.
/// </remarks>
public class CompiledDependencyTests
{
    private static readonly Assembly[] DomainAssemblies =
    [
        typeof(Quotes.Domain.Quote).Assembly,
        typeof(Identity.Domain.User).Assembly,
        typeof(Jobs.Domain.Job).Assembly,
        typeof(Messaging.Domain.OutboxMessage).Assembly
    ];

    [Fact]
    public void No_domain_assembly_knows_about_a_database_a_web_framework_or_a_broker()
    {
        // Names, not types, because the point is to catch a dependency that arrived transitively
        // through a package nobody meant to add.
        string[] banned =
        [
            "EntityFrameworkCore", "Dapper", "MongoDB", "StackExchange.Redis",
            "Microsoft.AspNetCore", "Azure.Messaging", "Polly", "Newtonsoft",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting"
        ];

        foreach (var assembly in DomainAssemblies)
        {
            var offenders = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(name => banned.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            // A domain model that cannot be instantiated without a DI container, or tested
            // without a database, has stopped being a model of the business and become a model
            // of the infrastructure. Before the split, Quote lived in the same assembly as the
            // DbContext, the Service Bus client and every ASP.NET type — so this was not a rule
            // anyone could have stated, let alone enforced.
            Assert.True(
                offenders.Length == 0,
                $"{assembly.GetName().Name} has picked up infrastructure dependencies: "
                + string.Join(", ", offenders));
        }
    }

    [Fact]
    public void The_shared_kernel_carries_no_infrastructure()
    {
        // The shared kernel is referenced by every project in the solution, so anything it drags
        // in becomes a dependency of the whole codebase. TextGuard and Telemetry live here
        // precisely because they are allocation-free, framework-free primitives.
        string[] banned =
        [
            "EntityFrameworkCore", "Microsoft.AspNetCore", "Azure.", "Polly",
            "Microsoft.Extensions.Hosting"
        ];

        var offenders = typeof(SharedKernel.IClock).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => banned.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "QuotesApi.SharedKernel has picked up infrastructure dependencies: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void A_domain_type_stays_inside_its_own_module()
    {
        // The cheapest possible check that the split is real rather than cosmetic: four types
        // that used to share one assembly must now be in four.
        var assemblies = DomainAssemblies.Select(a => a.GetName().Name).ToArray();

        Assert.Equal(assemblies.Length, assemblies.Distinct().Count());
    }

    [Fact]
    public void Contracts_carry_no_reference_to_another_modules_internals()
    {
        // QuoteEvent is published by the Messaging module and consumed by subscribers. If the
        // Contracts assembly referenced Quotes.Domain, every consumer would be compiled against
        // the internal model and the published shape could not change without breaking them.
        var contracts = typeof(Quotes.Contracts.QuoteEvent).Assembly;

        var offenders = contracts.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("QuotesApi.", StringComparison.Ordinal))
            .Where(n => n != "QuotesApi.SharedKernel")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "QuotesApi.Quotes.Contracts must depend on nothing but the shared kernel, "
            + "but references: " + string.Join(", ", offenders));
    }
}
