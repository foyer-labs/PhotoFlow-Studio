// SPDX-License-Identifier: Apache-2.0

using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The protocol of the level D captions process (phase 5b) against a fake process on in-memory pipes: ready, an
/// answer, an error of one request, a late answer of another request ignored, the process closing.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class VlmClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_and_answers_follow_the_protocol()
    {
        using var requests = new AnonymousPipeServerStream(PipeDirection.Out);
        using var requestsRead = new AnonymousPipeClientStream(PipeDirection.In, requests.ClientSafePipeHandle);
        using var answers = new AnonymousPipeServerStream(PipeDirection.Out);
        using var answersRead = new AnonymousPipeClientStream(PipeDirection.In, answers.ClientSafePipeHandle);
        var server = Task.Run(async () =>
        {
            using var reader = new StreamReader(requestsRead, Encoding.UTF8);
            await using var writer = new StreamWriter(answers, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync("loading the model…");          // noise is skipped
            await writer.WriteLineAsync("""{"type":"ready"}""");
            for (var i = 0; i < 2; i++)
            {
                var request = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
                var id = request["id"]!.GetValue<long>();
                await writer.WriteLineAsync("""{"id":99,"text":"late"}""");    // an answer to nobody
                await writer.WriteLineAsync(request["image"]!.GetValue<string>() == "bad.jpg"
                    ? new JsonObject { ["id"] = id, ["error"] = "Image not readable" }.ToJsonString()
                    : new JsonObject { ["id"] = id, ["text"] = $"Una moto rossa in pista. ({request["maxTokens"]})", ["tokens"] = 7, ["ms"] = 900 }.ToJsonString());
            }
        }, Ct);

        using var client = new VlmClient(new StreamWriter(requests, new UTF8Encoding(false)), new StreamReader(answersRead, Encoding.UTF8));
        await client.WaitReadyAsync(Ct);
        Assert.Equal("Una moto rossa in pista. (128)", await client.CaptionAsync("photo.jpg", "Descrivi la foto.", 128, Ct));
        var error = await Assert.ThrowsAsync<VlmException>(() => client.CaptionAsync("bad.jpg", "Descrivi la foto.", 128, Ct));
        Assert.Equal("Image not readable", error.Message);
        await server;
        answers.Dispose(); // the process closes
        await Assert.ThrowsAsync<VlmException>(() => client.CaptionAsync("photo.jpg", "Descrivi la foto.", 128, Ct));
    }

    [Fact]
    public async Task A_model_that_cannot_load_is_reported()
    {
        using var answers = new StringReader("""{"type":"error","message":"CUDA not available"}""" + "\n");
        using var client = new VlmClient(TextWriter.Null, answers);
        var error = await Assert.ThrowsAsync<VlmException>(() => client.WaitReadyAsync(Ct));
        Assert.Equal("CUDA not available", error.Message);
    }
}
