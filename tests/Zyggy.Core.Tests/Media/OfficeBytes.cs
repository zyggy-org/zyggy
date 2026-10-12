using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Zyggy.Core.Tests.Media;

/// <summary>One entry of a hand-written ZIP: stored unless <see cref="Deflated"/> is given; sizes and flags may be forged.</summary>
internal sealed record ZipEntrySpec(string Name, byte[] Body)
{
    /// <summary>When set, the entry is written as method 8 with exactly these compressed bytes.</summary>
    public byte[]? Deflated { get; init; }

    /// <summary>When set, the uncompressed size written in both headers instead of the body's length.</summary>
    public uint? DeclaredSize { get; init; }

    /// <summary>Sets general-purpose flag bit 0 (traditional encryption) without encrypting anything.</summary>
    public bool Encrypted { get; init; }
}

/// <summary>
/// Hand-built Office, OpenDocument, OLE and hostile ZIP samples for the sniffer tests (plan 39 Shared rules): a tiny stored-method ZIP
/// writer (local headers, central directory, CRC-32 table; no package, no <c>ZipArchive</c>) so a test can forge what the framework
/// cannot write — a declared size past 1 GiB, duplicate or unsafe names, an encrypted flag, a part that inflates past 4 MiB.
/// Every package is metadata-free: no <c>docProps</c>, no author, placeholder text only.
/// </summary>
internal static class OfficeBytes
{
    public const string DocxMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    public const string XlsxMain = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string PptxMain = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    public const string RelsType = "application/vnd.openxmlformats-package.relationships+xml";

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static readonly byte[] Document = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Sample</w:t></w:r></w:p></w:body></w:document>");

    private static readonly byte[] Workbook = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets/></workbook>");

    private static readonly byte[] Presentation = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:sldIdLst/></p:presentation>");

    // ---- raw ZIP ----

    public static byte[] Zip(params ZipEntrySpec[] entries) => Zip((IEnumerable<ZipEntrySpec>)entries);

    public static byte[] Zip(IEnumerable<ZipEntrySpec> entries)
    {
        using var file = new MemoryStream();
        using var central = new MemoryStream();
        var count = 0;
        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var data = entry.Deflated ?? entry.Body;
            var method = (ushort)(entry.Deflated is null ? 0 : 8);
            var flags = (ushort)(0x800 | (entry.Encrypted ? 1 : 0));
            var crc = Crc32(entry.Body);
            var uncompressed = entry.DeclaredSize ?? (uint)entry.Body.Length;
            var offset = (uint)file.Position;

            Write32(file, 0x04034B50);
            Write16(file, 20);
            Write16(file, flags);
            Write16(file, method);
            Write16(file, 0);
            Write16(file, 0x0021);
            Write32(file, crc);
            Write32(file, (uint)data.Length);
            Write32(file, uncompressed);
            Write16(file, (ushort)name.Length);
            Write16(file, 0);
            file.Write(name);
            file.Write(data);

            Write32(central, 0x02014B50);
            Write16(central, 20);
            Write16(central, 20);
            Write16(central, flags);
            Write16(central, method);
            Write16(central, 0);
            Write16(central, 0x0021);
            Write32(central, crc);
            Write32(central, (uint)data.Length);
            Write32(central, uncompressed);
            Write16(central, (ushort)name.Length);
            Write16(central, 0);
            Write16(central, 0);
            Write16(central, 0);
            Write16(central, 0);
            Write32(central, 0);
            Write32(central, offset);
            central.Write(name);
            count++;
        }

