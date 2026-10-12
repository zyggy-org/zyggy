using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Zyggy.Core.Media;

/// <summary>The Office Open XML documents the archive keeps (spec 39 Contracts "Accepted main-part content types").</summary>
internal enum OfficeKind
{
    /// <summary>A Word document (<c>wordprocessingml.document.main+xml</c>).</summary>
    Docx,

    /// <summary>An Excel workbook (<c>spreadsheetml.sheet.main+xml</c>).</summary>
    Xlsx,

    /// <summary>A PowerPoint presentation (<c>presentationml.presentation.main+xml</c>).</summary>
    Pptx,
}

/// <summary>Why an Office-shaped file is refused (spec 39 Contracts "<c>office_refused</c> details", closed).</summary>
internal enum OfficeRefusalDetail
{
    /// <summary>A content type anywhere in <c>[Content_Types].xml</c> contains <c>macroEnabled</c>.</summary>
    MacroEnabled,

    /// <summary>A VBA, ActiveX, macro-sheet or key-map part by content type or entry name.</summary>
    ActiveContent,

    /// <summary>An OLE compound file (.doc/.xls/.ppt).</summary>
    LegacyBinary,

    /// <summary>An OLE compound file whose directory names an <c>EncryptedPackage</c> stream (a password-protected OOXML file).</summary>
    Encrypted,

    /// <summary>An OpenDocument package (<c>mimetype</c> first entry).</summary>
    OpenDocument,

    /// <summary>A template or slideshow main type (dotx, xltx, potx, ppsx).</summary>
    OfficeVariant,

    /// <summary>No <c>[Content_Types].xml</c>, zero or several accepted main types, or the main part is missing.</summary>
    NotOffice,

    /// <summary>The archive cannot be opened, an entry is encrypted, names are duplicate or unsafe, or <c>[Content_Types].xml</c> is not well-formed XML or has a DTD.</summary>
    ZipMalformed,

    /// <summary>Too many entries, too many declared bytes, or an XML part inflating past the cap.</summary>
    ZipBounds,
}

/// <summary>The single mapping between <see cref="OfficeRefusalDetail"/> and its snake_case word in <c>office_refused (&lt;detail&gt;)</c>.</summary>
internal static class OfficeRefusalDetailWire
{
    public static string ToWire(OfficeRefusalDetail detail) => detail switch
    {
        OfficeRefusalDetail.MacroEnabled => "macro_enabled",
        OfficeRefusalDetail.ActiveContent => "active_content",
        OfficeRefusalDetail.LegacyBinary => "legacy_binary",
        OfficeRefusalDetail.Encrypted => "encrypted",
        OfficeRefusalDetail.OpenDocument => "opendocument",
        OfficeRefusalDetail.OfficeVariant => "office_variant",
        OfficeRefusalDetail.NotOffice => "not_office",
        OfficeRefusalDetail.ZipMalformed => "zip_malformed",
        OfficeRefusalDetail.ZipBounds => "zip_bounds",
        _ => throw new ArgumentOutOfRangeException(nameof(detail)),
    };
}

/// <summary>The sniffer's verdict: exactly one of an accepted kind or a refusal detail.</summary>
internal readonly record struct OfficeSniff(OfficeKind? Kind, OfficeRefusalDetail? Detail)
{
    public static OfficeSniff Accept(OfficeKind kind) => new(kind, null);

    public static OfficeSniff Refuse(OfficeRefusalDetail detail) => new(null, detail);
}

/// <summary>
/// Tells a Word, Excel or PowerPoint document from every other ZIP or OLE compound file by its content alone (spec 39 AC-5..AC-9):
/// <c>[Content_Types].xml</c> and the entry names, never the file name. Pure over bytes; extracts nothing to disk; decompresses at most
/// two small entries (<c>mimetype</c>, <c>[Content_Types].xml</c>) through a counting read; prohibits DTDs; bounds the entry count and the
/// declared sizes. <c>.rels</c> parts are never read (owner decision OQ-2 (b)).
/// </summary>
internal static class OfficeSniffer
{
    /// <summary>The most entries a package may hold.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>The most bytes the entries may declare together (1 GiB).</summary>
    public const long MaxDeclaredBytes = 1_073_741_824;

