// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// The working selection (decision S5): the photos sent to Develop and Export, with where they come from
/// ("Da Revisione · Gara Monza") and the photo open in Develop. Shared by the sections; changes raise
/// <see cref="Changed"/>.
/// </summary>
public sealed class WorkingSet
{
    private readonly Lock _lock = new();

    public string Origin { get; private set; } = string.Empty;

    /// <summary>Event name of the job the photos come from (for the {evento} placeholder), if known.</summary>
    public string? EventName { get; private set; }

    public IReadOnlyList<long> AssetIds { get; private set; } = [];

    public long? ActiveId { get; private set; }

    public event EventHandler? Changed;

    public bool IsEmpty => AssetIds.Count == 0;

    public void Set(string origin, IReadOnlyList<long> assetIds, long? activeId = null, string? eventName = null)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        lock (_lock)
        {
            Origin = origin;
            EventName = eventName;
            var list = assetIds.Distinct().ToList();
            AssetIds = list;
            ActiveId = activeId is { } id && list.Contains(id) ? id : list.Count > 0 ? list[0] : null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetActive(long assetId)
    {
        lock (_lock)
        {
            if (!AssetIds.Contains(assetId))
            {
                return;
            }

            ActiveId = assetId;
        }
    }

    public void Clear() => Set(string.Empty, []);
}
