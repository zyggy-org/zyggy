using System.Text;

namespace Zyggy.Core.Memory;

/// <summary>A parsed <c>archive add</c>: project, one-line name and description, the absolute source path and the item's slug.</summary>
internal sealed record ArchiveAddRequest(Slug Project, string Name, string Description, string SourcePath, Slug Slug);

/// <summary>An archived item named as <c>&lt;project&gt;/&lt;slug&gt;</c>.</summary>
internal sealed record ArchiveRef(Slug Project, Slug Slug);

/// <summary>A parsed <c>archive list</c>: an optional project filter, only unindexed rows, JSON output.</summary>
internal sealed record ArchiveListRequest(Slug? Project, bool Unindexed, bool Json);

/// <summary>The argument checks of <c>zyggy memory archive add | list | remove</c> (spec 37 AC-4), each fault one usage message.</summary>
internal static class ArchiveArguments
{
    public const string UsageSuffixAdd =
        " (usage: zyggy memory archive add --project <slug> --name <text> --description <text> --file <absolute path> [--slug <slug>])";

    public const string UsageSuffixList = " (usage: zyggy memory archive list [--project <slug>] [--unindexed] [--json])";

    public const string UsageSuffixRemove = " (usage: zyggy memory archive remove <project>/<slug>)";

    private const int NameMax = 100;

    private const int DescriptionMax = 149;

    private const int SlugMax = 60;

    public static (ArchiveAddRequest? Request, string? Error) ParseAdd(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var option = args[i];
            if (option is not ("--project" or "--name" or "--description" or "--file" or "--slug"))
            {
                return (null, $"unexpected argument '{option}'");
            }

            if (i + 1 >= args.Count)
            {
                return (null, $"{option} needs a value");
            }

            if (!values.TryAdd(option, args[i + 1]))
            {
                return (null, $"{option} given twice");
            }

            i++;
        }

        foreach (var required in new[] { "--project", "--name", "--description", "--file" })
        {
            if (!values.ContainsKey(required))
            {
                return (null, $"{required} is required");
            }
        }

        if (!Slug.TryParse(values["--project"], out var project))
        {
            return (null, "--project is not a slug");
        }

        var name = TextCollapse.Line(values["--name"]);
        if (name.Length == 0)
        {
            return (null, "--name is empty");
        }

        if (TextCollapse.CharCount(name) > NameMax)
        {
            return (null, $"--name longer than {NameMax} characters");
        }

        var description = TextCollapse.Line(values["--description"]);
        if (description.Length == 0)
        {
            return (null, "--description is empty");
        }

        if (TextCollapse.CharCount(description) > DescriptionMax)
        {
            return (null, $"--description longer than {DescriptionMax} characters");
        }

        var file = values["--file"];
        if (!Path.IsPathFullyQualified(file))
        {
            return (null, "--file must be an absolute path");
        }

        Slug? slug;
        if (values.TryGetValue("--slug", out var given))
        {
            if (!Slug.TryParse(given, out slug))
            {
                return (null, "--slug is not a slug");
            }
        }
        else if (!Slug.TryParse(DeriveSlug(name), out slug))
        {
            return (null, "cannot derive a slug from --name; pass --slug");
        }

        return (new ArchiveAddRequest(project, name, description, file, slug), null);
    }

    public static (ArchiveRef? Reference, string? Error) ParseRemove(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 1)
        {
            return (null, args.Count == 0 ? "<project>/<slug> is required" : $"unexpected argument '{args[1]}'");
        }

        var parts = args[0].Split('/');
        if (parts.Length != 2 || !Slug.TryParse(parts[0], out var project) || !Slug.TryParse(parts[1], out var slug))
        {
            return (null, $"'{args[0]}' is not <project>/<slug>");
        }

        return (new ArchiveRef(project, slug), null);
    }

    public static (ArchiveListRequest? Request, string? Error) ParseList(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Slug? project = null;
        var unindexed = false;
        var json = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--project":
                    if (i + 1 >= args.Count)
                    {
                        return (null, "--project needs a value");
                    }

                    if (project is not null)
                    {
                        return (null, "--project given twice");
                    }

                    if (!Slug.TryParse(args[i + 1], out project))
                    {
                        return (null, "--project is not a slug");
                    }

                    i++;
                    break;
                case "--unindexed":
                    unindexed = true;
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    return (null, $"unexpected argument '{args[i]}'");
            }
        }

        return (new ArchiveListRequest(project, unindexed, json), null);
    }

    /// <summary>Lower-cased name, every run of characters outside <c>[a-z0-9]</c> → <c>-</c>, trimmed of <c>-</c>, cut to 60.</summary>
    public static string DeriveSlug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            var keep = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
            if (keep)
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > SlugMax)
        {
            slug = slug[..SlugMax].TrimEnd('-');
        }

        return slug;
    }
}
