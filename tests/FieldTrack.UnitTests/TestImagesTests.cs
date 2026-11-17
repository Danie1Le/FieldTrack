using System.Reflection;
using System.Text.RegularExpressions;
using FieldTrack.IntegrationTests;

namespace FieldTrack.UnitTests;

public sealed partial class TestImagesTests
{
    // Local-only tooling no test starts, so TestImages has no constant for it (#48).
    private static readonly string[] _localOnlyImages = ["grafana/otel-lgtm:"];

    [Fact]
    public void TestImages_match_the_images_docker_compose_pulls()
    {
        var composeImages = ImageLine()
            .Matches(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docker-compose.yml")))
            .Select(match => match.Groups["image"].Value)
            .Where(image => !_localOnlyImages.Any(prefix => image.StartsWith(prefix, StringComparison.Ordinal)))
            .Order();

        var testImages = typeof(TestImages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order();

        testImages.ShouldBe(composeImages, "TestImages and docker-compose.yml must pin the same image tags");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImageLine();
}
