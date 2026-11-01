namespace Zyggy.Core.Envelope;

/// <summary>
/// The <c>via</c> field of an <c>item</c> envelope (founding spec §4, W41-6): how the item's bytes reach Central. Wire strings via
/// <see cref="EnvelopeWire"/>.
/// </summary>
public enum ItemRoute
{
    /// <summary><c>bus</c>: the file travels in the envelope body as 76-column base64.</summary>
    Bus,

    /// <summary><c>onedrive</c>: the file sits in the owner's OneDrive archive folder under <c>drive_name</c>; the body is empty.</summary>
    OneDrive,
}
