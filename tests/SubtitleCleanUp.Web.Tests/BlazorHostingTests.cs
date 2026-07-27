using System.Net;
using Shouldly;

namespace SubtitleCleanUp.Web.Tests;

[Collection(nameof(QueueApiTestCollection))]
public sealed class BlazorHostingTests
{
    [Fact]
    public async Task Blazor_framework_script_is_served()
    {
        using var application = new QueueApiApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/_framework/blazor.web.js");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");
    }

    [Fact]
    public async Task Blazor_negotiate_endpoint_is_available()
    {
        using var application = new QueueApiApplication();
        using var client = application.CreateClient();

        using var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
    }
}