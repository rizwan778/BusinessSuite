using System.Reflection;
using BusinessSuite.Application;
using BusinessSuite.Domain.Common;
using BusinessSuite.Infrastructure;

namespace BusinessSuite.Tests.Unit.Architecture;

/// <summary>
/// Guards the clean-architecture dependency rules (docs/ARCHITECTURE.md).
/// If one of these tests fails, a project started depending on a layer it must not know about.
/// Fix the design (move the code or introduce an interface); do not weaken the test.
/// </summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly DomainAssembly = typeof(Entity).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ApplicationServiceCollectionExtensions).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(InfrastructureServiceCollectionExtensions).Assembly;

    [Fact]
    public void Domain_depends_on_no_other_layer_and_no_framework()
    {
        AssertNoReferences(DomainAssembly,
            "BusinessSuite.Application",
            "BusinessSuite.Infrastructure",
            "BusinessSuite.Web",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_web_or_sql_server()
    {
        AssertNoReferences(ApplicationAssembly,
            "BusinessSuite.Infrastructure",
            "BusinessSuite.Web",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.Data.SqlClient",
            "Microsoft.AspNetCore");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_web()
    {
        AssertNoReferences(InfrastructureAssembly, "BusinessSuite.Web");
    }

    private static void AssertNoReferences(Assembly assembly, params string[] forbiddenPrefixes)
    {
        var violations = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assembly.GetName().Name} must not reference: {string.Join(", ", violations)}");
    }
}
