// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Detectors;
using PhotoFlow.Analysis.Grouping;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Sources;
using PhotoFlow.Data;
using PhotoFlow.Faces.Detection;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Inference.Gpu;
using PhotoFlow.Inference.Models;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Tagging.Clip;
using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Tagging.Captions;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tagging.RamPlus;
using PhotoFlow.Tagging.Vocabulary;

namespace PhotoFlow.App.Services;

/// <summary>Which models the analysis uses, for the status bar and the first run (3b).</summary>
/// <summary>The loaded models of the analysis (null when not installed).</summary>
public sealed record AnalysisComponents(IImageOperations Operations, IObjectDetector? Objects, IFaceDetector? Faces, IImageEmbedder? Embedder, IFaceEmbedder? Fingerprints,
    IImageTagger? Tagger = null, ITextReader? TextReader = null, IImageCaptioner? Captioner = null);

public sealed record AnalysisSetup(string? ObjectDetector, string? FaceDetector, bool EyesAvailable, ExecutionBackend Backend, string? Embedder = null, string? FaceRecognizer = null);

/// <summary>
/// Composition of the non-UI services (catalog, exiftool, handlers, models, jobs, card detection), started once at
/// launch. Model sessions are created on first use and kept for the session; without models the analysis still
/// measures sharpness and exposure.
/// </summary>
public sealed class PhotoFlowHost : IAsyncDisposable
{
    public const string GpuPackFolder = "cuda13-cudnn9";

    /// <summary>Model adapters this version can run: only these models can be downloaded or chosen (board 5g).</summary>
    public static readonly IReadOnlySet<string> ImplementedAdapters = new HashSet<string>(StringComparer.Ordinal)
    {
        "dfine-hf-v1", "insightface-buffalo-v1", "yunet-v1", "clip-hf-v1", "sface-v1", "ramplus-v1", "ppocr-v1", "florence2-v1", "qwen2.5-vl-genai",
        "qwen3.5-genai", "adaface-ir101-v1",
    };

    public const string ClipModelId = "clip-vit-b32";
    public const string RamPlusModelId = "ramplus-fp32";
    public const string OcrModelId = "ppocr-en";
    public const string CaptionModelId = "florence2-base-ft";
    public const string SFaceModelId = "opencv-sface-2021dec";
    public const string BuffaloModelId = "insightface-buffalo-l";
    public const string AdaFaceModelId = "adaface-ir101-webface12m";

    /// <summary>
    /// Level D models (spec §5, U1 revised on 15 Oct 2026): Qwen3.5-9B for a new installation; Qwen2.5-VL-7B stays in
    /// use where it is already converted until the user chooses the newer one — a model never changes on its own.
    /// </summary>
    public const string VlmModelId = "qwen3.5-9b";

    public const string PreviousVlmModelId = "qwen2.5-vl-7b";

    /// <summary>
    /// Level D needs an NVIDIA GPU with 12 GB (P16 as revised on 13 Oct 2026): the 7B int4 alone peaks at ~10 GB
    /// (weights 6.5 GB, ONNX Runtime GenAI, generation) besides Windows and the other models. Cards sold as 12 GB may
    /// report a little less, so the bound leaves half a gigabyte.
    /// </summary>
    public const long VlmGpuMemoryBytes = 11_776L * 1024 * 1024;

    private readonly SemaphoreSlim _sessionsGate = new(1, 1);
    private readonly Lock _settingsLock = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly List<IInferenceSession> _sessions = [];
    private readonly List<IInferenceSession> _analysisSessions = [];

    // Level D needs the GPU alone (P16): analyses enter shared, the VLM pass exclusive (see VlmPass).
    private readonly GpuTurns _gpuTurns = new();
    private CullingPhotoProcessor? _processor;
    private AnalysisComponents? _components;
    private Task _maintenance = Task.CompletedTask;
    private readonly CancellationTokenSource _shutdown = new();

