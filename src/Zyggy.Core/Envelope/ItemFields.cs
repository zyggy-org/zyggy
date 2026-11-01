using Zyggy.Core.Memory;

namespace Zyggy.Core.Envelope;

/// <summary>The fields of an <c>item</c> envelope (founding spec §4 "Mandatory fields per type", row <c>item</c>, W41-6).</summary>
/// <param name="Project">The <c>project</c> the item is archived under (mandatory).</param>
/// <param name="Slug">The item's <c>slug</c>, unique in the project (mandatory).</param>
/// <param name="Name">The <c>name</c>: one line, 1–100 characters (mandatory).</param>
/// <param name="Description">The <c>description</c>: one line, 1–149 characters (mandatory).</param>
/// <param name="SourceName">The <c>source_name</c>: the file name the item came from, 1–100 characters, no control character (mandatory).</param>
/// <param name="Size">The <c>size</c> of the file in bytes, at least 1 (mandatory).</param>
/// <param name="Sha256">The <c>sha256</c> of the file: 64 lowercase hexadecimal characters (mandatory).</param>
/// <param name="Via">The route, <c>via</c> (mandatory).</param>
/// <param name="DriveName">The <c>drive_name</c>, <c>&lt;slug&gt;.&lt;extension&gt;</c>: present exactly when <see cref="Via"/> is <see cref="ItemRoute.OneDrive"/>.</param>
public sealed record ItemFields(
    Slug Project,
    Slug Slug,
    string Name,
    string Description,
    string SourceName,
    long Size,
    string Sha256,
    ItemRoute Via,
    string? DriveName);
