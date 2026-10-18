// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace PhotoFlow.App.Services.Vlm;

public sealed class VlmException(string message) : InvalidOperationException(message);

/// <summary>
/// The protocol of <c>PhotoFlow.Vlm</c> (level D, phase 5b): one JSON object per line. The process says
/// <c>{"type":"ready"}</c> (or an error) once the model is loaded; each request <c>{"id","image","prompt","maxTokens"}</c>
/// gets <c>{"id","text","tokens","ms"}</c> or <c>{"id","error"}</c>. Requests go one at a time.
/// </summary>
public sealed class VlmClient(TextWriter requests, TextReader answers) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _next;

    /// <summary>Waits for the model to be loaded.</summary>
    /// <exception cref="VlmException">The process could not load the model, or closed.</exception>
    public async Task WaitReadyAsync(CancellationToken ct)
    {
        var message = await ReadAsync(ct).ConfigureAwait(false) ?? throw new VlmException("The captions process closed while loading the model.");
        if (message["type"]?.GetValue<string>() != "ready")
        {
            throw new VlmException(message["message"]?.GetValue<string>() ?? "The captions process could not load the model.");
        }
    }

    /// <summary>The caption of a JPEG file.</summary>
    /// <exception cref="VlmException">The process reported an error or closed.</exception>
    public async Task<string> CaptionAsync(string imagePath, string prompt, int maxTokens, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = ++_next;
            var request = new JsonObject { ["id"] = id, ["image"] = imagePath, ["prompt"] = prompt, ["maxTokens"] = maxTokens };
            try
            {
                await requests.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
                await requests.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                throw new VlmException($"The captions process closed ({e.Message}).");
            }

            while (true)
            {
                var answer = await ReadAsync(ct).ConfigureAwait(false) ?? throw new VlmException("The captions process closed.");
                if (answer["id"]?.GetValue<long>() != id)
                {
                    continue; // a late answer of a cancelled request
                }

                return answer["error"] is { } error
                    ? throw new VlmException(error.GetValue<string>())
                    : answer["text"]?.GetValue<string>() ?? string.Empty;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<JsonNode?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await answers.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }

            if (line is null)
            {
                return null;
            }

            if (line.TrimStart().StartsWith('{'))
            {
                return JsonNode.Parse(line);
            }
        }
    }
}
