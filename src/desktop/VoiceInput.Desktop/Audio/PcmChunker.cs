namespace VoiceInput.Desktop.Audio;

public sealed class PcmChunker
{
    private readonly byte[] _buffer;
    private int _buffered;

    public PcmChunker(int chunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkBytes);
        _buffer = new byte[chunkBytes];
    }

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> input)
    {
        var chunks = new List<byte[]>();
        while (!input.IsEmpty)
        {
            var count = Math.Min(_buffer.Length - _buffered, input.Length);
            input[..count].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += count;
            input = input[count..];

            if (_buffered != _buffer.Length)
            {
                continue;
            }

            chunks.Add((byte[])_buffer.Clone());
            _buffered = 0;
        }

        return chunks;
    }
}
