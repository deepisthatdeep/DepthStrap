namespace Bloxstrap.Utility;

/// <summary>A growing UTF-8 log may end between bytes or in the middle of a line.</summary>
internal sealed class LogLineBuffer
{
    internal const int MaximumLineLength = 65536;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();
    private readonly char[] _characters = new char[4096];
    private bool _discarding;

    internal void Reset() { _decoder.Reset(); _pending.Clear(); _discarding = false; }

    internal void Append(ReadOnlySpan<byte> bytes, Action<string> receive)
    {
        while (!bytes.IsEmpty)
        {
            _decoder.Convert(bytes, _characters, flush: false, out int consumed, out int count, out _);
            bytes = bytes[consumed..];
            for (int i = 0; i < count; i++)
            {
                char value = _characters[i];
                if (value == '\n')
                {
                    if (!_discarding) receive(_pending.ToString().TrimEnd('\r').TrimStart('\uFEFF'));
                    _pending.Clear(); _discarding = false;
                }
                else if (!_discarding)
                {
                    if (_pending.Length == MaximumLineLength) { _pending.Clear(); _discarding = true; }
                    else _pending.Append(value);
                }
            }
        }
    }
}
