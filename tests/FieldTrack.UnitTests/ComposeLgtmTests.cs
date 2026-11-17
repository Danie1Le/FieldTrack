using YamlDotNet.RepresentationModel;

namespace FieldTrack.UnitTests;

public sealed class ComposeLgtmTests
{
    [Fact]
    public void Grafana_opens_as_an_Editor_and_does_not_report_analytics()
    {
        // Anonymous Admin could add a PostgreSQL datasource pointing at the database, and the image
        // defaults to Admin when the role is unset (#48).
        using var compose = File.OpenText(Path.Combine(AppContext.BaseDirectory, "docker-compose.yml"));
        var yaml = new YamlStream();
        yaml.Load(compose);
        var services = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["services"];
        var environment = (YamlMappingNode)((YamlMappingNode)services["lgtm"])["environment"];

        ((YamlScalarNode)environment["GF_AUTH_ANONYMOUS_ORG_ROLE"]).Value.ShouldBe("Editor");
        ((YamlScalarNode)environment["GF_ANALYTICS_REPORTING_ENABLED"]).Value.ShouldBe("false");
    }
}
