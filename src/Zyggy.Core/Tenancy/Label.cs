namespace Zyggy.Core.Tenancy;

/// <summary>
/// The one validator for the label syntax shared by tenants, users and machines (founding spec §4 "Mandatory fields per type"):
/// lowercase ASCII letters, digits and inner hyphens, 1–63 characters, no leading or trailing hyphen.
/// </summary>
internal static class Label
{
    internal const int MaxLength = 63;

    internal const string SyntaxDescription =
        "a label of 1-63 lowercase ASCII letters, digits and inner hyphens ([a-z0-9-], no leading or trailing hyphen)";

    internal static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > MaxLength || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!IsLabelChar(c))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsLabelChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
}
