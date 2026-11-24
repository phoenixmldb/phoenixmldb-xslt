#pragma warning disable CA5394 // Seeded Random: reproducible test operations, not security.
using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="OutputBuffer"/> holds insertions made at or before the previous one and makes them
/// later in one pass (#639). Whatever it holds, every read gives the text a
/// <see cref="StringBuilder"/> that made each insertion at once would give.
/// </summary>
public sealed class OutputBufferTests
{
    [Fact]
    public void Insertions_in_the_order_a_recursion_unwinds_in_wait_and_are_made_in_one_pass()
    {
        var builder = new StringBuilder("ab");
        var buffer = new OutputBuffer(builder);
        buffer.Append("c");
        buffer.Insert(2, "<2>");     // before "c"
        buffer.Insert(1, "<1>");     // before "b": lower, held too
        buffer.Insert(1, "<0>");     // the same position: goes before "<1>"

        builder.ToString().Should().Be("abc", "the insertions are held");
        buffer.Length.Should().Be(12);
        buffer.Append("d");

        buffer.ToString().Should().Be("a<0><1>b<2>cd");
        builder.ToString().Should().Be("a<0><1>b<2>cd");
    }

    [Fact]
    public void Reading_or_cutting_after_the_held_text_does_not_make_the_insertions()
    {
        var builder = new StringBuilder("0123456789");
        var buffer = new OutputBuffer(builder);
        buffer.Insert(4, "<x>");

        buffer.ToString(7, 3).Should().Be("456");
        buffer[12].Should().Be('9');
        buffer.Length = 11;
        builder.ToString().Should().Be("01234567", "still held");
        buffer.ToString().Should().Be("0123<x>4567");
    }

    [Fact]
    public void Cutting_before_the_held_text_drops_it()
    {
        var builder = new StringBuilder("0123456789");
        var buffer = new OutputBuffer(builder);
        buffer.Insert(6, "<a>");
        buffer.Insert(4, "<b>");

        buffer.Length = 4;

        buffer.ToString().Should().Be("0123");
        buffer.Append("z").ToString().Should().Be("0123z");
    }

    [Fact]
    public void While_the_StringBuilder_is_read_directly_insertions_are_made_at_once()
    {
        var builder = new StringBuilder("0123");
        var buffer = new OutputBuffer(builder);
        buffer.Insert(2, "<held>");
        using (buffer.OpenRaw())
        {
            builder.ToString().Should().Be("01<held>23", "opening raw access makes the held insertions");
            buffer.Insert(1, "<now>");
            builder.ToString().Should().Be("0<now>1<held>23");
        }
        buffer.Insert(0, "<later>");
        builder.ToString().Should().Be("0<now>1<held>23");
        buffer.ToString().Should().Be("<later>0<now>1<held>23");
    }

    // Random operations, biased to insertions at or before the previous one (as a recursion unwinds)
    // and to reads and cuts on both sides of the held text; each read is compared with a
    // StringBuilder that makes every insertion at once.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_operations_read_as_a_StringBuilder_that_inserts_at_once(int seed)
    {
        var random = new Random(seed);
        var model = new StringBuilder();
        var builder = new StringBuilder();
        var buffer = new OutputBuffer(builder);
        OutputBuffer.RawAccess? raw = null;
        var lastInsert = 0;
        for (var step = 0; step < 3000; step++)
        {
            var length = model.Length;
            buffer.Length.Should().Be(length, $"seed {seed}, step {step}");
            switch (random.Next(20))
            {
                case < 6:
                {
                    var text = Text(random);
                    model.Append(text);
                    buffer.Append(text);
                    break;
                }
                case < 12:
                {
                    // Mostly at or a little before the previous insertion; sometimes anywhere.
                    var at = random.Next(4) == 0 ? random.Next(length + 1)
                        : Math.Max(0, Math.Min(length, lastInsert) - random.Next(4));
                    var text = Text(random);
                    model.Insert(at, text);
                    buffer.Insert(at, text);
                    lastInsert = at;
                    break;
                }
                case 12:
                case 13:
                {
                    var to = length == 0 ? 0 : length - random.Next(Math.Min(length, 12) + 1);
                    if (random.Next(5) == 0)
                        to = random.Next(length + 1);
                    model.Length = to;
                    buffer.Length = to;
                    break;
                }
                case 14:
                case 15:
                {
                    var start = random.Next(length + 1);
                    var count = random.Next(length - start + 1);
                    buffer.ToString(start, count).Should().Be(model.ToString(start, count), $"seed {seed}, step {step}");
                    break;
                }
                case 16:
                    if (length > 0)
                    {
                        var index = random.Next(length);
                        buffer[index].Should().Be(model[index], $"seed {seed}, step {step}");
                    }
                    break;
                case 17:
                    if (length > 0)
                    {
                        var start = random.Next(length);
                        var count = random.Next(Math.Min(4, length - start) + 1);
                        model.Remove(start, count);
                        buffer.Remove(start, count);
                    }
                    break;
                case 18:
                    if (raw is null)
                    {
                        raw = buffer.OpenRaw();
                        builder.ToString().Should().Be(model.ToString(), $"seed {seed}, step {step}: raw access");
                    }
                    else
                    {
                        builder.ToString().Should().Be(model.ToString(), $"seed {seed}, step {step}: under raw access");
                        raw.Value.Dispose();
                        raw = null;
                    }
                    break;
                default:
                    if (random.Next(10) == 0)
                    {
                        model.Clear();
                        buffer.Clear();
                    }
                    else
                    {
                        buffer.Flush();
                        builder.ToString().Should().Be(model.ToString(), $"seed {seed}, step {step}: flushed");
                    }
                    break;
            }
        }
        raw?.Dispose();
        buffer.ToString().Should().Be(model.ToString(), $"seed {seed}");
        builder.ToString().Should().Be(model.ToString(), $"seed {seed}");
    }

    public static TheoryData<int> Seeds => new(Enumerable.Range(1, 40));

    private static string Text(Random random)
    {
        var length = random.Next(1, 6);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('a' + random.Next(26));
        return new string(chars);
    }
}
