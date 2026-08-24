using System.Text;
using System.Text.Json;

namespace Yatagarasu;

internal sealed class GraphJsonStreamingReader
{
    private const int InitialBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly int _documentIndex;
    private readonly CancellationToken _cancellationToken;
    private byte[] _buffer = new byte[InitialBufferSize];
    private int _buffered;
    private int _consumed;
    private long _bufferStartOffset;
    private bool _isFinalBlock;
    private JsonReaderState _state = new(new JsonReaderOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    });

    internal GraphJsonStreamingReader(
        Stream stream,
        int documentIndex,
        CancellationToken cancellationToken)
    {
        _stream = stream;
        _documentIndex = documentIndex;
        _cancellationToken = cancellationToken;
    }

    internal GraphJsonToken Current { get; private set; }

    internal int DocumentIndex => _documentIndex;

    internal bool Read()
    {
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var reader = new Utf8JsonReader(
                _buffer.AsSpan(_consumed, _buffered - _consumed),
                _isFinalBlock,
                _state);
            try
            {
                if (reader.Read())
                {
                    long tokenOffset = _bufferStartOffset
                        + _consumed
                        + reader.TokenStartIndex;
                    Current = CaptureToken(ref reader, tokenOffset);
                    _consumed += checked((int)reader.BytesConsumed);
                    _state = reader.CurrentState;
                    return true;
                }

                _consumed += checked((int)reader.BytesConsumed);
                _state = reader.CurrentState;
            }
            catch (JsonException exception)
            {
                long offset = _bufferStartOffset + _consumed + reader.BytesConsumed;
                throw new GraphJsonImportException(
                    $"JSON構文が不正です: {exception.Message}",
                    _documentIndex,
                    offset,
                    exception);
            }

            if (_isFinalBlock)
                return false;

            FillBuffer();
        }
    }

    private void FillBuffer()
    {
        if (_consumed > 0)
        {
            int remaining = _buffered - _consumed;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, _consumed, _buffer, 0, remaining);
            _bufferStartOffset += _consumed;
            _buffered = remaining;
            _consumed = 0;
        }

        if (_buffered == _buffer.Length)
        {
            if (_buffer.Length > Array.MaxLength / 2)
                throw new GraphJsonImportException(
                    "単一JSON tokenが処理可能なサイズを超えています。",
                    _documentIndex,
                    _bufferStartOffset);
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        int read = _stream.Read(_buffer, _buffered, _buffer.Length - _buffered);
        if (read == 0)
            _isFinalBlock = true;
        else
            _buffered += read;
    }

    private static GraphJsonToken CaptureToken(ref Utf8JsonReader reader, long offset)
    {
        string? text = reader.TokenType switch
        {
            JsonTokenType.PropertyName or JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            _ => null,
        };
        return new GraphJsonToken(reader.TokenType, text, offset);
    }
}

internal readonly record struct GraphJsonToken(
    JsonTokenType Type,
    string? Text,
    long ByteOffset);
