using System.Reflection;
using Dispatch.SharedKernel;

namespace Dispatch.ArchitectureTests;

/// <summary>
/// The same boundaries, checked against what the compiler actually produced.
/// </summary>
/// <remarks>
/// <see cref="LayerDependencyTests"/> reads the project files, which is where the architectural
/// <em>intent</em> is declared. This reads the emitted assemblies, which is what the code
/// <em>does</em>. They catch different mistakes: a forbidden reference added to a csproj shows up
/// there before anyone uses it, and a NuGet package that drags a framework into the domain
/// transitively only shows up here.
/// </remarks>
public class CompiledDependencyTests
{
    private static readonly Assembly[] DomainAssemblies =
    [
        typeof(WorkManagement.Domain.WorkOrders.WorkOrder).Assembly,
        typeof(Scheduling.Domain.Reservations.Reservation).Assembly,
        typeof(Billing.Domain.Invoices.Invoice).Assembly
    ];

    [Fact]
    public void No_domain_assembly_knows_about_a_database_a_web_framework_or_a_broker()
    {
        // Names, not types, because the point is to catch a dependency that arrived transitively
        // through a package nobody meant to add.
        string[] banned =
        [
            "EntityFrameworkCore", "Dapper", "MongoDB", "StackExchange.Redis",
            "Microsoft.AspNetCore", "Azure.Messaging", "MediatR", "Newtonsoft",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting"
        ];

        foreach (var assembly in DomainAssemblies)
        {
            var offenders = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(name => banned.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            // A domain model that cannot be instantiated without a DI container, or tested
            // without a database, has stopped being a model of the business and become a model of
            // the infrastructure. Every test in Dispatch.WorkManagement.Domain.Tests runs with a
            // `new` and a fake clock, and this is the test that keeps it that way.
            Assert.True(
                offenders.Length == 0,
                $"{assembly.GetName().Name} has picked up infrastructure dependencies: "
                + string.Join(", ", offenders));
        }
    }

    [Fact]
    public void Contracts_expose_primitives_only()
    {
        var contractAssemblies = new[]
        {
            typeof(WorkManagement.Contracts.WorkOrderScheduledV1).Assembly,
            typeof(Scheduling.Contracts.TechnicianReservedV1).Assembly,
            typeof(Billing.Contracts.InvoiceDraftedV1).Assembly
        };

        var violations = new List<string>();

        foreach (var assembly in contractAssemblies)
        {
            var events = assembly.GetTypes().Where(t => typeof(IIntegrationEvent).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var type in events)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsPortable(property.PropertyType))
                    {
                        violations.Add($"{type.Name}.{property.Name} is {property.PropertyType.Name}");
                    }
                }
            }
        }

        // A published event carrying a domain type drags the internal model across the boundary:
        // every subscriber now compiles against it, so the owning module can no longer change it.
        // Primitives keep the contract a contract -- and keep it serialisable the day the
        // transport stops being a method call.
        Assert.True(
            violations.Count == 0,
            "Integration events must carry primitives only. Found:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Domain_events_never_leak_into_a_published_contract()
    {
        var contractAssemblies = new[]
        {
            typeof(WorkManagement.Contracts.WorkOrderScheduledV1).Assembly,
            typeof(Scheduling.Contracts.TechnicianReservedV1).Assembly,
            typeof(Billing.Contracts.InvoiceDraftedV1).Assembly
        };

        foreach (var assembly in contractAssemblies)
        {
            var leaked = assembly.GetTypes()
                .Where(t => typeof(IDomainEvent).IsAssignableFrom(t))
                .Select(t => t.Name)
                .ToArray();

            // Domain events are how a module talks to itself and are free to change with it.
            // Integration events are how it talks to everyone else and are not. Publishing a
            // domain event directly collapses that distinction and quietly makes the internal
            // model public.
            Assert.True(
                leaked.Length == 0,
                $"{assembly.GetName().Name} exposes domain events: {string.Join(", ", leaked)}");
        }
    }

    [Fact]
    public void Aggregate_roots_have_no_public_setters()
    {
        var violations = new List<string>();

        foreach (var assembly in DomainAssemblies)
        {
            var aggregates = assembly.GetTypes().Where(IsAggregateRoot);

            foreach (var type in aggregates)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.SetMethod is { IsPublic: true })
                    {
                        violations.Add($"{type.Name}.{property.Name}");
                    }
                }
            }
        }

        // The single mechanical rule that keeps invariants enforceable. One public setter and the
        // aggregate can be put into a state no method would have allowed -- at which point every
        // rule it enforces becomes a suggestion, and "is this object valid?" has to be re-asked
        // at every read instead of guaranteed at every write.
        Assert.True(
            violations.Count == 0,
            "Aggregate state changes only through named methods. Public setters found on:\n  "
            + string.Join("\n  ", violations));
    }

    private static bool IsAggregateRoot(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Types that survive leaving the process: primitives, and the handful of BCL types every serialiser knows.</summary>
    private static bool IsPortable(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateTime)
            || underlying == typeof(TimeSpan);
    }
}
