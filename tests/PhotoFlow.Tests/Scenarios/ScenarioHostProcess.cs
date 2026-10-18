// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;

namespace PhotoFlow.Tests.Scenarios;

/// <summary>Runs PhotoFlow.ScenarioHost as a separate process, so that a test can kill it midway like a crash.</summary>
internal static class ScenarioHostProcess
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Process Start(string catalogPath, string backups, long jobId, string log, int delayMs, string mode = "import")
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "PhotoFlow.ScenarioHost.dll");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { dll, catalogPath, backups, jobId.ToString(CultureInfo.InvariantCulture), log, delayMs.ToString(CultureInfo.InvariantCulture), mode })
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("ScenarioHost did not start.");
    }

    /// <summary>Reads the host output until <paramref name="stop"/> returns non-null or the process ends.</summary>
    public static async Task WaitForAsync(Process host, Func<string, string?> stop, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        while (await host.StandardOutput.ReadLineAsync(limit.Token) is { } line)
        {
            if (stop(line) is not null)
            {
                return;
            }
        }

        var error = await host.StandardError.ReadToEndAsync(limit.Token);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
    }

    /// <summary>The "completed N" value of a progress line ("copied C completed N failed F").</summary>
    public static int Completed(string line) => int.Parse(line.Split(' ')[3], CultureInfo.InvariantCulture);

    /// <summary>After a killed process the OS can release the catalog lock with some delay (seen on CI runners).</summary>
    public static async Task<SqliteCatalog> OpenCatalogAsync(string catalogPath, string backups)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                return await SqliteCatalog.OpenAsync(new CatalogOpenOptions(catalogPath, backups), Ct);
            }
            catch (CatalogLockedException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, Ct);
            }
        }
    }
}
