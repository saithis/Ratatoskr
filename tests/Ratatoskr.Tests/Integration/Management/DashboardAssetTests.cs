using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The dashboard renders another system's data — service names, handler keys, error text, message
/// payloads — none of which it controls. These tests pin the properties that keep that data from
/// becoming script: structurally in the shipped assets, and behaviourally through the API.
/// </summary>
public partial class DashboardAssetTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : DashboardTestBase(rabbitMq, postgres)
{
    private const string HostilePayload = "<script>alert('xss')</script>";

    [Test]
    [Arguments("app.js")]
    [Arguments("api.js")]
    [Arguments("audit.js")]
    [Arguments("bulk.js")]
    [Arguments("detail.js")]
    [Arguments("dom.js")]
    [Arguments("messages.js")]
    [Arguments("navigation.js")]
    [Arguments("state.js")]
    [Arguments("stream.js")]
    [Arguments("toast.js")]
    public async Task Script_NeverInterpretsDataAsMarkup(string fileName)
    {
        var source = await ReadAssetAsync($"js.{fileName}");

        foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(" })
        {
            source
                .Should()
                .NotContain(
                    sink,
                    $"'{sink}' turns data into markup, and every value this dashboard renders comes from another system"
                );
        }
    }

    [Test]
    [Arguments("app.js")]
    [Arguments("api.js")]
    [Arguments("audit.js")]
    [Arguments("bulk.js")]
    [Arguments("detail.js")]
    [Arguments("dom.js")]
    [Arguments("messages.js")]
    [Arguments("navigation.js")]
    [Arguments("state.js")]
    [Arguments("stream.js")]
    [Arguments("toast.js")]
    public async Task Script_StaysReadable(string fileName)
    {
        // The file this replaced was 200-400 character single lines with no blank lines, in a
        // repository whose other code is carefully formatted. Readability is a property worth a test.
        var lines = (await ReadAssetAsync($"js.{fileName}")).Split('\n');

        var tooLong = lines
            .Select((line, index) => (Number: index + 1, Text: line.TrimEnd()))
            .Where(line => line.Text.Length > 110)
            .ToArray();

        tooLong
            .Should()
            .BeEmpty(
                $"lines in {fileName} should stay readable; offenders: "
                    + string.Join(
                        ", ",
                        tooLong.Select(line =>
                            $"line {line.Number.ToString(CultureInfo.InvariantCulture)} ({line.Text.Length.ToString(CultureInfo.InvariantCulture)} chars)"
                        )
                    )
            );
    }

    [Test]
    public async Task Markup_CarriesNoInlineScript()
    {
        // The Content-Security-Policy is script-src 'self' with no unsafe-inline, so an inline
        // handler would not run anyway — it would just silently break the page.
        var html = await ReadAssetAsync("index.html");

        InlineHandler.IsMatch(html).Should().BeFalse("no element may carry an inline event handler");
        InlineScript.IsMatch(html).Should().BeFalse("no <script> element may carry inline code");
        html.Should().Contain("""<script type="module" src="js/app.js"></script>""");
    }

    [Test]
    public async Task HostileValues_CrossTheApiAsDataNotMarkup()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();
        var id = await SeedHostileOutboxAsync();

        using var list = await HttpClient.GetAsync($"{DashboardContextUrl}/outbox");
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await list.Content.ReadFromJsonAsync<CursorPage<OutboxListItem>>(
            ManagementJson.Options
        );
        var item = page!.Items.Should().ContainSingle().Subject;

        // The value survives intact — the API must not mangle it — and it travels as a JSON string,
        // which is the only place it can safely be: the client renders it with textContent.
        item.MessageType.Should().Be(HostilePayload);
        item.LastError.Should().Contain(HostilePayload);

        var raw = await list.Content.ReadAsStringAsync();
        raw.Should().NotContain("<script>", "JSON encoding must not emit raw markup");

        using var detail = await HttpClient.GetAsync($"{DashboardContextUrl}/outbox/{id}");
        var body = await detail.Content.ReadAsStringAsync();
        body.Should().NotContain("<script>");
        JsonDocument.Parse(body).RootElement.GetProperty("messageType").GetString()
            .Should()
            .Be(HostilePayload);
    }

    [Test]
    public async Task HostileServiceNames_CrossTheDiscoveryApiAsData()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();

        using var response = await HttpClient.GetAsync("/ratatoskr/api/services");
        var raw = await response.Content.ReadAsStringAsync();

        raw.Should().NotContain("<script>");
        JsonDocument.Parse(raw).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    private async Task<Guid> SeedHostileOutboxAsync()
    {
        var id = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var properties = new MessageProperties
            {
                Type = HostilePayload,
                Id = HostilePayload,
                Subject = HostilePayload,
            };
            var content = Encoding.UTF8.GetBytes($$"""{"payload":"{{HostilePayload}}"}""");
            var entity = OutboxMessageEntity.Create(content, properties, time, "efcore");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                entity.PublishFailed($"handler blew up on {HostilePayload}", time, 3, TimeSpan.FromSeconds(1));
            }

            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    private static async Task<string> ReadAssetAsync(string relativePath)
    {
        var assembly = typeof(Ratatoskr.UI.RatatoskrUiEndpointExtensions).Assembly;
        await using var stream =
            assembly.GetManifestResourceStream($"Ratatoskr.UI.wwwroot.{relativePath}")
            ?? throw new InvalidOperationException(
                $"'{relativePath}' is not embedded. Embedded names: "
                    + string.Join(", ", assembly.GetManifestResourceNames())
            );

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [GeneratedRegex("""<[^>]*\son[a-z]+\s*=""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineHandler { get; }

    [GeneratedRegex("""<script(?![^>]*\ssrc=)[^>]*>\s*\S""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineScript { get; }
}