    private PhotoFlowHost(IAppPaths paths, IFileSystem fileSystem, SettingsStore settingsStore, AppSettings settings, SqliteCatalog catalog, ExifToolSession exifTool)
    {
        Paths = paths;
        FileSystem = fileSystem;
        SettingsStore = settingsStore;
        Settings = settings;
        Catalog = catalog;
        ExifTool = exifTool;
        ExifReader = new ExifToolExifReader(exifTool);
        MetadataWriter = new ExifToolMetadataWriter(exifTool, fileSystem);
        MetadataReader = new ExifToolMetadataReader(exifTool, fileSystem);
        Thumbnails = new ThumbnailCache(fileSystem, paths.ThumbnailsDirectory);
        Previews = new PreviewProvider(fileSystem, ImageHandlerRegistry.CreateDefault(fileSystem, new ExifToolPreviewSource(exifTool), new MagickRawDecoder()));
        Inference = new OnnxRuntimeInferenceProvider(new InferenceOptions(Path.Combine(paths.GpuRuntimeDirectory, GpuPackFolder)));
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        Models = new ModelRegistry(Http, ModelRegistry.EmbeddedManifest(), paths.ModelsDirectory);
        GpuRuntime = new GpuRuntimeInstaller(Http, GpuRuntimeManifest.Embedded(), Path.Combine(paths.GpuRuntimeDirectory, GpuPackFolder), Path.Combine(paths.GpuRuntimeDirectory, "downloads"));
        Deferred = new DeferredCopyService(new CopyEngine(fileSystem), catalog.DeferredCopies);
        Planner = new ImportPlanner(fileSystem, ExifReader, catalog.Assets);
        Jobs = new JobManager(() => new JobRunnerRouter(
            Catalog,
            new ImportJobRunner(Catalog, new IngestService(new CopyEngine(FileSystem)), ExifReader, FileSystem, new LazyProcessor(this), Deferred, BatchStages()),
            new ArchiveJobRunner(Catalog, ExifReader, FileSystem, new LowPriorityProcessor(this), BatchStages()),
            new ReanalysisJobRunner(Catalog, FileSystem, new LowPriorityProcessor(this), CurrentVersionsAsync, Vlm, Previews)));
        Cards = new PollingCardDetector();
        Maintenance = new MaintenanceService(paths, new InternalFileCleaner(paths), protectedBackup: () => Restore?.Pending);
        Restore = new CatalogRestoreService(paths);
        FingerprintKeys = new FingerprintKeyStore(Path.Combine(paths.ConfigDirectory, "face-key.json"));
        Profiles = new ProfileLibrary(paths.ProfilesDirectory);
        FingerprintKeys.Open();
    }

    /// <summary>Key of the face fingerprints (D12): locked on another account or PC until the password or a recompute.</summary>
    public FingerprintKeyStore FingerprintKeys { get; }

    /// <summary>Built-in and custom scene profiles (spec §6, board 5e).</summary>
    public ProfileLibrary Profiles { get; }

    /// <summary>Display name of a profile id (custom profiles have their own name).</summary>
    public string ProfileName(string? id) => Profiles.Find(id) is { } p ? ViewModels.ProfileChoice.NameOf(p) : Localization.Strings.Get("Profile." + (id ?? ProfileLibrary.FallbackId));

    public IAppPaths Paths { get; }

    /// <summary>Updates of the application (spec §11).</summary>
    public AppUpdates Updates { get; } = new();

    /// <summary>Where the catalog lives and the changes waiting for the next start (spec §9); set by the application.</summary>
    public CatalogLocationService? CatalogLocation { get; set; }

    public IFileSystem FileSystem { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; }

    public ICatalog Catalog { get; }

    public ExifToolSession ExifTool { get; }

    public IExifReader ExifReader { get; }

    public IMetadataWriter MetadataWriter { get; }

    public IMetadataReader MetadataReader { get; }

    /// <summary>The XMP writer with the current preferences (captions in <c>dc:description</c>) and the sync of external changes.</summary>
    public XmpPublisher CreatePublisher() => new(Catalog, MetadataWriter, Settings.Xmp.WriteCaptions, MetadataReader);

    /// <summary>Imports the XMP changes made by other programs (spec §4).</summary>
    public ExternalXmpSync CreateXmpSync() => new(Catalog, MetadataReader);

    /// <summary>The versions the current models record (a regeneration recomputes what older models made).</summary>
    public async Task<ModelVersions> CurrentVersionsAsync(CancellationToken ct)
    {
        var c = await GetAnalysisComponentsAsync(ct).ConfigureAwait(false);
        var culling = new CullingAnalyzer(c.Operations, c.Objects, c.Faces);
        return new ModelVersions(culling.Id, culling.ModelVersion, c.Embedder?.ModelVersion, c.Fingerprints?.ModelVersion, c.Tagger?.ModelVersion,
            c.Captioner?.ModelVersion, c.TextReader?.ModelVersion, Vlm.ModelVersion, c.Captioner?.Language ?? "en", Vlm.Language);
    }

    /// <summary>The GPU can run level D: CUDA in use and at least <see cref="VlmGpuMemoryBytes"/> of memory (unknown memory: no).</summary>
    public static bool VlmGpuAllows(HardwareInfo hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        return hardware.AvailableBackends.Contains(ExecutionBackend.Cuda) && hardware.GpuMemoryBytes >= VlmGpuMemoryBytes;
    }

    public bool VlmGpuReady => VlmGpuAllows(Inference.Detected);

    /// <summary>The conversion of the level D model with the scripts next to the application (folder <c>vlm</c>).</summary>
    public VlmConversion CreateVlmConversion() => new(Models, Paths, new ProcessVlmToolchain(), Path.Combine(AppContext.BaseDirectory, "vlm"));

    /// <summary>Level D can run: a model is converted and the GPU allows it.</summary>
    public bool VlmAvailable => VlmGpuReady && ActiveVlmModelId is not null;

