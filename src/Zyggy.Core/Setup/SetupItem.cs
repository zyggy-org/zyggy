namespace Zyggy.Core.Setup;

/// <summary>What <c>zyggy setup</c> takes care of, in the order of its output lines (spec 42 Contracts).</summary>
public enum SetupItem
{
    /// <summary><c>node.json</c>: <c>claude.path</c> and <c>signWith</c> completed backup-first.</summary>
    NodeJson,

    /// <summary>The <c>zyggy-core</c> checkout: cloned or fast-forwarded on <c>main</c>.</summary>
    ZyggyCore,

    /// <summary><c>env.ZYGGY_SECRET_PATTERNS</c> in the user-scope Claude Code settings.</summary>
    SettingsEnv,

    /// <summary>The three deny rules in <c>permissions.deny</c> of the user-scope Claude Code settings.</summary>
    SettingsDeny,

    /// <summary>The <c>remember</c> node skill.</summary>
    SkillRemember,

    /// <summary>The <c>archive</c> node skill.</summary>
    SkillArchive,

    /// <summary>The <c>bus</c> node skill (deliverable 15); left as is when the checkout does not hold it yet.</summary>
    SkillBus,

    /// <summary>The <c>discover</c> node skill (deliverable 15); left as is when the checkout does not hold it yet.</summary>
    SkillDiscover,

    /// <summary>The <c>signWith</c> key in the secret store (name and fingerprint only).</summary>
    SecretSigningKey,

    /// <summary>The <c>github-pat</c> in the secret store (name and fingerprint only).</summary>
    SecretGitHubPat,

    /// <summary>A spike file store left on the laptop; printed only when it exists.</summary>
    SpikeFileStore,
}
