// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.App.ViewModels;

/// <summary>A stage bar of the selected job (board 3g): stages of later phases show "—".</summary>
public sealed partial class StageViewModel(string labelKey, bool active) : ObservableObject
{
    /// <summary>The first stage reads "Copy" for an import and "Read" for an archive analysed in place.</summary>
    [ObservableProperty]
    private string _label = Strings.Get(labelKey);

    public bool IsActive { get; } = active;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _count = "—";
}

public sealed record JobErrorViewModel(string File, string Message);

/// <summary>A job of the list on the left: running, queued or completed.</summary>
public sealed partial class JobListItemViewModel(Job job) : ObservableObject
{
    public Job Job { get; } = job;

    public string Name => string.IsNullOrWhiteSpace(Job.EventName) ? Strings.Get("Job.Unnamed") : Job.EventName;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _remaining = string.Empty;

    [ObservableProperty]
    private double _progress;
}

/// <summary>
/// Jobs (boards 3g-3i): running and completed jobs, stages with counts, estimate, pause/resume, per-file errors
/// (the job goes on), pause for an unreachable destination or card.
/// </summary>
public sealed partial class JobsViewModel : PageViewModel, IDisposable
{
    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;

    public JobsViewModel(PhotoFlowHost host, IShellNavigator navigator)
    {
        _host = host;
        _navigator = navigator;
        _host.Jobs.StatusChanged += OnStatus;
        Stages =
        [
            new("Stage.Copy", true), new("Stage.Preview", true), new("Stage.Analysis", true), new("Stage.Grouping", false),
            new("Stage.Decision", true), new("Stage.Tags", false), new("Stage.Faces", false), new("Stage.Review", false), new("Stage.Xmp", false),
        ];
    }

    public override string Title => Strings.Get("Nav.Jobs");

    public const string TogglePauseCommandId = "Jobs.TogglePause";

    private static readonly CommandTable Table = new(
    [
        new(TogglePauseCommandId, new KeyGesture(Key.Space), "Jobs.Command.PauseResume", StatusGroupKey: "Jobs.Command.PauseResume"),
    ]);

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id == TogglePauseCommandId ? TogglePauseCommand : base.ResolveCommand(id);

    public ObservableCollection<JobListItemViewModel> Running { get; } = [];

    public ObservableCollection<JobListItemViewModel> Queued { get; } = [];

    public ObservableCollection<JobListItemViewModel> Completed { get; } = [];

    public IReadOnlyList<StageViewModel> Stages { get; }