    /// <summary>The most actual bytes an XML part the sniffer reads may inflate to (4 MiB).</summary>
    public const int MaxXmlPartBytes = 4_194_304;

    private const string ContentTypesEntry = "[Content_Types].xml";
    private const string OdfMimetypePrefix = "application/vnd.oasis.opendocument.";

    private static readonly string[] ActiveContentTypes =
    [
        "application/vnd.ms-office.vbaProject",
        "application/vnd.ms-office.vbaProjectSignature",
        "application/vnd.ms-office.vbaProjectSignatureAgile",
        "application/vnd.ms-office.vbaProjectSignatureV3",
        "application/vnd.ms-word.vbaData+xml",
        "application/vnd.ms-office.activeX+xml",
        "application/vnd.ms-office.activeX",
        "application/vnd.ms-excel.macrosheet+xml",
        "application/vnd.ms-excel.intlmacrosheet+xml",
        "application/vnd.ms-word.keyMapCustomizations+xml",
        "application/vnd.ms-word.attachedToolbars",
    ];

    private static readonly string[] ActiveContentLastSegments = ["vbaProject.bin", "vbaData.xml"];

    private static readonly string[] ActiveContentSegments = ["activeX", "macrosheets"];

    private static readonly (string ContentType, OfficeKind Kind)[] AcceptedMainTypes =
    [
        ("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", OfficeKind.Docx),
        ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", OfficeKind.Xlsx),
        ("application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml", OfficeKind.Pptx),
    ];

    private static readonly string[] VariantMainTypes =
    [
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.template.main+xml",
        "application/vnd.openxmlformats-officedocument.presentationml.template.main+xml",
        "application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml",
    ];

