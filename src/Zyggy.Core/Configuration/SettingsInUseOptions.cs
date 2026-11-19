namespace Zyggy.Core.Configuration;

/// <summary>
/// How often a running node looks at its <c>node.json</c> (spec 55): <see cref="SettingsInUse.CheckPeriod"/>, unless a test overrides it.
/// </summary>
internal sealed class SettingsInUseOptions
{
    /// <summary>Gets or sets a period that replaces <see cref="SettingsInUse.CheckPeriod"/>. Tests only; production leaves it <see langword="null"/>.</summary>
    public TimeSpan? CheckPeriodOverride { get; set; }
}
