// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Sources;

namespace PhotoFlow.Sources.Scanning;

/// <summary>
/// Card detection by polling (spec §4, ~2 s), cross-platform: removable volumes on Windows, mount points under
/// /Volumes, /media and /run/media on macOS and Linux; a volume counts as a card when it has a DCIM folder.
/// </summary>
public sealed class PollingCardDetector : ICardDetector
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>Where macOS and Linux mount removable media: operating-system conventions, not application paths.</summary>
    private static readonly string[] RemovableMountRoots =
    [
        "/Volumes/", // path-hygiene: allowed (macOS mount point for removable media)
        "/media/", // path-hygiene: allowed (Linux udisks mount point)
        "/run/media/", // path-hygiene: allowed (Linux udisks2 mount point)
    ];

    private readonly Func<IEnumerable<CardVolume>> _probe;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private Dictionary<string, CardVolume> _current = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;

    public PollingCardDetector(Func<IEnumerable<CardVolume>>? probe = null, TimeSpan? interval = null, TimeProvider? timeProvider = null)
    {
        _probe = probe ?? ProbeVolumes;
        _interval = interval ?? DefaultInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<CardVolume>? CardInserted;

    public event EventHandler<CardVolume>? CardRemoved;

    public IReadOnlyList<CardVolume> Current
    {
        get
        {
            lock (_sync)
            {
                return _current.Values.ToList();
            }
        }
    }

    public void Start()
    {
        Poll();
        _timer ??= _timeProvider.CreateTimer(_ => Poll(), null, _interval, _interval);
    }

    /// <summary>One detection pass: raises events for cards that appeared or disappeared since the last pass.</summary>
    public void Poll()
    {
        Dictionary<string, CardVolume> now;
        try
        {
            now = _probe().ToDictionary(v => v.RootPath, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // a volume being mounted or ejected: try again at the next pass
        }

        List<CardVolume> inserted, removed;
        lock (_sync)
        {
            inserted = now.Values.Where(v => !_current.ContainsKey(v.RootPath)).ToList();
            removed = _current.Values.Where(v => !now.ContainsKey(v.RootPath)).ToList();
            _current = now;
        }

        foreach (var card in removed)
        {
            CardRemoved?.Invoke(this, card);
        }

        foreach (var card in inserted)
        {
            CardInserted?.Invoke(this, card);
        }
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>Removable volumes (or user mount points on macOS/Linux) that contain a DCIM folder.</summary>
    public static IEnumerable<CardVolume> ProbeVolumes()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            CardVolume? card = null;
            try
            {
                var candidate = OperatingSystem.IsWindows()
                    ? drive.DriveType == DriveType.Removable
                    : RemovableMountRoots.Any(root => drive.RootDirectory.FullName.StartsWith(root, StringComparison.Ordinal));
                if (candidate && drive.IsReady && Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "DCIM")))
                {
                    card = new CardVolume(drive.RootDirectory.FullName, drive.VolumeLabel, drive.TotalSize);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Volume not ready: skip it.
            }

            if (card is not null)
            {
                yield return card;
            }
        }
    }
}
