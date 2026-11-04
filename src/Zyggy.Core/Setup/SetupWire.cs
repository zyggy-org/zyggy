using Zyggy.Core.Envelope;

namespace Zyggy.Core.Setup;

/// <summary>The words of a <see cref="SetupLine"/> (plan 42 A1): the one place they live.</summary>
internal static class SetupWire
{
    public static string ItemWords(SetupItem item, KeyId? signWith) => item switch
    {
        SetupItem.NodeJson => "node.json",
        SetupItem.ZyggyCore => "zyggy-core",
        SetupItem.SettingsEnv or SetupItem.SettingsDeny => "settings",
        SetupItem.SkillRemember => "skill remember",
        SetupItem.SkillArchive => "skill archive",
        SetupItem.SecretSigningKey => signWith is null ? "signing key" : "signing key " + signWith,
        SetupItem.SecretGitHubPat => "github-pat",
        SetupItem.SpikeFileStore => "spike file store",
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, null),
    };

    public static string OutcomeWords(SetupOutcome outcome) => outcome switch
    {
        SetupOutcome.Done => "done",
        SetupOutcome.AlreadyDone => "already done",
        SetupOutcome.LeftAsIs => "left as is",
        SetupOutcome.Missing => "missing",
        SetupOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}
