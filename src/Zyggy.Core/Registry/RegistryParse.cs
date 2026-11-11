namespace Zyggy.Core.Registry;

/// <summary>
/// The result of <see cref="RegistryDocument.Parse"/>: a document, or a refusal naming the field (<c>schema</c>, <c>root</c>,
/// <c>tenant</c>, <c>projects[0].name</c>, …) and a short detail. Exactly one of <see cref="Document"/> and <see cref="Field"/> is set.
/// </summary>
/// <param name="Document">The parsed document, or <see langword="null"/> on a refusal.</param>
/// <param name="Field">The refused field, or <see langword="null"/> when parsed.</param>
/// <param name="Detail">A short reason, or <see langword="null"/> when parsed.</param>
public sealed record RegistryParse(RegistryDocument? Document, string? Field, string? Detail)
{
    /// <summary>Gets a value indicating whether the file parsed.</summary>
    public bool IsOk => Document is not null;

    internal static RegistryParse Ok(RegistryDocument document) => new(document, null, null);

    internal static RegistryParse Refused(string field, string detail) => new(null, field, detail);
}
