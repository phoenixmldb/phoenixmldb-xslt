using System.Text;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// A transformation's output buffer: the <see cref="StringBuilder"/> the result is written to, which
/// can also take text inserted before content written earlier, in constant time when the insertions
/// come in the order a recursion unwinds in (#639).
/// </summary>
/// <remarks>
/// <para>
/// An element's start tag is known only after its content, which can add attributes and namespace
/// nodes to it, so it is inserted before the content (<c>MoveStartTagBefore</c>). In a deep
/// recursion each level inserts at or before the position where the level below it inserted.
/// <see cref="StringBuilder.Insert(int, string)"/> adds a chunk for text that does not fit the room
/// in the chunk there, and walks every chunk after the insertion point; with a chunk for each level,
/// the insertions took time about the square of the depth.
/// </para>
/// <para>
/// Here an insertion at or before the lowest one held is held in a list. The held text is put in
/// place, all of it in one pass over the text after the lowest insertion point, when something reads
/// or cuts the buffer there, or when the owner asks (<see cref="Flush"/>). Appending, the length, and
/// reading, cutting or inserting after the held text do not need it. Positions are those of the
/// text with the held insertions made: what the buffer holds is always the same as if each
/// insertion had been made when it was asked for.
/// </para>
/// </remarks>
internal sealed class OutputBuffer(StringBuilder text)
{
    private readonly StringBuilder _text = text;

    // Insertions not made yet: the text to go before the character at Position of _text. Positions do
    // not increase along the list; at one position a later insertion goes before an earlier one.
    private readonly List<(int Position, string Text)> _held = [];
    private int _heldLength;

    // The position, in the buffer's text, where the held text ends: after it, a position less the
    // held length is a position of _text. Before the lowest insertion point, a position is the same in both.
    private int HeldEnd => _held[0].Position + _heldLength;

    // While code that reads the StringBuilder itself runs (OpenRaw), insertions are made at once.
    private int _rawUsers;
    private int LowestHeld => _held[^1].Position;

    /// <summary>The <see cref="StringBuilder"/>, with the held insertions made.</summary>
    public StringBuilder Builder
    {
        get
        {
            Flush();
            return _text;
        }
    }

    public int Length
    {
        get => _text.Length + _heldLength;
        set
        {
            if (_held.Count > 0)
            {
                if (value >= HeldEnd)
                {
                    _text.Length = value - _heldLength;
                    return;
                }
                if (value <= LowestHeld)
                    Drop();
                else
                    Flush();
            }
            _text.Length = value;
        }
    }

    public char this[int index]
    {
        get
        {
            if (_held.Count > 0)
            {
                if (index >= HeldEnd)
                    return _text[index - _heldLength];
                if (index >= LowestHeld)
                    Flush();
            }
            return _text[index];
        }
    }

    public OutputBuffer Append(char value)
    {
        _text.Append(value);
        return this;
    }

    public OutputBuffer Append(string? value)
    {
        _text.Append(value);
        return this;
    }

    public OutputBuffer Append(StringBuilder? value)
    {
        _text.Append(value);
        return this;
    }

    public OutputBuffer Append(string? value, int startIndex, int count)
    {
        _text.Append(value, startIndex, count);
        return this;
    }

    public OutputBuffer Insert(int index, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return this;
        if (index == Length)
            return Append(value);
        if (_rawUsers > 0)
        {
            Flush();
            _text.Insert(index, value);
            return this;
        }
        if (_held.Count > 0 && index > LowestHeld)
        {
            if (index >= HeldEnd)
            {
                _text.Insert(index - _heldLength, value);
                return this;
            }
            Flush();
        }
        _held.Add((index, value));
        _heldLength += value.Length;
        return this;
    }

    public OutputBuffer Remove(int startIndex, int length)
    {
        Flush();
        _text.Remove(startIndex, length);
        return this;
    }

    public OutputBuffer Clear()
    {
        Drop();
        _text.Clear();
        return this;
    }

    public override string ToString()
    {
        Flush();
        return _text.ToString();
    }

    public string ToString(int startIndex, int length)
    {
        if (_held.Count > 0)
        {
            if (length == 0)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, Length);
                return string.Empty;
            }
            if (startIndex >= HeldEnd)
                return _text.ToString(startIndex - _heldLength, length);
            if (startIndex + length > LowestHeld)
                Flush();
        }
        return _text.ToString(startIndex, length);
    }

    /// <summary>Makes the held insertions, in one pass over the text after the lowest of them.</summary>
    public void Flush()
    {
        if (_held.Count == 0)
            return;
        var from = LowestHeld;
        var tail = _text.ToString(from, _text.Length - from);
        _text.Length = from;
        var at = from;
        // From the lowest position up; at one position, the latest insertion first.
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            var (position, value) = _held[i];
            _text.Append(tail, at - from, position - at);
            _text.Append(value);
            at = position;
        }
        _text.Append(tail, at - from, tail.Length - (at - from));
        Drop();
    }

    /// <summary>
    /// For code that is given the <see cref="StringBuilder"/> itself (<see cref="Builder"/>) and reads
    /// it while the transformation writes to this buffer: until the result is disposed, insertions are
    /// made at once, so the StringBuilder always holds the buffer's text.
    /// </summary>
    public RawAccess OpenRaw()
    {
        Flush();
        _rawUsers++;
        return new RawAccess(this);
    }

    public readonly struct RawAccess(OutputBuffer owner) : IDisposable
    {
        public void Dispose() => owner._rawUsers--;
    }

    private void Drop()
    {
        _held.Clear();
        _heldLength = 0;
    }
}
