// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>Shared arrangement: a simulated card and two local destinations.</summary>
public abstract class IngestInvariantTestBase : IDisposable
{
    protected IngestInvariantTestBase()
    {
        Card = Temp.Sub("card");
        Library = Temp.Sub("library");
        Backup = Temp.Sub("backup");
        CardFiles = SyntheticFiles.CreateCard(Card, nefCount: 40, jpegCount: 10);
    }

    protected TempFolder Temp { get; } = new("pf-inv");

    protected string Card { get; }

    protected string Library { get; }

    protected string Backup { get; }

    protected IReadOnlyList<string> CardFiles { get; }

    protected IReadOnlyList<IngestDestination> Destinations =>
    [
        new("library", Library, DestinationKind.Primary),
        new("backup", Backup, DestinationKind.Secondary),
    ];

    protected IngestRequest Request() =>
        new(CardFiles.Select(f => new IngestFile(f, Path.Combine("2026", Path.GetFileName(f)), new FileInfo(f).Length)).ToList(), Destinations, SourceRoot: Card);

    protected static async Task<(IngestSummary Summary, IReadOnlyCollection<VerifiedFile> Verified)> RunAsync(IFileSystem fileSystem, IngestRequest request)
    {
        var verified = new ConcurrentBag<VerifiedFile>();
        var service = new IngestService(new CopyEngine(fileSystem));
        var summary = await service.RunAsync(
            request,
            (file, _) =>
            {
                verified.Add(file);
                return ValueTask.CompletedTask;
            },
            progress: null,
            TestContext.Current.CancellationToken);
        return (summary, verified);
    }

    public void Dispose()
    {
        Temp.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>I1: no write or delete on the import source, ever.</summary>
[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class SourceIsNeverWrittenTests : IngestInvariantTestBase
{
    [Fact]
    public async Task Import_from_a_locked_card_makes_no_write_attempt_and_leaves_it_identical()
    {
        SyntheticFiles.MakeReadOnly(Card);
        var before = FolderSnapshot.Take(Card);
        var fileSystem = new RecordingFileSystem(new PhysicalFileSystem(), Card);

        var (summary, verified) = await RunAsync(fileSystem, Request());

        Assert.Empty(fileSystem.Violations);
        Assert.Equal(before, FolderSnapshot.Take(Card));
        Assert.Equal(CardFiles.Count, summary.Verified);
        Assert.Equal(CardFiles.Count, verified.Count);
    }

    [Fact]
    public async Task Errors_during_the_copy_never_touch_the_card()
    {
        var before = FolderSnapshot.Take(Card);
        var faults = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            CorruptWrites = p => p.Contains("DSC_0003", StringComparison.Ordinal),
            FailReads = p => p.Contains("DSC_0007", StringComparison.Ordinal),
        };
        var fileSystem = new RecordingFileSystem(faults, Card);

        await RunAsync(fileSystem, Request());

        Assert.Empty(fileSystem.Violations);
        Assert.Equal(before, FolderSnapshot.Take(Card));
    }

    [Fact]
    public async Task A_destination_inside_the_source_is_rejected_before_copying()
    {
        var before = FolderSnapshot.Take(Card);
        var request = Request() with { Destinations = [new("library", Path.Combine(Card, "copies"), DestinationKind.Primary)] };

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new PhysicalFileSystem(), request));
        Assert.Equal(before, FolderSnapshot.Take(Card));
    }
}

