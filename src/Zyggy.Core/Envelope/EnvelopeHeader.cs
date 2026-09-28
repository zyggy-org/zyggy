using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Envelope;

/// <summary>The fields common to every envelope type (founding spec §4 "Mandatory fields per type", row "all").</summary>
/// <param name="Tenant">The <c>tenant</c> field.</param>
/// <param name="Id">The <c>id</c> field, also the file name.</param>
/// <param name="From">The sending machine.</param>
/// <param name="To">The addressed machine.</param>
/// <param name="Created">The <c>created</c> instant, in UTC.</param>
/// <param name="Priority">The <c>priority</c> field; <see cref="EnvelopePriority.Normal"/> when absent.</param>
/// <param name="InReplyTo">The <c>in_reply_to</c> job id; mandatory on reports, otherwise optional.</param>
public sealed record EnvelopeHeader(
    TenantId Tenant,
    EnvelopeId Id,
    MachineName From,
    MachineName To,
    DateTimeOffset Created,
    EnvelopePriority Priority = EnvelopePriority.Normal,
    EnvelopeId? InReplyTo = null);
