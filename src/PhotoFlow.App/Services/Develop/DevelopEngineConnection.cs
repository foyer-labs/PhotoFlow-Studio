// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Develop;

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// Protocol of the development engine (decision S1): one JSON request per line on <paramref name="requests"/>, one
/// answer per line on <paramref name="answers"/> (<c>{"id","ok","result"}</c>), progress events
/// (<c>{"id","event":"progress","fraction","message"}</c>) and cancellation (<c>{"cmd":"cancel","target"}</c>).
/// Several requests can be in flight: the engine has a short and a long queue.
/// </summary>
public sealed class DevelopEngineConnection : IDisposable
{
    private readonly TextWriter _requests;
    private readonly TextReader _answers;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private long _nextId;

    public DevelopEngineConnection(TextWriter requests, TextReader answers)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _answers = answers ?? throw new ArgumentNullException(nameof(answers));
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Raised once when the answers end (the engine closed).</summary>
    public event EventHandler? Closed;

    public bool IsClosed { get; private set; }

    /// <summary>Waits for the <c>ready</c> message of the engine and returns its version.</summary>
    public Task<string> WaitReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    public async Task<JsonObject> CallAsync(string command, JsonObject? arguments, IProgress<DevelopProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (IsClosed)
        {
            throw new DevelopEngineException(Localization.Strings.Get("Develop.Engine.Closed"));
        }

        var id = Interlocked.Increment(ref _nextId);
        var request = arguments?.DeepClone().AsObject() ?? [];
        request["id"] = id;
        request["cmd"] = command;
        var pending = new Pending(new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously), progress);
        _pending[id] = pending;
        await SendAsync(request.ToJsonString(), ct).ConfigureAwait(false);
        try
        {
            return await pending.Answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The engine stops the work at its next step. Sent here and not from a callback registered on the token:
            // callbacks run last-registered first, so WaitAsync would end the call and remove the registration first.
            _pending.TryRemove(id, out _);
            await SendAsync(new JsonObject { ["cmd"] = "cancel", ["target"] = id }.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SendAsync(string line, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _requests.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _requests.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) when (IsClosed)
        {
            // the engine is gone: the pending calls fail in Close()
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _answers.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith('{'))
                {
                    continue; // the engine writes only JSON on this channel; anything else is ignored
                }

                JsonObject message;
                try
                {
                    message = JsonNode.Parse(line)?.AsObject() ?? [];
                }
                catch (JsonException)
                {
                    continue;
                }

                Dispatch(message);
            }
        }
        catch (IOException)
        {
            // closed pipe: same as the end of the answers
        }
        catch (ObjectDisposedException)
        {
        }

        Close();
    }

    private void Dispatch(JsonObject message)
    {
        var id = Json.Long(message["id"], -1);
        if (Json.NullableString(message["event"]) is { } kind)
        {
            if (kind == "ready")
            {
                _ready.TrySetResult(Json.String(message["version"]));
            }
            else if (kind == "progress" && _pending.TryGetValue(id, out var running))
            {
                running.Progress?.Report(new DevelopProgress(Json.Double(message["fraction"]), Json.String(message["message"])));
            }

            return;
        }

        if (!_pending.TryRemove(id, out var pending))
        {
            return; // answer to a request already abandoned
        }

        if (Json.Bool(message["ok"]))
        {
            pending.Answer.TrySetResult(message["result"]?.DeepClone() as JsonObject ?? []);
        }
        else if (Json.Bool(message["cancelled"]))
        {
            pending.Answer.TrySetCanceled();
        }
        else
        {
            pending.Answer.TrySetException(new DevelopEngineException(Json.String(message["error"], "?"), Json.NullableString(message["detail"])));
        }
    }

    private void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        _ready.TrySetException(new DevelopEngineException(Localization.Strings.Get("Develop.Engine.Closed")));
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Answer.TrySetException(new DevelopEngineException(Localization.Strings.Get("Develop.Engine.Closed")));
            }
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        _ = _reader;
    }

    private sealed record Pending(TaskCompletionSource<JsonObject> Answer, IProgress<DevelopProgress>? Progress);
}
