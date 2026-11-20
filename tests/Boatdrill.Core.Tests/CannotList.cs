using Boatdrill.Core.Adapters;
using Boatdrill.Core.Sampling;

namespace Boatdrill.Core.Tests;

/// <summary>
/// A remote that answers but whose files cannot be listed, as an rclone remote whose sign-in has expired. Given a
/// source's name, only that source's files cannot be listed, and every other source's files are all there.
/// </summary>
internal sealed class CannotList(string? failsFor = null) : IDestinationAdapter
{
    public const string Problem = "its files could not be listed: the remote said no";

    public DestinationKind Kind => DestinationKind.Rclone;

    public Task<DestinationProbe> ProbeAsync(Destination destination, CancellationToken ct) => Task.FromResult(new DestinationProbe(true, "answered", null));

    public Task<SourceBinding> BindAsync(Destination destination, Sample sample, string scratchDirectory, BindPurpose purpose,
        IReadOnlyCollection<string> alsoRead, CancellationToken ct)
    {
        var binding = new SourceBinding(destination, sample.Source, MirrorLayout.Auto, "remote:backup", 0, sample.Files.Count);
        return Task.FromResult(failsFor is null || failsFor == sample.Source.Name ? binding with { Problem = Problem, CouldNotLook = true } : binding);
    }

    public Task<CopyLookup> LocateAsync(SourceBinding binding, string relativePath, CancellationToken ct) => Task.FromResult(binding.CouldNotLook
        ? CopyLookup.Failed("remote:backup/" + relativePath, Problem)
        : new CopyLookup(CopyState.Found, "remote:backup/" + relativePath, 0, null, null));

    public Task<ReadBack> ReadBackAsync(SourceBinding binding, CopyLookup copy, string scratchPath, CancellationToken ct) =>
        throw new InvalidOperationException("Nothing is read back from a backup whose files could not be listed.");
}
