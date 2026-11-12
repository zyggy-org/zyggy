using System.Text;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Registry;

/// <summary>
/// The registry name rule (founding spec §4 "Registry file" as amended by A14-1): a project's name is its folder name lowercased
/// with every run of other characters replaced by one hyphen, trimmed of hyphens and cut to the label length.
/// </summary>
internal static class ProjectName
{
    /// <summary>Derives the routable label of a folder; empty when nothing usable remains.</summary>
    internal static string Derive(string folderName)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        var builder = new StringBuilder(folderName.Length);
        bool pendingHyphen = false;
        foreach (char raw in folderName)
        {
            char c = char.ToLowerInvariant(raw);
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingHyphen = false;
                builder.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        string name = builder.ToString();
        if (name.Length > Label.MaxLength)
        {
            name = name[..Label.MaxLength].TrimEnd('-');
        }

        return name;
    }

    /// <summary>Whether a text is a routable label.</summary>
    internal static bool IsLabel(string text) => Label.IsValid(text);
}
