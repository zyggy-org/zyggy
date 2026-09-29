using System.Buffers;
using System.Diagnostics;
using System.Text;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Zyggy.Core.Envelope;

/// <summary>
/// Emits a front-matter tree in canonical style (founding spec §4 "Canonical form, normative details"): keys in ordinal
/// order at every level, block mappings indented 2, flow sequences when every item is a scalar and block sequences
/// otherwise, every scalar from its parsed text with the plain → single-quoted → double-quoted ladder, <c>\n</c> line
/// endings, unlimited width, UTF-8 without BOM. The only YAML emitter in the solution; used for both signing and writing.
/// </summary>
internal static class FrontMatterEmitter
{
    private const string SignatureKey = "sig";

    // The characters YamlDotNet treats as line breaks: LF, CR, NEL, LS, PS.
    private static readonly SearchValues<char> LineBreaks = SearchValues.Create("\n\r\u0085\u2028\u2029");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static byte[] Emit(FrontMatterMapping mapping, bool includeSig)
    {
        var writer = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(writer, new EmitterSettings().WithNewLine("\n").WithIndentedSequences());
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));
        EmitMapping(emitter, mapping, skipSig: !includeSig);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());

        string text = writer.ToString();
        Debug.Assert(!text.StartsWith("---", StringComparison.Ordinal), "an implicit document never emits an opening delimiter");
        return Utf8NoBom.GetBytes(text);
    }

    private static void EmitNode(Emitter emitter, FrontMatterNode node)
    {
        switch (node)
        {
            case FrontMatterScalar scalar:
                EmitScalar(emitter, scalar.Value);
                break;
            case FrontMatterSequence sequence:
                SequenceStyle style = sequence.Items.All(i => i is FrontMatterScalar) ? SequenceStyle.Flow : SequenceStyle.Block;
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, style));
                foreach (FrontMatterNode item in sequence.Items)
                {
                    EmitNode(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                break;
            case FrontMatterMapping mapping:
                EmitMapping(emitter, mapping, skipSig: false);
                break;
            default:
                throw new UnreachableException("the front-matter hierarchy is closed");
        }
    }

    private static void EmitMapping(Emitter emitter, FrontMatterMapping mapping, bool skipSig)
    {
        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
        foreach (KeyValuePair<string, FrontMatterNode> entry in mapping.Entries)
        {
            if (skipSig && string.Equals(entry.Key, SignatureKey, StringComparison.Ordinal))
            {
                continue;
            }

            EmitScalar(emitter, entry.Key);
            EmitNode(emitter, entry.Value);
        }

        emitter.Emit(new MappingEnd());
    }

    // Requested style Plain; the emitter downgrades to single- then double-quoted when plain is not allowed (§4 (c)).
    // A value with a line break is requested double-quoted: the emitter would otherwise fold it across lines inside
    // single quotes, and §4 forbids line folding.
    private static void EmitScalar(Emitter emitter, string text)
    {
        ScalarStyle style = text.AsSpan().IndexOfAny(LineBreaks) >= 0 ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text, style, true, true));
    }
}