    /// <summary>
    /// The level D model in use: the one chosen in Settings › Models when it is converted; otherwise Qwen2.5-VL when it
    /// was already converted (no silent change of the captions), otherwise Qwen3.5. Null when none is converted.
    /// </summary>
    public string? ActiveVlmModelId
    {
        get
        {
            if (Settings.ModelChoices.GetValueOrDefault(nameof(ModelFunction.Vlm)) is { } chosen
                && Models.Models.Any(m => m.Id == chosen && m.Function == ModelFunction.Vlm) && Models.IsInstalled(chosen))
            {
                return chosen;
            }

            return new[] { PreviousVlmModelId, VlmModelId }.Where(id => Models.Models.Any(m => m.Id == id)).FirstOrDefault(Models.IsInstalled);
        }
    }

    /// <summary>Level D in a pass of its own, with the GPU free of the other models.</summary>
    public IExclusiveCaptioning Vlm => _vlmPass ??= new VlmPass(this);

    private VlmPass? _vlmPass;

    /// <summary>A per-photo analysis: shared with the others, waits while a level D pass holds the GPU.</summary>
    public Task<IDisposable> EnterAnalysisAsync(CancellationToken ct) => _gpuTurns.EnterSharedAsync(ct);

    /// <summary>Frees the GPU of the analysis models (they load again at the next analysis).</summary>
    private async Task ReleaseAnalysisModelsAsync(CancellationToken ct)
    {
        await _sessionsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var session in _analysisSessions)
            {
                session.Dispose();
                _sessions.Remove(session);
            }

