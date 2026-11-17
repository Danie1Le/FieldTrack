using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldTrack.Api.Auth;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Alerts;

namespace FieldTrack.IntegrationTests;

public sealed class AlertActionTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    private const string OperatorEmail = "operator@example.com";
    private const string OperatorName = "Olivia Operator";
    private const string OperatorPassword = "Operator-pass-1";

    [Fact]
    public async Task An_Operator_Acknowledges_an_Open_Alert_once_recording_who_and_when()
    {
        var (alert, _) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();

        using var acknowledged = await operatorClient.PostAsync($"/alerts/{alert}/acknowledge", content: null, CancellationToken);
        using var again = await operatorClient.PostAsync($"/alerts/{alert}/acknowledge", content: null, CancellationToken);

        acknowledged.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await acknowledged.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        body.GetProperty("status").GetString().ShouldBe("Acknowledged");
        body.GetProperty("acknowledgedBy").GetString().ShouldBe(OperatorName);
        body.GetProperty("acknowledgedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Resolving_an_Acknowledged_Alert_keeps_who_Acknowledged_it_and_stores_a_blank_note_as_none()
    {
        var (alert, _) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();
        using var admin = await AdminClientAsync();
        using var acknowledged = await operatorClient.PostAsync($"/alerts/{alert}/acknowledge", content: null, CancellationToken);
        var acknowledgedAt = (await acknowledged.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("acknowledgedAt").GetDateTimeOffset();

        using var response = await admin.PostAsJsonAsync($"/alerts/{alert}/resolve", new { note = "  " }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        resolved.GetProperty("status").GetString().ShouldBe("Resolved");
        resolved.GetProperty("resolveReason").GetString().ShouldBe("Manual");
        resolved.GetProperty("acknowledgedBy").GetString().ShouldBe(OperatorName);
        resolved.GetProperty("acknowledgedAt").GetDateTimeOffset().ShouldBe(acknowledgedAt);
        resolved.GetProperty("resolvedBy").GetString().ShouldBe(UserSeeder.AdminDisplayName);
        resolved.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_Operator_never_sees_a_users_email_in_Alerts()
    {
        var (alert, _) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();
        using var admin = await AdminClientAsync();
        using var acknowledged = await admin.PostAsync($"/alerts/{alert}/acknowledge", content: null, CancellationToken);

        using var resolved = await operatorClient.PostAsync($"/alerts/{alert}/resolve", content: null, CancellationToken);
        string[] bodies = [
            await resolved.Content.ReadAsStringAsync(CancellationToken),
            await operatorClient.GetStringAsync($"/alerts/{alert}", CancellationToken),
            await operatorClient.GetStringAsync("/alerts?active=false", CancellationToken)];

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
        bodies[1].ShouldContain(UserSeeder.AdminDisplayName);
        bodies[1].ShouldContain(OperatorName);
        foreach (var body in bodies)
        {
            body.ShouldNotContain(FieldTrackApiFactory.AdminEmail, Case.Insensitive);
            body.ShouldNotContain(OperatorEmail, Case.Insensitive);
        }
    }

    [Fact]
    public async Task Resolving_by_hand_while_the_condition_holds_latches_the_rule_until_it_clears_and_breaches_again()
    {
        var (alert, t) = await OpenAlertAsync();
        using var admin = await AdminClientAsync();
        // The Resolve form warns before anyone confirms.
        (await admin.GetFromJsonAsync<JsonElement>($"/alerts/{alert}", CancellationToken)).GetProperty("latched").GetBoolean().ShouldBeTrue();

        using var response = await admin.PostAsJsonAsync($"/alerts/{alert}/resolve", new { note = "Coolant topped up" }, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        await WriteAsync([Received(1, t.AddSeconds(1), 112)]);
        var stillBreaching = await ListAsync(admin, "/alerts");
        await WriteAsync([Received(2, t.AddSeconds(2), 90)]);
        await WriteAsync([Received(3, t.AddSeconds(3), 111)]);
        var alerts = await ListAsync(admin, "/alerts");

        resolved.GetProperty("status").GetString().ShouldBe("Resolved");
        resolved.GetProperty("resolveReason").GetString().ShouldBe("Manual");
        resolved.GetProperty("resolvedBy").GetString().ShouldBe(UserSeeder.AdminDisplayName);
        resolved.GetProperty("note").GetString().ShouldBe("Coolant topped up");
        resolved.GetProperty("latched").GetBoolean().ShouldBeTrue();
        stillBreaching.ShouldHaveSingleItem();
        // The clear found the Alert already Resolved: the platform's conditional resolve changed nothing.
        var manual = alerts.Single(a => a.GetProperty("id").GetString() == alert);
        manual.GetProperty("resolveReason").GetString().ShouldBe("Manual");
        manual.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBe(resolved.GetProperty("resolvedAt").GetDateTimeOffset());
        var raisedAgain = alerts.Single(a => a.GetProperty("id").GetString() != alert);
        raisedAgain.GetProperty("status").GetString().ShouldBe("Open");
        raisedAgain.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(3);
        raisedAgain.GetProperty("latched").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Resolving_an_Alert_the_platform_already_Resolved_is_refused_and_leaves_it_Cleared()
    {
        var (alert, t) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();
        await WriteAsync([Received(1, t.AddSeconds(1), 90)]);

        using var response = await operatorClient.PostAsJsonAsync($"/alerts/{alert}/resolve", new { note = "Too late" }, CancellationToken);
        var stored = await operatorClient.GetFromJsonAsync<JsonElement>($"/alerts/{alert}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stored.GetProperty("resolveReason").GetString().ShouldBe("Cleared");
        stored.GetProperty("resolvedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        stored.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        stored.GetProperty("latched").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_resolves_of_one_Alert_end_with_one_winner_and_the_rest_refused()
    {
        var (alert, _) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            operatorClient.PostAsJsonAsync($"/alerts/{alert}/resolve", new { note = $"Note {i}" }, CancellationToken)));

        try
        {
            responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.OK, .. Enumerable.Repeat(HttpStatusCode.Conflict, 7)]);
            var winner = await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
            using var admin = await AdminClientAsync();
            var stored = await admin.GetFromJsonAsync<JsonElement>($"/alerts/{alert}", CancellationToken);
            stored.GetProperty("note").GetString().ShouldBe(winner.GetProperty("note").GetString());
            stored.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBe(winner.GetProperty("resolvedAt").GetDateTimeOffset());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Resolve_takes_no_note_and_refuses_a_Resolved_Alert_an_unknown_Alert_and_a_note_over_MaxNoteLength()
    {
        var (alert, _) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();
        var tooLong = new { note = new string('n', Alert.MaxNoteLength + 1) };

        using var refusedNote = await operatorClient.PostAsJsonAsync($"/alerts/{alert}/resolve", tooLong, CancellationToken);
        using var resolved = await operatorClient.PostAsync($"/alerts/{alert}/resolve", content: null, CancellationToken);
        using var again = await operatorClient.PostAsync($"/alerts/{alert}/resolve", content: null, CancellationToken);
        using var acknowledgeResolved = await operatorClient.PostAsync($"/alerts/{alert}/acknowledge", content: null, CancellationToken);
        using var unknown = await operatorClient.PostAsync($"/alerts/{Guid.NewGuid()}/resolve", content: null, CancellationToken);
        using var unknownAcknowledge = await operatorClient.PostAsync($"/alerts/{Guid.NewGuid()}/acknowledge", content: null, CancellationToken);

        refusedNote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resolved.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        body.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("resolvedBy").GetString().ShouldBe(OperatorName);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        acknowledgeResolved.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownAcknowledge.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // An Engine overheat rule on an Asset fitted with TRK-007 and one breaching Reading at Device time t: one Open Alert.
    [Fact]
    public async Task Acknowledge_and_Resolve_are_pushed_live_by_display_name_with_no_email()
    {
        var (alert, _) = await OpenAlertAsync();
        using var operatorClient = await OperatorClientAsync();
        await using var live = await LiveClient.ConnectAsync(Fixture.Api, operatorClient.DefaultRequestHeaders.Authorization!.Parameter);

        using (var acknowledge = await operatorClient.PostAsync($"/alerts/{alert}/acknowledge", content: null, CancellationToken))
        {
            acknowledge.EnsureSuccessStatusCode();
        }

        var acknowledged = await live.NextAsync(LiveHub.AlertUpdated, pushed => pushed.GetProperty("status").GetString() == "Acknowledged");
        using (var resolve = await operatorClient.PostAsync($"/alerts/{alert}/resolve", content: null, CancellationToken))
        {
            resolve.EnsureSuccessStatusCode();
        }

        var resolved = await live.NextAsync(LiveHub.AlertUpdated, pushed => pushed.GetProperty("status").GetString() == "Resolved");

        acknowledged.GetProperty("id").GetString().ShouldBe(alert);
        acknowledged.GetProperty("acknowledgedBy").GetString().ShouldBe(OperatorName);
        resolved.GetProperty("resolvedBy").GetString().ShouldBe(OperatorName);
        // Every dashboard gets every push, the Demo Operator's too, so no user's email may travel in one.
        acknowledged.GetRawText().ShouldNotContain("@");
        resolved.GetRawText().ShouldNotContain("@");
        (await live.NextAsync(LiveHub.AssetsUpdated)).EnumerateArray().ShouldHaveSingleItem().GetProperty("status").GetString().ShouldBe("Normal");
    }

    private async Task<(string Alert, DateTimeOffset T)> OpenAlertAsync()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        // After the Attachment, so the Reading is stamped with the Asset.
        var t = DateTimeOffset.UtcNow;
        await WriteAsync([Received(0, t, 110)]);
        return ((await ListAsync(admin, "/alerts")).ShouldHaveSingleItem().GetProperty("id").GetString()!, t);
    }

    private async Task<HttpClient> OperatorClientAsync()
    {
        using (var admin = await AdminClientAsync())
        {
            await AuthClient.CreateUserAsync(admin, OperatorEmail, "Operator", OperatorPassword, OperatorName);
        }

        var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, OperatorEmail, OperatorPassword);
        return client;
    }
}