        var centralOffset = (uint)file.Position;
        central.WriteTo(file);
        Write32(file, 0x06054B50);
        Write16(file, 0);
        Write16(file, 0);
        Write16(file, (ushort)count);
        Write16(file, (ushort)count);
        Write32(file, (uint)central.Length);
        Write32(file, centralOffset);
        Write16(file, 0);
        return file.ToArray();
    }

    public static ZipEntrySpec Stored(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    public static ZipEntrySpec Stored(string name, byte[] body) => new(name, body);

    /// <summary>An entry written with method 8; <paramref name="body"/> is deflated by the framework (highly repetitive bodies shrink to a few KB).</summary>
    public static ZipEntrySpec Deflated(string name, byte[] body) => new(name, body) { Deflated = Deflate(body) };

    public static byte[] Deflate(byte[] body)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(body);
        }

        return output.ToArray();
    }

    /// <summary><paramref name="count"/> spaces: a body that inflates far beyond its deflated size.</summary>
    public static byte[] Spaces(int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, (byte)' ');
        return bytes;
    }

    // ---- content types ----

    /// <summary>A <c>[Content_Types].xml</c> with the usual <c>rels</c>/<c>xml</c> defaults (plus <paramref name="defaults"/>), the overrides and any raw extra XML.</summary>
    public static string ContentTypes(IEnumerable<(string Part, string Type)> overrides, IEnumerable<(string Extension, string Type)>? defaults = null, string? extra = null)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        builder.Append("<Default Extension=\"rels\" ContentType=\"").Append(RelsType).Append("\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        foreach (var (extension, type) in defaults ?? [])
        {
            builder.Append("<Default Extension=\"").Append(extension).Append("\" ContentType=\"").Append(type).Append("\"/>");
        }

        foreach (var (part, type) in overrides)
        {
            builder.Append("<Override PartName=\"").Append(part).Append("\" ContentType=\"").Append(type).Append("\"/>");
        }

        return builder.Append(extra).Append("</Types>").ToString();
    }

    public static string Rels(string target) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{target}\"/></Relationships>";

    // ---- packages ----

    /// <summary>A ZIP whose first entry is the given <c>[Content_Types].xml</c>, followed by the parts.</summary>
    public static byte[] Package(string contentTypesXml, params ZipEntrySpec[] parts) =>
        Zip([Stored("[Content_Types].xml", contentTypesXml), .. parts]);

    /// <summary>A minimal Word document: the main override, <c>_rels/.rels</c>, <c>word/document.xml</c>, then the extra parts (not declared unless <paramref name="extraContentTypes"/> says so).</summary>
    public static byte[] Docx(params ZipEntrySpec[] extraParts) => DocxWith(null, extraParts);

    public static byte[] DocxWith(string? extraContentTypes, params ZipEntrySpec[] extraParts) =>
        Office(DocxMain, "/word/document.xml", Document, extraContentTypes, extraParts);

    public static byte[] Xlsx(params ZipEntrySpec[] extraParts) => XlsxWith(null, extraParts);

    public static byte[] XlsxWith(string? extraContentTypes, params ZipEntrySpec[] extraParts) =>
        Office(XlsxMain, "/xl/workbook.xml", Workbook, extraContentTypes, extraParts);

    public static byte[] Pptx(params ZipEntrySpec[] extraParts) => PptxWith(null, extraParts);

    public static byte[] PptxWith(string? extraContentTypes, params ZipEntrySpec[] extraParts) =>
        Office(PptxMain, "/ppt/presentation.xml", Presentation, extraContentTypes, extraParts);

    /// <summary>A Word-shaped package whose main part is declared with <paramref name="mainType"/> (macro-enabled, template, …).</summary>
    public static byte[] WithMainType(string mainType, string partName = "/word/document.xml", params ZipEntrySpec[] extraParts) =>
        Office(mainType, partName, Document, null, extraParts);

    /// <summary>The Word parts under an arbitrary <c>[Content_Types].xml</c>.</summary>
    public static byte[] WithContentTypes(string xml) =>
        Package(xml, Stored("_rels/.rels", Rels("word/document.xml")), Stored("word/document.xml", Document));

    /// <summary>An OpenDocument package: <c>mimetype</c> first, stored, then a placeholder <c>content.xml</c>.</summary>
    public static byte[] Odf(string mimetype) =>
        Zip(Stored("mimetype", mimetype), Stored("content.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>"));

    /// <summary>
    /// An OLE compound file: the 512-byte header (sector shift 9, first directory sector 0) and one directory sector holding
    /// <c>Root Entry</c> and, when asked, an <c>EncryptedPackage</c> stream entry.
    /// </summary>
    public static byte[] Cfb(bool encryptedPackage)
    {
        var bytes = new byte[1024];
        CfbHeader(bytes, 9, 0);
        DirectoryEntry(bytes.AsSpan(512, 128), "Root Entry", 5);
        if (encryptedPackage)
        {
            DirectoryEntry(bytes.AsSpan(640, 128), "EncryptedPackage", 2);
        }

        return bytes;
    }

    /// <summary>An OLE header only, no directory sector behind it.</summary>
    public static byte[] CfbHeaderOnly(int sectorShift = 9)
    {
        var bytes = new byte[512];
        CfbHeader(bytes, sectorShift, 0);
        return bytes;
    }

    private static void CfbHeader(Span<byte> bytes, int sectorShift, uint firstDirectorySector)
    {
        ((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]).CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x18..], 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x1A..], 0x0003);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x1C..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x1E..], (ushort)sectorShift);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x20..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x2C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x30..], firstDirectorySector);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x38..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x3C..], 0xFFFFFFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x44..], 0xFFFFFFFE);
        bytes[0x4C..0x200].Fill(0xFF);
    }

    private static void DirectoryEntry(Span<byte> entry, string name, byte type)
    {
        var utf16 = Encoding.Unicode.GetBytes(name);
        utf16.CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)(utf16.Length + 2));
        entry[0x42] = type;
        entry[0x43] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], 0xFFFFFFFF);
    }

    private static byte[] Office(string mainType, string partName, byte[] mainPart, string? extraContentTypes, ZipEntrySpec[] extraParts)
    {
        var xml = ContentTypes([(partName, mainType)], null, extraContentTypes);
        return Package(xml, [Stored("_rels/.rels", Rels(partName[1..])), Stored(partName[1..], mainPart), .. extraParts]);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static void Write16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void Write32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