            _analysisSessions.Clear();
            _components = null;
            _processor = null;
        }
        finally
        {
            _sessionsGate.Release();
        }
    }

    /// <summary>
    /// Level D (phase 5b): takes the GPU turn (new analyses wait), waits for the analyses running, frees the GPU of the
    /// analysis models, starts the VLM process for the pass and closes it at the end, so the analyses find the memory
    /// again. Only one pass at a time.
    /// </summary>
    private sealed class VlmPass(PhotoFlowHost host) : IExclusiveCaptioning
    {
        public string? ModelVersion => host.VlmAvailable && host.ActiveVlmModelId is { } id ? $"{id}@{host.Models.Get(id).Version}" : null;

        public string Language => "it";

        public async Task RunAsync(Func<IImageCaptioner, Task> work, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(work);
            if (ModelVersion is not { } version)
            {
                return;
            }

            using var turn = await host._gpuTurns.EnterExclusiveAsync(ct).ConfigureAwait(false);
            await host.ReleaseAnalysisModelsAsync(ct).ConfigureAwait(false);
            await using var captioner = new VlmCaptioner(host.Models.InstallDirectory(version.Split('@')[0]), version,
                Path.Combine(host.Paths.GpuRuntimeDirectory, GpuPackFolder), new SkiaImageOperations(), host.Paths);
            await work(captioner).ConfigureAwait(false);
        }
    }

    /// <summary>A regeneration (job Reanalyze, spec §5): the new results wait for the preview and the confirmation.</summary>
    public async Task<long> StartReanalysisAsync(ReanalysisSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var jobId = await ReanalysisJobRunner.CreateAsync(Catalog, settings with { Workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2) }, ct).ConfigureAwait(false);
        _ = Jobs.StartAsync(jobId, JobPriority.Low);
        return jobId;
    }

    public Task<ReanalysisPreview> PreviewReanalysisAsync(long jobId, CancellationToken ct) => Reanalysis.PreviewAsync(Catalog, jobId, ct);

    public async Task<ReanalysisApplied> ApplyReanalysisAsync(long jobId, CancellationToken ct)
    {
        // P22: a backup of the catalog before the people are reset, so the user can go back.
        if (await Catalog.Jobs.GetAsync(jobId, ct).ConfigureAwait(false) is { } job
            && ReanalysisJobRunner.DeserializeSettings(job.SettingsSnapshotJson) is { ResetPeople: true, Sample: null })
        {
            await Maintenance.BackupAsync(Catalog, "people-reset", ct).ConfigureAwait(false);
        }

        return await Reanalysis.ApplyAsync(Catalog, jobId, () => FingerprintKeys.Protector, CreatePublisher(), Settings.PrudentMode, ct).ConfigureAwait(false);
    }

    public Task DiscardReanalysisAsync(long jobId, CancellationToken ct) => Reanalysis.DiscardAsync(Catalog, jobId, ct);

    /// <summary>
    /// Another model for a function (spec §5, board 5g): saved, and the models are loaded again at once when no job
    /// is running (otherwise at the next start). Returns true when the new model is already in use.
    /// </summary>
    public async Task<bool> ChooseModelAsync(ModelFunction function, string modelId, CancellationToken ct)
    {
        await UpdateSettingsAsync(s => s with { ModelChoices = new Dictionary<string, string>(s.ModelChoices) { [function.ToString()] = modelId } }, ct).ConfigureAwait(false);
        return await ReloadModelsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Releases the loaded models so the next analysis loads the chosen ones; false while a job runs.</summary>
    public async Task<bool> ReloadModelsAsync(CancellationToken ct)
    {
        if (Jobs.AnyRunning)
        {
            return false;
        }

        await _sessionsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions)
            {
                session.Dispose();
            }

            _sessions.Clear();
            _analysisSessions.Clear();
            _components = null;
            _processor = null;
            _sceneClassifier = null;
            _textEmbedder = null;
            return true;
        }
        finally
        {
            _sessionsGate.Release();
        }
    }

    /// <summary>A .pfcatalog chosen elsewhere (empty Libraries) to import in the settings.</summary>
    public string? PendingCatalogImport { get; set; }

    /// <summary>The whole catalog in one file (spec §9).</summary>
    public Task<CatalogExportSummary> ExportCatalogAsync(string path, CatalogExportOptions options, CancellationToken ct) =>
        CatalogPackage.ExportAsync(Catalog, Paths, FingerprintKeys.Protector, path, options, ct);

    /// <summary>Prepares an exported catalog with the libraries remapped; it replaces this one at the next start.</summary>
    public async Task<string> ImportCatalogAsync(string path, IReadOnlyDictionary<long, string> roots, string? password, CancellationToken ct)
    {
        var prepared = await CatalogPackage.PrepareImportAsync(path, Paths, roots, password, FingerprintKeys.Protector, ct).ConfigureAwait(false);
        Restore.Request(prepared);
        return prepared;
    }

    /// <summary>The last sync of the XMP changes made by other programs (at start, in the background, or asked).</summary>
    public XmpSyncReport? LastXmpSync { get; private set; }

    /// <summary>Imports the changes other programs made to the XMP written by PhotoFlow (all, or some photos).</summary>
    public async Task<XmpSyncReport?> SyncExternalXmpAsync(IProgress<int>? progress, CancellationToken ct)
    {
        try
        {
            LastXmpSync = await CreateXmpSync().SyncAsync(null, progress, ct).ConfigureAwait(false);
            return LastXmpSync;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            return null;
        }
    }

    public ThumbnailCache Thumbnails { get; }

    /// <summary>Decoding and resampling for the UI and the duplicate search (no models).</summary>
    public IImageOperations ImageOperations { get; } = new SkiaImageOperations();

    public IPreviewProvider Previews { get; }

    public OnnxRuntimeInferenceProvider Inference { get; }

    public HttpClient Http { get; }

    public ModelRegistry Models { get; }

    public GpuRuntimeInstaller GpuRuntime { get; }

    public DeferredCopyService Deferred { get; }

    public ImportPlanner Planner { get; }

    public JobManager Jobs { get; }

    public ICardDetector Cards { get; }

    /// <summary>Backups and cleanup of PhotoFlow's internal files (B1).</summary>
    public MaintenanceService Maintenance { get; }

    public CatalogRestoreService Restore { get; }

    /// <summary>Outcome of a restore requested in the settings and applied at this start (null when none was pending).</summary>
    public CatalogRestoreOutcome? RestoreOutcome { get; private set; }

    /// <summary>The import path template from the settings; the default when the stored one is not valid.</summary>
    public PathTemplate PathTemplate
    {
        get
        {
            try
            {
                return new PathTemplate(Settings.Import.PathTemplate);
            }
            catch (ArgumentException)
            {
                return new PathTemplate(PathTemplate.Default);
            }
        }
    }

    /// <summary>Jobs found running at startup: the previous session ended unexpectedly (board 3c).</summary>
    public IReadOnlyList<Job> InterruptedJobs { get; private set; } = [];

    /// <exception cref="CatalogLockedException">Another PhotoFlow window uses the catalog.</exception>
    public static async Task<PhotoFlowHost> StartAsync(IAppPaths paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var fileSystem = new PhysicalFileSystem();
        var settingsStore = new SettingsStore(fileSystem, paths);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var restore = new CatalogRestoreService(paths);
        CatalogRestoreOutcome? restoreOutcome = null;
        if (restore.Pending is { } pending)
        {
            try
            {
                restoreOutcome = new CatalogRestoreOutcome(pending, await restore.ApplyPendingAsync(ct).ConfigureAwait(false), null);
            }
            catch (CatalogCorruptException e)
            {
                // The backup is damaged: nothing was replaced, the current catalog opens as before.
                restore.Cancel();
                restoreOutcome = new CatalogRestoreOutcome(pending, null, e.Message);
            }
        }

        var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(paths.CatalogPath, paths.BackupsDirectory), ct).ConfigureAwait(false);
        var exifTool = new ExifToolSession(settings.ExifToolPath is { } custom && File.Exists(custom)
            ? new ExifToolOptions(custom)
            : ExifToolOptions.Bundled(AppContext.BaseDirectory));
        var host = new PhotoFlowHost(paths, fileSystem, settingsStore, settings, catalog, exifTool) { RestoreOutcome = restoreOutcome };
        host.InterruptedJobs = await ImportJobRunner.RecoverInterruptedAsync(catalog, ct).ConfigureAwait(false);
        await catalog.Tags.SeedVocabularyAsync(RamPlusVocabulary.Entries, TagSource.RamPlus, ct).ConfigureAwait(false);
        host.Cards.Start();
        host._maintenance = Task.Run(async () =>
        {
            await host.Maintenance.RunAtStartupAsync(catalog, Path.Combine(paths.GpuRuntimeDirectory, "downloads"), host.GpuRuntime.IsInstalled, CancellationToken.None)
                .ConfigureAwait(false);
            await host.SyncExternalXmpAsync(null, host._shutdown.Token).ConfigureAwait(false);
        }, CancellationToken.None);
        return host;
    }

    public async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_settingsLock)
        {
            Settings = change(Settings);
        }

        // One write at a time, always of the latest settings: an older save finishing last cannot undo a newer change.
        await _settingsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SettingsStore.SaveAsync(Settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    /// <summary>
    /// Offline package (spec §11) from a folder copied from another PC: the models whose license is accepted (or needs
    /// no acceptance) and, with the NVIDIA licenses accepted, the GPU pack when all its archives are there. Every file
    /// is verified with SHA-256; the folder is only read. Acceptances are recorded only for what was installed.
    /// </summary>
    public async Task<(IReadOnlyList<string> Models, bool GpuPack)> ImportOfflinePackageAsync(
        string folder, IReadOnlySet<string> acceptedModelLicenses, bool nvidiaLicensesAccepted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(acceptedModelLicenses);
        var models = await Task.Run(() => Models.ImportOfflineAsync(folder, m => acceptedModelLicenses.Contains(m.Id), ct), ct).ConfigureAwait(false);
        var accepted = models.Where(id => Models.Get(id).License.RequiresAcceptance).ToList();
        if (accepted.Count > 0)
        {
            await UpdateSettingsAsync(s => s with { AcceptedModelLicenses = s.AcceptedModelLicenses.Concat(accepted).Distinct().ToList() }, ct).ConfigureAwait(false);
        }

        var gpu = false;
        if (nvidiaLicensesAccepted && GpuRuntime.HasOfflinePackage(folder))
        {
            await UpdateSettingsAsync(s => s with { NvidiaLicensesAccepted = true }, ct).ConfigureAwait(false);
            await Task.Run(() => GpuRuntime.ImportOfflineAsync(folder, true, ct), ct).ConfigureAwait(false);
            gpu = true;
        }

        return (models, gpu);
    }

    /// <summary>Stages run once every photo of a job is analyzed: groupings, then decisions per group (spec §3, phases 4-5).</summary>
    private IReadOnlyList<IBatchStage> BatchStages()
    {
        var embedder = DescribeAnalysis().Embedder is { } clip ? $"{clip}@{Models.Get(clip).Version}" : null;
        return
        [
            new GroupingStage(Catalog, Profiles.Get, embedder),
            new ProfileStage(Catalog, embedder is null ? null : new LazySceneClassifier(this), embedder),
            new ReanalysisStage(Catalog, FileSystem, new LazyProcessor(this), null, Profiles.Get, PipelineDefaults.DefaultAnalysisWorkers),
            new GroupDecisionStage(Catalog, Profiles.Get, new DecisionEngine()),
            new FacesStage(Catalog, () => FingerprintKeys.Protector),
            new TaggingStage(Catalog, GetCustomTagScanAsync),
            new NumbersStage(Catalog),
            new VlmCaptionStage(Catalog, FileSystem, Previews, Vlm),
        ];
    }

    /// <summary>
    /// "Re-analyze as…" for a group (D8, board 2a): the user's profile for its photos, then the photos measured with
    /// another profile are analyzed again and the job decided again (user decisions untouched, I5).
    /// </summary>
    public async Task ReanalyzeAsync(long jobId, IReadOnlyList<long> assetIds, string profileId, CancellationToken ct)
    {
        await Catalog.Groups.SetUserProfileAsync(jobId, assetIds, profileId, ct).ConfigureAwait(false);
        var job = await Catalog.Jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Job {jobId} not found.");
        var context = new JobContext(jobId, job.Type, job.ProfileId ?? "events");
        await new ReanalysisStage(Catalog, FileSystem, new LazyProcessor(this), null, Profiles.Get, PipelineDefaults.DefaultAnalysisWorkers).RunAsync(context, null, ct).ConfigureAwait(false);
        await new GroupDecisionStage(Catalog, Profiles.Get, new DecisionEngine()).RunAsync(context, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The fingerprint key is lost (4c3): a new key, then every fingerprint is recomputed from the library previews;
    /// names and assignments stay (spec §7). Needs the face recognition model.
    /// </summary>
    public async Task<(int Recomputed, int Missing)> RecomputeFingerprintsAsync(string? password, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var setup = DescribeAnalysis();
        if (setup.FaceRecognizer is not { } recognizer)
        {
            throw new InvalidOperationException("No face recognition model installed.");
        }

        FingerprintKeys.Reset(password);
        var session = await Inference.CreateSessionAsync(Models.Resolve(recognizer, RecognizerFile(recognizer)), ExecutionTarget.Auto, ct).ConfigureAwait(false);
        try
        {
            var embedder = CreateFaceEmbedder(recognizer, session);
            return await new FingerprintRecompute(Catalog, FileSystem, Previews, embedder, FingerprintKeys.Protector).RunAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>The tagging levels of the last import, or by hardware: RAM++ only with a GPU (spec §5).</summary>
    public TagLevels DefaultTagLevels => Settings.Import.Tags
        ?? (Inference.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda) ? TagLevels.Default : TagLevels.ZeroShot);

    /// <summary>The models that can make the fingerprints (P22): ArcFace in buffalo_l, SFace, AdaFace IR-101.</summary>
    public static readonly IReadOnlyList<string> FaceRecognizers = [BuffaloModelId, SFaceModelId, AdaFaceModelId];

    /// <summary>Installed and, when its license needs it, accepted.</summary>
    public bool ModelUsable(string id) =>
        Models.Models.FirstOrDefault(m => m.Id == id) is { } model && Models.IsInstalled(id)
        && (!model.License.RequiresAcceptance || Settings.AcceptedModelLicenses.Contains(id));

    /// <summary>The fingerprint model of a recognizer id, on its ONNX session.</summary>
    public static string RecognizerFile(string id) => id switch
    {
        SFaceModelId => SFaceEmbedder.File,
        AdaFaceModelId => AdaFaceEmbedder.File,
        _ => ArcFaceEmbedder.File,
    };

    public static IFaceEmbedder CreateFaceEmbedder(string id, IInferenceSession session) => id switch
    {
        SFaceModelId => new SFaceEmbedder(session),
        AdaFaceModelId => new AdaFaceEmbedder(session),
        _ => new ArcFaceEmbedder(session),
    };

    /// <summary>Models the analysis would use now (installed and, where needed, accepted).</summary>
    public AnalysisSetup DescribeAnalysis()
    {
        var gpu = Inference.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda);
        var chosenDetector = Settings.ModelChoices.GetValueOrDefault(nameof(ModelFunction.ObjectDetection));
        var detector = chosenDetector is not null && Models.IsInstalled(chosenDetector) ? chosenDetector
            : gpu && Models.IsInstalled("dfine-m-obj2coco") ? "dfine-m-obj2coco"
            : Models.IsInstalled("dfine-s-obj2coco") ? "dfine-s-obj2coco" : null;
        var chosenFaces = Settings.ModelChoices.GetValueOrDefault(nameof(ModelFunction.Faces));
        var buffalo = Models.IsInstalled("insightface-buffalo-l") && Settings.AcceptedModelLicenses.Contains("insightface-buffalo-l")
            && !(chosenFaces == "opencv-yunet-2023mar" && Models.IsInstalled(chosenFaces));
        var faces = buffalo ? "insightface-buffalo-l" : Models.IsInstalled("opencv-yunet-2023mar") ? "opencv-yunet-2023mar" : null;
        var recognizer = buffalo ? "insightface-buffalo-l" : faces is not null && Models.IsInstalled(SFaceModelId) ? SFaceModelId : null;

        // The recognizer chosen apart from the detector (P22): any of them works on the 5 keypoints of both detectors.
        if (faces is not null && Settings.ModelChoices.GetValueOrDefault(nameof(ModelFunction.FaceRecognition)) is { } chosen
            && FaceRecognizers.Contains(chosen) && ModelUsable(chosen))
        {
            recognizer = chosen;
        }

        return new AnalysisSetup(detector, faces, buffalo, gpu ? ExecutionBackend.Cuda : ExecutionBackend.Cpu,
            Models.IsInstalled(ClipModelId) ? ClipModelId : null, recognizer);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Cards.Dispose();
        await Jobs.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _maintenance.ConfigureAwait(false);
            await Maintenance.BackupAtCloseAsync(Catalog, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            // A backup that cannot be written must not prevent closing; the daily backup retries.
        }

        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        await Catalog.DisposeAsync().ConfigureAwait(false);
        await ExifTool.DisposeAsync().ConfigureAwait(false);
        Http.Dispose();
        await _settingsGate.WaitAsync().ConfigureAwait(false); // a settings save still running ends first
        _settingsGate.Dispose();
        _sessionsGate.Dispose();
        _gpuTurns.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Replaces the per-photo analysis (demo catalog of the documentation screenshots, tests): the jobs, their batch
    /// stages and the catalog stay the real ones. Null in normal use.
    /// </summary>
    public IPhotoProcessor? ProcessorOverride { get; set; }

    private async Task<IPhotoProcessor> GetProcessorAsync(CancellationToken ct)
    {
        if (ProcessorOverride is { } replacement)
        {
            return replacement;
        }

        var c = await GetAnalysisComponentsAsync(ct).ConfigureAwait(false);
        var created = new CullingPhotoProcessor(Previews, new CullingAnalyzer(c.Operations, c.Objects, c.Faces), new DecisionEngine(), Profiles.Get, Thumbnails,
            c.Embedder, c.Fingerprints, () => FingerprintKeys.Protector, c.Tagger, c.TextReader, c.Captioner);
        return Interlocked.CompareExchange(ref _processor, created, null) ?? created;
    }

    /// <summary>The models of the analysis, loaded once (shared by the pipeline and the benchmark).</summary>
    public async Task<AnalysisComponents> GetAnalysisComponentsAsync(CancellationToken ct)
    {
        await _sessionsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_components is not null)
            {
                return _components;
            }

            var setup = DescribeAnalysis();
            var operations = new SkiaImageOperations();
            IObjectDetector? objects = null;
            if (setup.ObjectDetector is { } detectorId)
            {
                var session = await Open(detectorId, "model.onnx").ConfigureAwait(false);
                objects = new DFineDetector(session, operations);
            }

            IFaceDetector? faces = null;
            if (setup.FaceDetector == "insightface-buffalo-l")
            {
                faces = new InsightFaceDetector(await Open(setup.FaceDetector, "det_10g.onnx").ConfigureAwait(false),
                    await Open(setup.FaceDetector, "2d106det.onnx").ConfigureAwait(false), operations);
            }
            else if (setup.FaceDetector is { } yunet)
            {
                faces = new YuNetFaceDetector(await Open(yunet, "face_detection_yunet_2023mar.onnx").ConfigureAwait(false), operations);
            }

            IImageEmbedder? embedder = setup.Embedder is { } clip
                ? new ClipImageEmbedder(await Open(clip, "vision_model.onnx").ConfigureAwait(false), operations)
                : null;
            IFaceEmbedder? fingerprints = setup.FaceRecognizer is { } recognizer
                ? CreateFaceEmbedder(recognizer, await Open(recognizer, RecognizerFile(recognizer)).ConfigureAwait(false))
                : null;
            IImageTagger? tagger = null;
            if (Models.IsInstalled(RamPlusModelId))
            {
                var classes = RamPlusClasses.Load(Models.Resolve(RamPlusModelId, RamPlusTagger.TagListFile).FilePath, Models.Resolve(RamPlusModelId, RamPlusTagger.ThresholdsFile).FilePath);
                tagger = new RamPlusTagger(await Open(RamPlusModelId, RamPlusTagger.ModelFile).ConfigureAwait(false), operations, classes, Settings.Tagging.ThresholdAdjustment);
            }

            ITextReader? textReader = Models.IsInstalled(OcrModelId)
                ? new PaddleOcr(await Open(OcrModelId, PaddleOcr.DetectorFile).ConfigureAwait(false),
                    await Open(OcrModelId, PaddleOcr.RecognizerFile, PaddleOcr.RecognizerTarget).ConfigureAwait(false), operations)
                : null;
            IImageCaptioner? captioner = Models.IsInstalled(CaptionModelId)
                ? new Florence2Captioner(await Open(CaptionModelId, Florence2Captioner.VisionFile).ConfigureAwait(false),
                    await Open(CaptionModelId, Florence2Captioner.EmbedFile).ConfigureAwait(false),
                    await Open(CaptionModelId, Florence2Captioner.EncoderFile).ConfigureAwait(false),
                    await Open(CaptionModelId, Florence2Captioner.DecoderFile).ConfigureAwait(false),
                    ByteLevelDecoder.Load(Models.Resolve(CaptionModelId, Florence2Captioner.TokenizerFile).FilePath), operations)
                : null;
            _components = new AnalysisComponents(operations, objects, faces, embedder, fingerprints, tagger, textReader, captioner);
            return _components;
        }
        finally
        {
            _sessionsGate.Release();
        }

        async Task<IInferenceSession> Open(string id, string file, ExecutionTarget target = ExecutionTarget.Auto)
        {
            var session = await Inference.CreateSessionAsync(Models.Resolve(id, file), target, ct).ConfigureAwait(false);
            _sessions.Add(session);
            _analysisSessions.Add(session);
            return session;
        }
    }

    /// <summary>
    /// Creates and starts the in-place analysis of a library at low priority: half the parallel analysis of an
    /// import, and it waits while an import runs (spec §10, archives).
    /// </summary>
    public async Task<long> StartArchiveAnalysisAsync(long libraryId, string profileId, ArchiveOrder order, CancellationToken ct)
    {
        var settings = new ArchiveJobSettings(libraryId, profileId, order, Settings.Import.Faces,
            Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2), Tags: DefaultTagLevels);
        var jobId = await ArchiveJobRunner.CreateAsync(Catalog, FileSystem, settings, ct).ConfigureAwait(false);
        _ = Jobs.StartAsync(jobId, JobPriority.Low);
        return jobId;
    }

    /// <summary>Low-priority analysis: each photo first yields to a running import.</summary>
    private sealed class LowPriorityProcessor(PhotoFlowHost host) : IPhotoProcessor
    {
        public async Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            await host.Jobs.YieldToNormalAsync(ct).ConfigureAwait(false);
            using var turn = await host.EnterAnalysisAsync(ct).ConfigureAwait(false);
            return await (await host.GetProcessorAsync(ct).ConfigureAwait(false)).ProcessAsync(photo, options, ct).ConfigureAwait(false);
        }
    }

    /// <summary>CLIP text model loaded on the first classification (the prompts are embedded once).</summary>
    private sealed class LazySceneClassifier(PhotoFlowHost host) : ISceneClassifier
    {
        public string ModelVersion => $"{ClipModelId}+scenes-v1";

        public async Task<IReadOnlyList<(string ProfileId, double Probability)>> ClassifyAsync(ReadOnlyMemory<float> imageEmbedding, CancellationToken ct) =>
            await (await host.GetSceneClassifierAsync(ct).ConfigureAwait(false)).ClassifyAsync(imageEmbedding, ct).ConfigureAwait(false);
    }

    private ClipSceneClassifier? _sceneClassifier;
    private ClipTextEmbedder? _textEmbedder;
    private CustomTagClassifier? _customTags;

    private RamPlusClasses? _ramPlusClasses;

    /// <summary>The official RAM++ threshold of a tag (board 5c), when the model is installed.</summary>
    public double? RamPlusThreshold(string key)
    {
        if (!Models.IsInstalled(RamPlusModelId))
        {
            return null;
        }

        _ramPlusClasses ??= RamPlusClasses.Load(Models.Resolve(RamPlusModelId, RamPlusTagger.TagListFile).FilePath, Models.Resolve(RamPlusModelId, RamPlusTagger.ThresholdsFile).FilePath);
        var index = _ramPlusClasses.Tags.ToList().IndexOf(key);
        return index < 0 ? null : _ramPlusClasses.Thresholds[index];
    }

    /// <summary>Model version of the stored CLIP embeddings (search by meaning, similar photos); null without CLIP.</summary>
    public string? EmbeddingModelVersion => Models.IsInstalled(ClipModelId) ? $"{ClipModelId}@{Models.Get(ClipModelId).Version}" : null;

    /// <summary>CLIP text embedding of a free-text search; null without the CLIP model.</summary>
    public async Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct)
    {
        if (EmbeddingModelVersion is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        await GetSceneClassifierAsync(ct).ConfigureAwait(false);
        return Vectors.Normalize(await _textEmbedder!.EmbedAsync(text, ct).ConfigureAwait(false));
    }

    /// <summary>The personal CLIP tags (level A) over the stored embeddings; null without the CLIP model.</summary>
    public async Task<CustomTagScan?> GetCustomTagScanAsync(CancellationToken ct)
    {
        if (!Models.IsInstalled(ClipModelId))
        {
            return null;
        }

        await GetSceneClassifierAsync(ct).ConfigureAwait(false);
        _customTags ??= new CustomTagClassifier(_textEmbedder!);
        return new CustomTagScan(Catalog, _customTags, $"{ClipModelId}@{Models.Get(ClipModelId).Version}");
    }

    private async Task<ClipSceneClassifier> GetSceneClassifierAsync(CancellationToken ct)
    {
        await _sessionsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sceneClassifier is null)
            {
                var session = await Inference.CreateSessionAsync(Models.Resolve(ClipModelId, "text_model.onnx"), ExecutionTarget.Auto, ct).ConfigureAwait(false);
                _sessions.Add(session);
                _textEmbedder = new ClipTextEmbedder(session, ClipTokenizer.Load(Models.Resolve(ClipModelId, "tokenizer.json").FilePath));
                _sceneClassifier = new ClipSceneClassifier(_textEmbedder);
            }

            return _sceneClassifier;
        }
        finally
        {
            _sessionsGate.Release();
        }
    }

    /// <summary>Loads the models when the first photo reaches the analysis, not at startup.</summary>
    private sealed class LazyProcessor(PhotoFlowHost host) : IPhotoProcessor
    {
        public async Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            using var turn = await host.EnterAnalysisAsync(ct).ConfigureAwait(false);
            return await (await host.GetProcessorAsync(ct).ConfigureAwait(false)).ProcessAsync(photo, options, ct).ConfigureAwait(false);
        }
    }
}
