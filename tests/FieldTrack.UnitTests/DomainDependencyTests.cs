using System.Reflection;
using System.Xml.Linq;

namespace FieldTrack.UnitTests;

public sealed class DomainDependencyTests
{
    // Spec #21, Modules: Domain holds entities and the rules engine with no infrastructure dependencies.

    [Fact]
    public void Domain_references_only_the_base_class_library()
    {
        var references = Assembly.Load("FieldTrack.Domain").GetReferencedAssemblies().Select(reference => reference.Name);

        references.ShouldAllBe(name => name == "netstandard" || name!.StartsWith("System", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_project_declares_no_package_project_or_framework_references()
    {
        // Catches a reference before any code uses it, and System.* names that are NuGet packages, not the BCL.
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FieldTrack.Domain.csproj"));

        var references = project.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference")
            .Select(element => element.ToString());

        references.ShouldBeEmpty();
    }
}
