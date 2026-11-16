using System.Net;

namespace FieldTrack.IntegrationTests;

public sealed class ApiDocsTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task Scalar_api_reference_is_served()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync("/scalar/v1", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }
}
