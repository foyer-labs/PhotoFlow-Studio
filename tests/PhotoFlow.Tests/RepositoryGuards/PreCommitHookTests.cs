// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.RepositoryGuards;

/// <summary>
/// The pre-commit hook (and the CI check) keep personal photos out of the public repository
/// (CLAUDE.md §8, risk R9). Exercised on a throw-away git repository with the real scripts.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class PreCommitHookTests : IDisposable
{
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00];
    private static readonly byte[] TiffHeader = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] HeicHeader = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63];

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "pf-hook-" + Guid.NewGuid().ToString("N"));

    public PreCommitHookTests()
    {
        Directory.CreateDirectory(_repo);
        Copy("scripts/check-images.sh", executable: true);
        Copy(".githooks/pre-commit", executable: true);
        Copy(".githooks/allowed-images.txt");
        Copy("tests/data/public/LICENSES.md");
        Git("init", "-q");
        Git("config", "user.name", "test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "core.hooksPath", ".githooks");
        Git("config", "core.autocrlf", "false");
        Write("readme.txt", "hello"u8.ToArray());
        Git("add", "-A");
        var init = Commit("init");
        Assert.True(init.ExitCode == 0, init.Output);
    }

    [Fact]
    public void Jpeg_is_blocked() => AssertBlocked("my photo.jpg", JpegHeader, "image extension");

    [Fact]
    public void Raw_is_blocked() => AssertBlocked("DSC_0001.NEF", TiffHeader, "RAW extension");

    [Fact]
    public void Renamed_jpeg_is_blocked_by_its_signature() => AssertBlocked("notes.txt", JpegHeader, "JPEG signature");

    [Fact]
    public void Heic_without_extension_is_blocked_by_its_signature() => AssertBlocked("IMG_0001", HeicHeader, "ISO-BMFF");

    [Fact]
    public void Unlisted_file_in_public_test_data_is_blocked() =>
        AssertBlocked("tests/data/public/sample.png", PngHeader, "image extension");

    [Fact]
    public void Listed_file_in_public_test_data_is_accepted()
    {
        Write("tests/data/public/sample.png", PngHeader);
        File.AppendAllText(Path.Combine(_repo, "tests/data/public/LICENSES.md"), "| `sample.png` | test | test | CC0-1.0 | 2026-10-12 |\n");
        Git("add", "-A");
        Assert.Equal(0, Commit("listed public file").ExitCode);
    }

    [Fact]
    public void Allow_listed_app_icon_is_accepted()
    {
        Write("src/PhotoFlow.App/Assets/photoflow.ico", [0x00, 0x00, 0x01, 0x00, 0x01, 0x00]);
        Git("add", "-A");
        Assert.Equal(0, Commit("icon").ExitCode);
    }

    [Fact]
    public void Ci_check_catches_a_commit_made_with_no_verify()
    {
        Write("sneaky.bin", JpegHeader);
        Git("add", "-A");
        Assert.Equal(0, Git("commit", "-q", "--no-verify", "-m", "bypass").ExitCode);

        var check = Run("sh", "scripts/check-images.sh", "--all");
        Assert.NotEqual(0, check.ExitCode);
        Assert.Contains("sneaky.bin", check.Output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git object files are read-only
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
            // Temporary folder: leftovers are harmless.
        }
    }

    private void AssertBlocked(string path, byte[] content, string expectedReason)
    {
        Write(path, content);
        Git("add", "-A");
        var result = Commit("attempt");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(path, result.Output, StringComparison.Ordinal);
        Assert.Contains(expectedReason, result.Output, StringComparison.Ordinal);
    }

    private (int ExitCode, string Output) Commit(string message) => Git("commit", "-q", "-m", message);

    private (int ExitCode, string Output) Git(params string[] args) => Run("git", args);

    private (int ExitCode, string Output) Run(string fileName, params string[] args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (fileName == "sh" && OperatingSystem.IsWindows())
        {
            info.FileName = GitShell();
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>On Windows, the POSIX shell shipped with Git for Windows.</summary>
    private static string GitShell()
    {
        var info = new ProcessStartInfo("git", "--exec-path") { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(info)!;
        var execPath = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        // <git>/mingw64/libexec/git-core → <git>/bin/sh.exe
        var gitRoot = Path.GetFullPath(Path.Combine(execPath, "..", "..", ".."));
        var sh = Path.Combine(gitRoot, "bin", "sh.exe");
        return File.Exists(sh) ? sh : Path.Combine(gitRoot, "usr", "bin", "sh.exe");
    }

    private void Copy(string relativePath, bool executable = false)
    {
        var target = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Repository.PathOf(relativePath.Split('/')), target);
        if (executable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        }
    }

    private void Write(string relativePath, byte[] content)
    {
        var target = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
    }
}
