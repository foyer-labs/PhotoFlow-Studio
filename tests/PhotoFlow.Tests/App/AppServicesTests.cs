// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class AppServicesTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-app");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Settings_round_trip_and_fall_back_to_defaults()
    {
        var store = new SettingsStore(new PhysicalFileSystem(), new DefaultAppPaths(_temp.Path));
        Assert.Equal(new AppSettings().PrudentMode, (await store.LoadAsync(Ct)).PrudentMode);

        var settings = new AppSettings
        {
            SetupCompleted = true,
            Import = new ImportDefaults { PrimaryRoot = @"D:\Foto" }.WithRecentEvent("Matrimonio").WithRecentEvent("Coppa").WithRecentEvent("Matrimonio"),
        };
        await store.SaveAsync(settings, Ct);
        await store.SaveAsync(settings, Ct); // replacing an existing file
        var loaded = await store.LoadAsync(Ct);

        Assert.True(loaded.SetupCompleted);
        Assert.Equal(@"D:\Foto", loaded.Import.PrimaryRoot);
        Assert.Equal(["Matrimonio", "Coppa"], loaded.Import.RecentEvents);

        await File.WriteAllTextAsync(Path.Combine(new DefaultAppPaths(_temp.Path).ConfigDirectory, SettingsStore.FileName), "{ broken", Ct);
        Assert.False((await store.LoadAsync(Ct)).SetupCompleted);
    }

    [Fact]
    public async Task Jobs_run_in_background_report_progress_and_pause()
    {
        var runner = new GatedRunner();
        await using var manager = new JobManager(() => runner);
        var statuses = new List<JobStatus>();
        manager.StatusChanged += (_, s) => { lock (statuses) { statuses.Add(s); } };

        var run = manager.StartAsync(7);
        Assert.Same(run, manager.StartAsync(7)); // a running job is not started twice
        await runner.Started.Task.WaitAsync(Ct);
        Assert.True(manager.IsRunning(7));

        manager.Pause(7);
        var summary = await run.WaitAsync(Ct);

        Assert.Equal(JobState.Paused, summary.State);
        Assert.False(manager.IsRunning(7));
        lock (statuses)
        {
            Assert.Contains(statuses, s => s.State == JobState.Running && s.Progress.Copied == 1);
            Assert.Equal(JobState.Paused, statuses[^1].State);
        }
    }

    [Fact]
    public async Task Low_priority_work_waits_while_an_import_runs()
    {
        var runner = new GatedRunner();
        await using var manager = new JobManager(() => runner);
        Assert.False(manager.HasNormalPriorityRunning);
        await manager.YieldToNormalAsync(Ct); // nothing running: no wait

        var import = manager.StartAsync(1, JobPriority.Normal);
        await runner.Started.Task.WaitAsync(Ct);
        Assert.True(manager.HasNormalPriorityRunning);
        var waiting = manager.YieldToNormalAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);

        manager.Pause(1);
        await import.WaitAsync(Ct);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(manager.HasNormalPriorityRunning);
    }

    [Theory]
    [InlineData(100, 10, 0, 60, 540)]
    [InlineData(100, 2, 0, 60, null)]
    [InlineData(100, 100, 0, 60, 0)]
    public void Remaining_time_comes_from_the_throughput_of_the_run(int total, int completed, int atStart, int elapsedSeconds, int? expectedSeconds)
    {
        var estimate = JobManager.Estimate(new JobProgress(total, total, completed, 0, null), atStart, TimeSpan.FromSeconds(elapsedSeconds));
        Assert.Equal(expectedSeconds, estimate is { } e ? (int)e.TotalSeconds : null);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class GatedRunner : IJobRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JobRunSummary> RunAsync(long jobId, IProgress<JobProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new JobProgress(10, 1, 0, 0, "a.NEF"));
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                return new JobRunSummary(JobState.Paused, 0, 0, JobPauseReason.UserRequest);
            }

            return new JobRunSummary(JobState.Completed, 10, 0, null);
        }
    }
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class PhotoFlowHostTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-host");

    [Fact]
    public async Task The_host_starts_on_an_empty_profile_and_detects_no_models()
    {
        var paths = new DefaultAppPaths(_temp.Path);
        await using var host = await PhotoFlowHost.StartAsync(paths, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(paths.CatalogPath));
        Assert.Empty(host.InterruptedJobs);
        Assert.False(host.Settings.SetupCompleted);
        var setup = host.DescribeAnalysis();
        Assert.Null(setup.ObjectDetector);
        Assert.Null(setup.FaceDetector);
        Assert.False(setup.EyesAvailable);

        // A second window cannot open the same catalog.
        await Assert.ThrowsAsync<CatalogLockedException>(() => PhotoFlowHost.StartAsync(paths, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _temp.Dispose();
}
