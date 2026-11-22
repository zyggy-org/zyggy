using Zyggy.Core.Outlook;

namespace Zyggy.Core.Setup;

/// <summary>
/// The <see cref="SetupItem.WorkBoundary"/> item (spec 58 D-Q7): when the installer is given the instance checkout on the laptop, its
/// <c>work-boundary.json</c> is copied to the node directory, where the outlook tools read the policy record. Without the option
/// nothing is printed; the instance record stays the evidence.
/// </summary>
internal static class WorkBoundaryCopy
{
    /// <summary>Copies the record when <paramref name="instancePath"/> is given.</summary>
    /// <param name="instancePath">The instance checkout on the laptop, or <see langword="null"/> when the option was not given.</param>
    /// <param name="nodeDir">The node directory of the completed <c>node.json</c>.</param>
    /// <returns>The line, or <see langword="null"/> when the option was not given.</returns>
    public static SetupLine? Apply(string? instancePath, string nodeDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        if (instancePath is null)
        {
            return null;
        }

        string source = Path.Combine(Path.GetFullPath(instancePath), OutlookPolicy.WorkBoundaryFileName);
        string target = OutlookPolicy.CopyPath(nodeDir);
        try
        {
            if (!File.Exists(source))
            {
                return new SetupLine(SetupItem.WorkBoundary, SetupOutcome.LeftAsIs, "no " + OutlookPolicy.WorkBoundaryFileName + " in " + Path.GetFullPath(instancePath));
            }

            byte[] bytes = File.ReadAllBytes(source);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                return new SetupLine(SetupItem.WorkBoundary, SetupOutcome.AlreadyDone, null);
            }

            Directory.CreateDirectory(nodeDir);
            File.WriteAllBytes(target, bytes);
            return new SetupLine(SetupItem.WorkBoundary, SetupOutcome.Done, "copied from " + source + " to " + target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupLine(SetupItem.WorkBoundary, SetupOutcome.Failed, ex.Message);
        }
    }
}
