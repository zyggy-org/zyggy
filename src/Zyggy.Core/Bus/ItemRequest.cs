using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>Why <c>zyggy archive</c> was not accepted before anything was read (spec 41 AC-9, AC-10): one usage line, exit 2.</summary>
/// <param name="Detail">The detail, in the words of <c>zyggy memory archive add</c>.</param>
public sealed record ItemRequestError(string Detail);

/// <summary>
/// What <c>zyggy archive</c> asks for (spec 41 CLI surface): the project, the one-line name and description, the absolute source path,
/// the item's slug (given or derived from the name as <c>memory archive add</c> derives it) and whether <c>--via bus</c> was given.
/// </summary>
public sealed class ItemRequest
{
    internal ItemRequest(Slug project, string name, string description, string sourcePath, Slug slug, bool viaBus)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        Slug = slug ?? throw new ArgumentNullException(nameof(slug));
        ViaBus = viaBus;
    }

    /// <summary>Gets the project the item is archived under.</summary>
    public Slug Project { get; }

    /// <summary>Gets the one-line name.</summary>
    public string Name { get; }

    /// <summary>Gets the one-line description.</summary>
    public string Description { get; }

    /// <summary>Gets the absolute path of the file to send.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the item's slug.</summary>
    public Slug Slug { get; }

    /// <summary>Gets a value indicating whether <c>--via bus</c> was given (the bus route even when a OneDrive folder is configured).</summary>
    public bool ViaBus { get; }

    /// <summary>
    /// Checks the options of <c>zyggy archive</c> with the words of <c>memory archive add</c> (spec 41 AC-9): the four required options,
    /// the project slug, name and description within their limits, an absolute <c>--file</c>, the slug given or derivable, and
    /// <c>--via</c> absent or <c>bus</c>. Nothing is read beyond the arguments.
    /// </summary>
    /// <param name="project">The <c>--project</c> value.</param>
    /// <param name="name">The <c>--name</c> value.</param>
    /// <param name="description">The <c>--description</c> value.</param>
    /// <param name="file">The <c>--file</c> value.</param>
    /// <param name="slug">The <c>--slug</c> value.</param>
    /// <param name="via">The <c>--via</c> value.</param>
    /// <param name="request">The request when accepted.</param>
    /// <returns><see langword="null"/> when accepted; otherwise the one usage line.</returns>
    public static ItemRequestError? TryCreate(string? project, string? name, string? description, string? file, string? slug, string? via, out ItemRequest? request)
    {
        request = null;
        var (add, error) = ArchiveArguments.Validate(project, name, description, file, slug);
        if (add is null)
        {
            return new ItemRequestError(error!);
        }

        if (via is not null && !string.Equals(via, "bus", StringComparison.Ordinal))
        {
            return new ItemRequestError("--via must be bus");
        }

        request = new ItemRequest(add.Project, add.Name, add.Description, add.SourcePath, add.Slug, via is not null);
        return null;
    }

    /// <summary>Central archives with <c>zyggy memory archive add</c>, never through the bus (spec 41 AC-10).</summary>
    /// <param name="machine">This node's machine.</param>
    /// <returns>The refusal on <c>central</c>; otherwise <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="machine"/> is null.</exception>
    public static ItemRequestError? CheckSender(MachineName machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine == MachineName.Central ? new ItemRequestError("this machine is central; use zyggy memory archive add") : null;
    }
}
