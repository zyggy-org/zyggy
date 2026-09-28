using System.Text;
using System.Text.Unicode;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Zyggy.Core.Envelope;

/// <summary>
/// Splits an envelope file into front matter and body at the byte level, and reads the front matter into a
/// <see cref="FrontMatterMapping"/> through the YAML event stream (founding spec §4 "Canonical form, normative details",
/// reading rules). The only place that parses YAML.
/// </summary>
internal static class FrontMatterReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static EnvelopeRejection? Read(ReadOnlyMemory<byte> file, out FrontMatterMapping frontMatter, out ReadOnlyMemory<byte> body)
    {
        frontMatter = null!;
        body = default;

        ReadOnlySpan<byte> span = file.Span;
        int start = span.StartsWith("﻿"u8) ? 3 : 0;
        int afterOpening = OpeningDelimiterEnd(span, start);
        if (afterOpening < 0)
        {
            return Malformed("missing opening delimiter: the file must start with a line '---'");
        }

        if (!TryFindClosingDelimiter(span, afterOpening, out int frontMatterEnd, out int bodyStart))
        {
            return Malformed("missing closing delimiter: no line '---' ends the front matter");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(span[afterOpening..frontMatterEnd]);
        }
        catch (DecoderFallbackException)
        {
            return Malformed("front matter is not valid UTF-8");
        }

        if (!Utf8.IsValid(span[bodyStart..]))
        {
            return Malformed("body is not valid UTF-8");
        }

        EnvelopeRejection? rejection = ReadYaml(text, out frontMatter);
        if (rejection is null)
        {
            body = file[bodyStart..].ToArray();
        }

        return rejection;
    }

    private static int OpeningDelimiterEnd(ReadOnlySpan<byte> span, int start)
    {
        ReadOnlySpan<byte> rest = span[start..];
        return rest.StartsWith("---\n"u8) ? start + 4
            : rest.StartsWith("---\r\n"u8) ? start + 5
            : -1;
    }

    private static bool TryFindClosingDelimiter(ReadOnlySpan<byte> span, int from, out int lineStart, out int bodyStart)
    {
        lineStart = from;
        while (lineStart < span.Length)
        {
            int newline = span[lineStart..].IndexOf((byte)'\n');
            int lineEnd = newline < 0 ? span.Length : lineStart + newline;
            ReadOnlySpan<byte> line = span[lineStart..lineEnd];
            if (newline >= 0 && line.EndsWith("\r"u8))
            {
                line = line[..^1];
            }

            if (line.SequenceEqual("---"u8))
            {
                bodyStart = newline < 0 ? span.Length : lineEnd + 1;
                return true;
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        bodyStart = -1;
        return false;
    }

    private static EnvelopeRejection? ReadYaml(string text, out FrontMatterMapping frontMatter)
    {
        frontMatter = null!;
        try
        {
            var parser = new Parser(new StringReader(text));
            var events = new EventCursor(parser);
            events.Expect<StreamStart>();
            ParsingEvent next = events.Next();
            if (next is not DocumentStart document)
            {
                return Malformed("root is not a mapping: the front matter is empty");
            }

            if (document.Version is not null || HasDeclaredTagDirective(document))
            {
                return Malformed("directives are not allowed in the front matter");
            }

            ParsingEvent root = events.Next();
            if (root is not MappingStart)
            {
                return Malformed("root is not a mapping");
            }

            EnvelopeRejection? rejection = ReadMapping(events, (MappingStart)root, out FrontMatterMapping? mapping);
            if (rejection is not null)
            {
                return rejection;
            }

            events.Expect<DocumentEnd>();
            if (events.Next() is not StreamEnd)
            {
                return Malformed("more than one document in the front matter");
            }

            frontMatter = mapping!;
            return null;
        }
        catch (YamlException exception)
        {
            return Malformed("yaml: " + exception.Message);
        }
    }

    private static EnvelopeRejection? ReadNode(EventCursor events, ParsingEvent current, out FrontMatterNode? node)
    {
        node = null;
        if (current is AnchorAlias)
        {
            return Malformed("aliases are not allowed in the front matter");
        }

        if (current is NodeEvent nodeEvent)
        {
            if (!nodeEvent.Anchor.IsEmpty)
            {
                return Malformed("anchors are not allowed in the front matter");
            }

            if (!nodeEvent.Tag.IsEmpty)
            {
                return Malformed("explicit tags are not allowed in the front matter");
            }
        }

        switch (current)
        {
            case Scalar scalar:
                node = new FrontMatterScalar(scalar.Value);
                return null;
            case SequenceStart:
                EnvelopeRejection? sequenceRejection = ReadSequence(events, out FrontMatterSequence? sequence);
                node = sequence;
                return sequenceRejection;
            case MappingStart mappingStart:
                EnvelopeRejection? mappingRejection = ReadMapping(events, mappingStart, out FrontMatterMapping? mapping);
                node = mapping;
                return mappingRejection;
            default:
                return Malformed("yaml: unexpected " + current.GetType().Name);
        }
    }

    private static EnvelopeRejection? ReadSequence(EventCursor events, out FrontMatterSequence? sequence)
    {
        sequence = null;
        var items = new List<FrontMatterNode>();
        for (ParsingEvent current = events.Next(); current is not SequenceEnd; current = events.Next())
        {
            EnvelopeRejection? rejection = ReadNode(events, current, out FrontMatterNode? item);
            if (rejection is not null)
            {
                return rejection;
            }

            items.Add(item!);
        }

        sequence = new FrontMatterSequence(items);
        return null;
    }

    private static EnvelopeRejection? ReadMapping(EventCursor events, MappingStart start, out FrontMatterMapping? mapping)
    {
        mapping = null;
        if (!start.Anchor.IsEmpty)
        {
            return Malformed("anchors are not allowed in the front matter");
        }

        if (!start.Tag.IsEmpty)
        {
            return Malformed("explicit tags are not allowed in the front matter");
        }

        var entries = new Dictionary<string, FrontMatterNode>(StringComparer.Ordinal);
        for (ParsingEvent current = events.Next(); current is not MappingEnd; current = events.Next())
        {
            if (current is AnchorAlias)
            {
                return Malformed("aliases are not allowed in the front matter");
            }

            if (current is not Scalar key)
            {
                return Malformed("complex keys are not allowed: every mapping key must be a scalar");
            }

            if (!key.Anchor.IsEmpty)
            {
                return Malformed("anchors are not allowed in the front matter");
            }

            if (!key.Tag.IsEmpty)
            {
                return Malformed("explicit tags are not allowed in the front matter");
            }

            if (string.Equals(key.Value, "<<", StringComparison.Ordinal))
            {
                return Malformed("merge keys are not allowed in the front matter");
            }

            EnvelopeRejection? rejection = ReadNode(events, events.Next(), out FrontMatterNode? value);
            if (rejection is not null)
            {
                return rejection;
            }

            if (!entries.TryAdd(key.Value, value!))
            {
                return Malformed($"duplicate key '{key.Value}' in the front matter");
            }
        }

        mapping = new FrontMatterMapping(entries);
        return null;
    }

    // The parser reports the two default handles (! and !!) on every document; only a %TAG line adds anything else.
    private static bool HasDeclaredTagDirective(DocumentStart document) =>
        document.Tags is { } tags
        && tags.Any(t => !(t.Handle == "!" && t.Prefix == "!") && !(t.Handle == "!!" && t.Prefix == "tag:yaml.org,2002:"));

    private static EnvelopeRejection Malformed(string detail) => new(EnvelopeRejectionReason.Malformed, null, detail);

    private sealed class EventCursor(Parser parser)
    {
        public ParsingEvent Next() =>
            parser.MoveNext() && parser.Current is { } current
                ? current
                : throw new YamlException("unexpected end of the event stream");

        public void Expect<T>()
            where T : ParsingEvent
        {
            ParsingEvent current = Next();
            if (current is not T)
            {
                throw new YamlException("expected " + typeof(T).Name + " but found " + current.GetType().Name);
            }
        }
    }
}