/// <summary>I3: no later stage runs on a file whose copy has not been verified.</summary>
[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class NoStageBeforeVerificationTests : IngestInvariantTestBase
{
    [Fact]
    public async Task A_corrupted_primary_copy_is_never_handed_to_the_next_stage()
    {
        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            // Every attempt on this file is corrupted, so the retry cannot succeed either.
            CorruptWrites = p => p.StartsWith(Library, StringComparison.OrdinalIgnoreCase) && p.Contains("DSC_0005", StringComparison.Ordinal),
        };

        var (summary, verified) = await RunAsync(fileSystem, Request());

        Assert.DoesNotContain(verified, v => v.File.SourcePath.Contains("DSC_0005", StringComparison.Ordinal));
        Assert.Contains(summary.Failed, f => f.File.SourcePath.Contains("DSC_0005", StringComparison.Ordinal));
        Assert.Equal(CardFiles.Count - 1, verified.Count);
        Assert.False(File.Exists(Path.Combine(Library, "2026", "DSC_0005.NEF")), "An unverified file must not appear under its final name.");
    }

    [Fact]
    public async Task A_read_error_on_the_card_stops_that_file_only()
    {
        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            FailReads = p => p.Contains("DSC_0002", StringComparison.Ordinal),
        };

        var (summary, verified) = await RunAsync(fileSystem, Request());

        Assert.DoesNotContain(verified, v => v.File.SourcePath.Contains("DSC_0002", StringComparison.Ordinal));
        Assert.Contains(summary.Failed, f => f.SourceError is not null && f.File.SourcePath.Contains("DSC_0002", StringComparison.Ordinal));
        Assert.Equal(CardFiles.Count - 1, verified.Count);
    }

    [Fact]
    public async Task Every_handed_over_file_matches_the_source_byte_for_byte()
    {
        var (_, verified) = await RunAsync(new PhysicalFileSystem(), Request());

        Assert.All(verified, v => Assert.Equal(FolderSnapshot.Sha256(v.File.SourcePath), FolderSnapshot.Sha256(v.PrimaryPath)));
    }

    [Fact]
    public async Task A_disconnected_primary_pauses_the_job_and_hands_over_nothing_unverified()
    {
        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            Unavailable = p => p.StartsWith(Library, StringComparison.OrdinalIgnoreCase),
        };

        var (summary, verified) = await RunAsync(fileSystem, Request());

        Assert.True(summary.PausedForUnavailableDestination);
        Assert.Empty(verified);
    }
}

/// <summary>I4: no file is ever deleted automatically.</summary>
[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class NothingIsDeletedTests : IngestInvariantTestBase
{
    [Fact]
    public async Task Repeated_imports_with_failures_only_ever_add_files()
    {
        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            CorruptWrites = p => p.Contains("DSC_0004", StringComparison.Ordinal),
        };
        var card = FolderSnapshot.Take(Card);

        await RunAsync(fileSystem, Request());
        var libraryAfterFirst = FolderSnapshot.Take(Library, IsFinalFile);
        var backupAfterFirst = FolderSnapshot.Take(Backup, IsFinalFile);

        fileSystem.CorruptWrites = _ => false;
        await RunAsync(fileSystem, Request());

        Assert.Equal(card, FolderSnapshot.Take(Card));
        AssertSuperset(libraryAfterFirst, FolderSnapshot.Take(Library, IsFinalFile));
        AssertSuperset(backupAfterFirst, FolderSnapshot.Take(Backup, IsFinalFile));
        Assert.Equal(CardFiles.Count, FolderSnapshot.Take(Library, IsFinalFile).Count);
    }

    [Fact]
    public async Task An_existing_different_file_at_the_target_name_is_never_overwritten()
    {
        var target = Directory.CreateDirectory(Path.Combine(Library, "2026")).FullName;
        var existing = Path.Combine(target, "DSC_0001.NEF");
        await File.WriteAllBytesAsync(existing, [1, 2, 3], TestContext.Current.CancellationToken);
        var before = FolderSnapshot.Sha256(existing);

        var (_, verified) = await RunAsync(new PhysicalFileSystem(), Request());

        Assert.Equal(before, FolderSnapshot.Sha256(existing));
        var copied = Assert.Single(verified, v => v.File.SourcePath.EndsWith("DSC_0001.NEF", StringComparison.Ordinal));
        Assert.NotEqual(existing, copied.PrimaryPath);
    }

    [Fact]
    public void The_file_system_abstraction_has_no_delete_operation()
    {
        var deleting = typeof(IFileSystem).GetMethods().Where(m => m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(deleting);
    }

    private static bool IsFinalFile(string path) => !path.EndsWith(".pftmp", StringComparison.OrdinalIgnoreCase);

    private static void AssertSuperset(IReadOnlyDictionary<string, string> earlier, IReadOnlyDictionary<string, string> later)
    {
        foreach (var (path, hash) in earlier)
        {
            Assert.True(later.TryGetValue(path, out var current), $"{path} disappeared");
            Assert.Equal(hash, current);
        }
    }
}
