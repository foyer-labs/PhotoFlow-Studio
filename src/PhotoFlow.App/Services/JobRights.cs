// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Sources.Jobs;

namespace PhotoFlow.App.Services;

/// <summary>Author and copyright of an event (M2): those chosen at its import, kept in the settings of the job.</summary>
public static class JobRights
{
    /// <summary>The rights of the import <paramref name="job"/>, or null (other jobs, or an import made before M2): use the settings.</summary>
    public static RightsInfo? Of(Job? job)
    {
        if (job is not { Type: JobType.Import })
        {
            return null;
        }

        try
        {
            return ImportJobRunner.DeserializeSettings(job.SettingsSnapshotJson).Rights;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
