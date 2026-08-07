using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AoideSidecar.Api.Models;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.AoideSidecar.Tests;

/// <summary>
/// Deserialises a push body exactly as the endpoint does.
/// </summary>
/// <remarks>
/// The rest of the suite builds <see cref="SyncOpDto"/> objects in memory, which never
/// exercises the wire format. A type the serialiser cannot construct would sail through
/// every one of those tests and fail only against a real client.
/// </remarks>
public class PushRequestBindingTests
{
    private readonly ITestOutputHelper _output;

    public PushRequestBindingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // The same options the controller uses.
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    private const string RealisticBody = """
        {
          "deviceId": "iphone-15-pro",
          "ops": [
            {
              "opId": "3f1c9a2e-0b7d-4c1a-9e5f-2d8b6a4c1e70",
              "entity": "playlists",
              "entityId": "0f8b1d3a-2c4e-4a6b-8d1f-3e5a7c9b2d40",
              "operation": "upsert",
              "payload": {
                "id": "0f8b1d3a-2c4e-4a6b-8d1f-3e5a7c9b2d40",
                "name": "Late Night",
                "description": null,
                "folder_id": null,
                "is_smart": 0,
                "smart_rules": null,
                "sort_index": "a0",
                "updated_at": 1754500000000,
                "deleted": 0,
                "origin_device": "iphone-15-pro"
              },
              "createdAt": 1754500000000
            }
          ]
        }
        """;

    [Fact]
    public void Deserialises_a_realistic_push_body()
    {
        var request = JsonSerializer.Deserialize<PushRequest>(RealisticBody, Options);

        Assert.NotNull(request);
        Assert.Equal("iphone-15-pro", request!.DeviceId);
        Assert.Single(request.Ops);

        var op = request.Ops[0];
        Assert.Equal("3f1c9a2e-0b7d-4c1a-9e5f-2d8b6a4c1e70", op.OpId);
        Assert.Equal("playlists", op.Entity);
        Assert.Equal("upsert", op.Operation);
        Assert.Equal(JsonValueKind.Object, op.Payload.ValueKind);
        Assert.Equal(1754500000000, op.CreatedAt);
        Assert.Equal("Late Night", op.Payload.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deserialises_from_a_stream_as_the_endpoint_does()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RealisticBody));

        var request = await JsonSerializer.DeserializeAsync<PushRequest>(stream, Options);

        Assert.NotNull(request);
        Assert.Single(request!.Ops);
    }

    [Fact]
    public void Payload_survives_deserialisation_intact()
    {
        // JsonElement can reference pooled buffers. If the payload were not detached from
        // the parse, GetRawText here would read freed or reused memory.
        var request = JsonSerializer.Deserialize<PushRequest>(RealisticBody, Options)!;
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var raw = request.Ops[0].Payload.GetRawText();

        Assert.Contains("Late Night", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_ops_array_deserialises()
    {
        var request = JsonSerializer.Deserialize<PushRequest>(
            """{"deviceId":"phone","ops":[]}""", Options);

        Assert.NotNull(request);
        Assert.Empty(request!.Ops);
    }

    [Fact]
    public void A_null_ops_array_deserialises()
    {
        var request = JsonSerializer.Deserialize<PushRequest>(
            """{"deviceId":"phone","ops":null}""", Options);

        Assert.NotNull(request);
    }

    [Fact]
    public void The_response_serialises()
    {
        var json = JsonSerializer.Serialize(
            new PushResponse
            {
                Accepted = new[] { "a", "b" },
                Rejected = new[] { new RejectedOpDto { OpId = "c", Reason = "why" } },
                Cursor = 42
            },
            Options);

        _output.WriteLine(json);

        Assert.Contains("\"accepted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cursor\":42", json, StringComparison.Ordinal);
    }
}