    public ObservableCollection<JobErrorViewModel> Errors { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsEmpty), nameof(SelectedName), nameof(CanPause), nameof(CanResume))]
    private JobListItemViewModel? _selected;

    [ObservableProperty]
    private string _selectedDetail = string.Empty;

    [ObservableProperty]
    private string _remaining = string.Empty;

    [ObservableProperty]
    private string _elapsed = string.Empty;

    [ObservableProperty]
    private string? _pauseMessage;

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Running.Count == 0 && Queued.Count == 0 && Completed.Count == 0;

    public string SelectedName => Selected?.Name ?? string.Empty;

    public bool CanPause => Selected is not null && _host.Jobs.IsRunning(Selected.Job.Id);

    public bool CanResume => Selected is not null && !_host.Jobs.IsRunning(Selected.Job.Id)
                                                  && (Selected.Job.State is JobState.Paused or JobState.Pending || Errors.Count > 0);

    public string ErrorsHeader => Strings.Format("Jobs.Errors", Errors.Count);

    public override async Task ActivateAsync()
    {
        // Read first, then replace the lists without awaiting in between: refreshes raised by job events may overlap.
        var recent = await _host.Catalog.Jobs.ListRecentAsync(30, CancellationToken.None);
        var selectedId = Selected?.Job.Id;
        Running.Clear();
        Queued.Clear();
        Completed.Clear();
        foreach (var job in recent)
        {
            var running = _host.Jobs.IsRunning(job.Id);
            var item = new JobListItemViewModel(job) { Detail = running ? DescribeRunning(job) : Describe(job) };
            (job.State switch
            {
                _ when _host.Jobs.IsRunning(job.Id) => Running,
                JobState.Running or JobState.Paused or JobState.Pending => Queued,
                _ => Completed,
            }).Add(item);
        }

        Selected = Running.Concat(Queued).Concat(Completed).FirstOrDefault(j => j.Job.Id == selectedId)
                   ?? Running.FirstOrDefault() ?? Queued.FirstOrDefault() ?? Completed.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(JobListItemViewModel? value) => _ = LoadSelectedAsync();

    [RelayCommand]
    private void Pause()
    {
        if (Selected is { } job)
        {
            _host.Jobs.Pause(job.Job.Id);
        }
    }

    [RelayCommand]
    private void Resume()
    {
        if (Selected is { } job)
        {
            PauseMessage = null;
            _ = _host.Jobs.StartAsync(job.Job.Id, job.Job.Priority);
            Log.Add(Line(Strings.Get("Jobs.Log.Resumed")));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (CanPause)
        {
            Pause();
        }
        else if (CanResume)
        {
            Resume();
        }
    }

    [RelayCommand]
    private void OpenReview()
    {
        if (Selected is { } job)
        {
            if (job.Job.Type == JobType.Reanalyze)
            {
                _navigator.ShowPage(new RegenerationViewModel(_host, _navigator, reanalysisJobId: job.Job.Id));
            }
            else
            {
                _navigator.OpenReview(job.Job.Id);
            }
        }
    }

    public void Dispose() => _host.Jobs.StatusChanged -= OnStatus;

    private async Task LoadSelectedAsync()
    {
        if (Selected is not { } selected)
        {
            Errors.Clear();
            return;
        }

        var job = await _host.Catalog.Jobs.GetAsync(selected.Job.Id, CancellationToken.None) ?? selected.Job;
        Stages[0].Label = Strings.Get(job.Type == JobType.AnalyzeInPlace ? "Stage.Read" : "Stage.Copy");
        var items = await _host.Catalog.Jobs.GetItemsAsync(job.Id, CancellationToken.None);
        SelectedDetail = Strings.Format("Jobs.Detail", Strings.Get("JobType." + job.Type), job.CreatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
            items.Count.ToString("N0", CultureInfo.CurrentCulture), _host.ProfileName(job.ProfileId));
        if (Selected != selected)
        {
            return; // another job was selected meanwhile
        }

        Errors.Clear();
        foreach (var failed in items.Where(i => i.State == JobItemState.Failed))
        {
            Errors.Add(new JobErrorViewModel(Path.GetFileName(failed.SourcePath), failed.Error ?? string.Empty));
        }

        var copied = items.Count(i => i.AssetId is not null);
        var done = items.Count(i => i.State == JobItemState.Done);
        UpdateStages(items.Count, copied, done);
        if (!_host.Jobs.IsRunning(job.Id))
        {
            Remaining = string.Empty;
            Elapsed = string.Empty;
        }
        PauseMessage = job.State == JobState.Paused && !_host.Jobs.IsRunning(job.Id) ? Strings.Get("Jobs.Paused") : null;
        OnPropertyChanged(nameof(ErrorsHeader));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    private void UpdateStages(int total, int copied, int done)
    {
        void Set(int index, int value)
        {
            Stages[index].Value = total == 0 ? 0 : (double)value / total;
            Stages[index].Count = value.ToString("N0", CultureInfo.CurrentCulture);
        }

        Set(0, copied);
        Set(1, done);
        Set(2, done);
        Set(4, done);
    }

    private void OnStatus(object? sender, JobStatus status) => Ui.Post(() =>
    {
        var item = Running.Concat(Queued).Concat(Completed).FirstOrDefault(j => j.Job.Id == status.JobId);
        if (item is null || (status.State == JobState.Running && !Running.Contains(item)) || status.State != JobState.Running)
        {
            if (status.State != JobState.Running)
            {
                Log.Add(Line(status.PauseReason is { } reason ? Strings.Get("Jobs.Pause." + reason) : Strings.Get("Jobs.State." + status.State)));
                if (status.PauseReason is JobPauseReason.DestinationUnavailable or JobPauseReason.SourceUnavailable)
                {
                    PauseMessage = Strings.Get("Jobs.Pause." + status.PauseReason);
                }
            }

            _ = ActivateAsync();
            return;
        }

        var p = status.Progress;
        item.Progress = p.Total == 0 ? 0 : (double)(p.Completed + p.Failed) / p.Total;
        item.Remaining = status.Remaining is { } r ? Formats.Duration(r) : string.Empty;
        item.Detail = DescribeRunning(item.Job);
        if (Selected == item)
        {
            UpdateStages(p.Total, p.Copied, p.Completed);
            Remaining = status.Remaining is { } left ? Strings.Format("Jobs.Remaining", Formats.Duration(left)) : string.Empty;
            Elapsed = Strings.Format("Jobs.Elapsed", Formats.Duration(status.Elapsed));
            if (p.Failed > Errors.Count)
            {
                _ = LoadSelectedAsync();
            }
        }
    });

    /// <summary>Running jobs side by side (3g): a background analysis says when it is waiting for an import.</summary>
    private string DescribeRunning(Job job) => job.Priority != JobPriority.Low
        ? Strings.Get("JobType." + job.Type)
        : Strings.Get(_host.Jobs.HasNormalPriorityRunning ? "Jobs.Background.Waiting" : "Jobs.Background.Running");

    private string Describe(Job job) => $"{job.CreatedAt.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)} · {Strings.Get("Jobs.State." + job.State)}";

    private static string Line(string text) => $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}  {text}";
}
