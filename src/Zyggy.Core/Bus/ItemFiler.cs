using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// Central's side of an item (spec 41 decision 1, "one writer"): the signed fields and the bytes go to
/// <see cref="ArchiveService.AddBytesAsync"/> — the same closed checks, sidecar, index line and memory commit as <c>archive add</c>.
/// </summary>
internal sealed class ItemFiler(ArchiveService archive, ArchiveOptions options)
{
    /// <summary>Gets the archive options the filer applies (Central's <c>archive.json</c>).</summary>
    public ArchiveOptions Options { get; } = options;

    /// <summary>
    /// Files a verified <c>via: bus</c> item: a signed <c>size</c> over Central's <c>item_max_bytes</c> is refused <c>too_large</c> before
    /// the body is decoded (AC-34); otherwise the body is decoded and filed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the envelope is not a verified bus item (a caller bug).</exception>
    public Task<ArchiveOutcome> FileBusAsync(Model item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemFields fields = item.Item is { Via: ItemRoute.Bus } bus ? bus : throw new InvalidOperationException("not a bus item");
        if (fields.Size > Options.ItemMaxBytes)
        {
            return Task.FromResult(ArchiveOutcome.Refused(
                ArchiveRefusal.TooLarge, string.Create(CultureInfo.InvariantCulture, $"{fields.Size} > {Options.ItemMaxBytes}")));
        }

        return ItemBody.TryDecode(item, out byte[]? bytes)
            ? FileAsync(item, bytes, ct)
            : throw new InvalidOperationException("the bus item body does not decode");
    }

    /// <summary>Files <paramref name="bytes"/> under the item's signed project, slug, name, description and source name.</summary>
    public Task<ArchiveOutcome> FileAsync(Model item, byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(bytes);
        ItemFields fields = item.Item ?? throw new InvalidOperationException("not an item");
        return archive.AddBytesAsync(new ArchiveAddBytes(fields.Project, fields.Name, fields.Description, fields.Slug, fields.SourceName, bytes), ct);
    }
}
