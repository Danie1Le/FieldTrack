using System.Reflection;

namespace FieldTrack.UnitTests;

public sealed class DomainDependencyTests
{
    [Fact]
    public void Domain_references_only_the_base_class_library()
    {
        // Spec #21, Modules: Domain holds entities and the rules engine with no infrastructure dependencies.
        var references = Assembly.Load("FieldTrack.Domain").GetReferencedAssemblies().Select(reference => reference.Name);

        references.ShouldAllBe(name => name == "netstandard" || name!.StartsWith("System", StringComparison.Ordinal));
    }
}
