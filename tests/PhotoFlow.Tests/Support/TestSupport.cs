// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Tests.Support;

/// <summary>Test categories of CLAUDE.md §7 (xUnit trait <c>Category</c>).</summary>
public static class TestCategory
{
    public const string Name = "Category";
    public const string Unit = "Unit";
    public const string Parity = "Parity";
    public const string Integration = "Integration";
    public const string Scenario = "Scenario";
    public const string Quality = "Quality";
    public const string Performance = "Performance";
}

/// <summary>
/// Local test data lives outside the repository, in the folder named by <c>PHOTOFLOW_TESTDATA</c>.
/// When it is not set, local tests are skipped, never failed.
/// </summary>
public static class TestData
{
    public const string EnvironmentVariable = "PHOTOFLOW_TESTDATA";

    public static string? Root
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) || !Directory.Exists(value) ? null : value;
        }
    }

    /// <summary>Returns the requested folder under the test-data root, or skips the test.</summary>
    public static string RequireDirectory(params string[] relativeParts)
    {
        var root = Root;
        if (root is null)
        {
            Assert.Skip($"{EnvironmentVariable} is not set: local test skipped.");
        }

        var path = Path.Combine([root, .. relativeParts]);
        if (!Directory.Exists(path))
        {
            Assert.Skip($"Local test data not found: {path}");
        }

        return path;
    }
}

/// <summary>Locates files of the repository checkout (tests only; product code uses IAppPaths).</summary>
public static class Repository
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    public static string PathOf(params string[] relativeParts) => Path.Combine([Root, .. relativeParts]);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PhotoFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (PhotoFlow.slnx) not found.");
    }
}