    /// <summary>Tells whether the bytes start with a ZIP local file header (<c>PK\x03\x04</c> at offset 0); junk before it is not a ZIP.</summary>
    public static bool IsZip(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]);

    /// <summary>Tells whether the bytes start with the OLE compound-file signature.</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

    /// <summary>Judges a ZIP in the spec's order: open, bounds and names; OpenDocument; <c>[Content_Types].xml</c>; macros; active content; main type.</summary>
    public static OfficeSniff SniffZip(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var entries = archive.Entries;
            if (entries.Count > MaxEntries)
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.ZipBounds);
            }

            long declared = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                declared += entry.Length;
                if (declared > MaxDeclaredBytes)
                {
                    return OfficeSniff.Refuse(OfficeRefusalDetail.ZipBounds);
                }

                if (entry.IsEncrypted || !IsSafeName(entry.FullName) || !names.Add(entry.FullName))
                {
                    return OfficeSniff.Refuse(OfficeRefusalDetail.ZipMalformed);
                }
            }

            if (entries.Count > 0 && string.Equals(entries[0].FullName, "mimetype", StringComparison.Ordinal)
                && Encoding.ASCII.GetString(ReadBounded(entries[0])).StartsWith(OdfMimetypePrefix, StringComparison.Ordinal))
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.OpenDocument);
            }

            var contentTypes = entries.FirstOrDefault(e => string.Equals(e.FullName, ContentTypesEntry, StringComparison.Ordinal));
            if (contentTypes is null)
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.NotOffice);
            }

            var (defaults, overrides) = ReadContentTypes(contentTypes);
            var allTypes = defaults.Concat(overrides.Select(o => o.ContentType)).ToList();
            if (allTypes.Any(t => t.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)))
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.MacroEnabled);
            }

            if (allTypes.Any(t => ActiveContentTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) || names.Any(IsActiveContentName))
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.ActiveContent);
            }

            if (overrides.Any(o => VariantMainTypes.Contains(o.ContentType, StringComparer.OrdinalIgnoreCase)))
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.OfficeVariant);
            }

            var mains = overrides
                .Select(o => (o.PartName, Kind: AcceptedMainTypes.FirstOrDefault(a => string.Equals(a.ContentType, o.ContentType, StringComparison.OrdinalIgnoreCase)).Kind,
                    Accepted: AcceptedMainTypes.Any(a => string.Equals(a.ContentType, o.ContentType, StringComparison.OrdinalIgnoreCase))))
                .Where(m => m.Accepted)
                .ToList();
            if (mains.Count != 1 || !names.Contains(mains[0].PartName.TrimStart('/')))
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.NotOffice);
            }

            return OfficeSniff.Accept(mains[0].Kind);
        }
        catch (BoundsExceededException)
        {
            return OfficeSniff.Refuse(OfficeRefusalDetail.ZipBounds);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or NotSupportedException)
        {
            return OfficeSniff.Refuse(OfficeRefusalDetail.ZipMalformed);
        }
    }

    /// <summary>
    /// Judges an OLE compound file from its header and first directory sector only (plan 39 assumption A4): an <c>EncryptedPackage</c>
    /// entry there is a password-protected OOXML file; anything else is an older binary format.
    /// </summary>
    public static OfficeSniff SniffCompoundFile(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 512)
        {
            return OfficeSniff.Refuse(OfficeRefusalDetail.LegacyBinary);
        }

        int shift = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x1E..]);
        if (shift is not (9 or 12))
        {
            return OfficeSniff.Refuse(OfficeRefusalDetail.LegacyBinary);
        }

        long sector = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x30..]);
        var size = 1L << shift;
        var offset = (sector + 1) << shift;
        if (sector >= 0xFFFFFFFA || offset + size > bytes.Length)
        {
            return OfficeSniff.Refuse(OfficeRefusalDetail.LegacyBinary);
        }

        var directory = bytes.Slice((int)offset, (int)size);
        for (var at = 0; at + 128 <= directory.Length; at += 128)
        {
            var entry = directory.Slice(at, 128);
            int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]);
            if (nameBytes is >= 2 and <= 64 && Encoding.Unicode.GetString(entry[..(nameBytes - 2)]) == "EncryptedPackage")
            {
                return OfficeSniff.Refuse(OfficeRefusalDetail.Encrypted);
            }
        }

        return OfficeSniff.Refuse(OfficeRefusalDetail.LegacyBinary);
    }

    // Empty, rooted, backslash, a `.`/`..` segment, an empty inner segment, a drive prefix or a control character.
    private static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name[0] == '/' || name.Contains('\\', StringComparison.Ordinal)
            || (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
            || name.Any(c => c < 0x20 || c == 0x7F))
        {
            return false;
        }

        var segments = name.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment is "." or ".." || (segment.Length == 0 && i < segments.Length - 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsActiveContentName(string name)
    {
        var segments = name.Split('/');
        return ActiveContentLastSegments.Contains(segments[^1], StringComparer.OrdinalIgnoreCase)
            || segments.Any(s => ActiveContentSegments.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    private static (List<string> Defaults, List<(string PartName, string ContentType)> Overrides) ReadContentTypes(ZipArchiveEntry entry)
    {
        var defaults = new List<string>();
        var overrides = new List<(string, string)>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            MaxCharactersInDocument = MaxXmlPartBytes,
            CloseInput = true,
        };
        using var reader = XmlReader.Create(new CountingStream(entry.Open()), settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "Default" && reader.GetAttribute("ContentType") is { } defaultType)
            {
                defaults.Add(defaultType);
            }
            else if (reader.LocalName == "Override" && reader.GetAttribute("ContentType") is { } overrideType)
            {
                overrides.Add((reader.GetAttribute("PartName") ?? string.Empty, overrideType));
            }
        }

        return (defaults, overrides);
    }

    // The whole entry through the counting read; the caller looks only at its prefix.
    private static byte[] ReadBounded(ZipArchiveEntry entry)
    {
        using var stream = new CountingStream(entry.Open());
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class BoundsExceededException : Exception
    {
    }

    // A read-only wrapper that throws once more than MaxXmlPartBytes actual bytes have come out of the entry.
    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            return _read > MaxXmlPartBytes ? throw new BoundsExceededException() : read;
        }
    }
}
