// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Threading.Channels;

namespace PhotoFlow.Tests.Develop;

/// <summary>An in-memory line pipe: what is written on <see cref="Writer"/> is read on <see cref="Reader"/>.</summary>
public sealed class LineChannel
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public LineChannel()
    {
        Writer = new ChannelWriter(_lines.Writer);
        Reader = new ChannelReader(_lines.Reader);
    }

    public TextWriter Writer { get; }

    public TextReader Reader { get; }

    /// <summary>End of the lines: the reader gets null.</summary>
    public void Complete() => _lines.Writer.TryComplete();

    public async Task<string> NextAsync(CancellationToken ct) => await _lines.Reader.ReadAsync(ct);

    private sealed class ChannelWriter(ChannelWriter<string> lines) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                lines.TryWrite(_pending.ToString().TrimEnd('\r'));
                _pending.Clear();
            }
            else
            {
                _pending.Append(value);
            }
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lines.TryWrite(_pending.Append(buffer.Span).ToString());
            _pending.Clear();
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ChannelReader(ChannelReader<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await lines.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}
