// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Paths;
using PhotoFlow.Inference.Models;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The conversion of the level D model on the user's PC (phase 5b, U2-U3) with a fake Python: the weights the user put
/// in the source folder are verified first (a missing or different file stops everything); without Python the error
/// says so; otherwise the environment, the pinned packages and the conversion run in order, the progress follows the
/// script, and only a complete conversion is moved in place and marked installed. The packages are not installed again
/// when the requirements did not change. The weights are never touched.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class VlmConversionTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-vlm-conversion");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Weights_are_verified_then_converted_in_a_private_environment()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        var weights = new byte[] { 1, 2, 3, 4, 5 };
        var model = new ModelInfo("vlm-test", ModelFunction.Vlm, "Test VLM", "test@1/int4", 5, "gpu", "qwen2.5-vl-genai",
            new ModelLicense("Apache-2.0", "https://example.invalid", true, false, null),
            [new ModelFile("model.safetensors", "https://example.invalid/model.safetensors", Convert.ToHexString(SHA256.HashData(weights)).ToLowerInvariant(), weights.Length, null, [])])
        {
            ManualConvert = true,
        };
        using var http = new HttpClient();
        var registry = new ModelRegistry(http, [model], paths.ModelsDirectory);
        var tools = _temp.Sub("tools");
        await File.WriteAllTextAsync(Path.Combine(tools, "requirements.txt"), "torch==2.14.1\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(tools, "convert.py"), "# fake\n", Ct);
        var python = new FakePython();
        long free = long.MaxValue; // the CI machines have less than the ~45 GB of a real conversion
        var conversion = new VlmConversion(registry, paths, python, tools, _ => free);

        // Nothing in the source folder: stopped at the verification, Python never asked.
        var missing = await Assert.ThrowsAsync<VlmConversionException>(() => conversion.ConvertAsync(model.Id, null, Ct));
        Assert.Equal(VlmConversionStage.Verify, missing.Stage);
        Assert.Contains("missing model.safetensors", missing.Message, StringComparison.Ordinal);
        Assert.Empty(python.Calls);
        Assert.True(File.Exists(missing.LogPath));

        // A different file: stopped too.
        var source = registry.SourceDirectory(model.Id);
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "model.safetensors"), [9, 9, 9, 9, 9], Ct);
        Assert.Contains("different", (await Assert.ThrowsAsync<VlmConversionException>(() => conversion.ConvertAsync(model.Id, null, Ct))).Message, StringComparison.Ordinal);

        // The right weights, but no Python.
        await File.WriteAllBytesAsync(Path.Combine(source, "model.safetensors"), weights, Ct);
        python.Installed = false;
        Assert.Equal(VlmConversionStage.Python, (await Assert.ThrowsAsync<VlmConversionException>(() => conversion.ConvertAsync(model.Id, null, Ct))).Stage);

        // Python 3.12, but not enough free space: stopped before the environment.
        python.Installed = true;
        free = 20L * 1024 * 1024 * 1024;
        Assert.Equal(VlmConversionStage.Space, (await Assert.ThrowsAsync<VlmConversionException>(() => conversion.ConvertAsync(model.Id, null, Ct))).Stage);
        free = long.MaxValue;

        // Python 3.12: environment, packages, conversion; the model is installed.
        python.Calls.Clear();
        var stages = new List<VlmConversionProgress>();
        await conversion.ConvertAsync(model.Id, new SyncProgress<VlmConversionProgress>(stages.Add), Ct);
        Assert.True(registry.IsInstalled(model.Id));
        Assert.True(File.Exists(Path.Combine(registry.InstallDirectory(model.Id), ModelRegistry.ConvertedConfig)));
        Assert.Equal(weights, await File.ReadAllBytesAsync(Path.Combine(source, "model.safetensors"), Ct)); // weights untouched
        Assert.Equal(["--version", "venv", "pip", "convert.py"], python.Calls);
        Assert.Contains(stages, s => s is { Stage: VlmConversionStage.Convert, Detail: "vision" } && s.Fraction is > 0.5 and < 1);
        Assert.Equal(VlmConversionStage.Done, stages[^1].Stage);
        Assert.False(Directory.Exists(Path.Combine(paths.TempDirectory, "vlm-work"))); // the intermediate files never stay

        // Again: the environment and the packages are there, only the conversion runs; the old model is set aside.
        python.Calls.Clear();
        await conversion.ConvertAsync(model.Id, null, Ct);
        Assert.Equal(["--version", "convert.py"], python.Calls);

        // A conversion that fails leaves the installed model as it was.
        python.FailConversion = true;
        var failed = await Assert.ThrowsAsync<VlmConversionException>(() => conversion.ConvertAsync(model.Id, null, Ct));
        Assert.Equal((VlmConversionStage.Convert, "RuntimeError: out of memory"), (failed.Stage, failed.Message));
        Assert.True(registry.IsInstalled(model.Id));
        Assert.False(Directory.Exists(Path.Combine(paths.TempDirectory, "vlm-work")));

        // The environment can be removed; the model stays installed.
        Assert.True(conversion.EnvironmentExists);
        conversion.RemoveEnvironment();
        Assert.False(conversion.EnvironmentExists);
        Assert.True(registry.IsInstalled(model.Id));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Python 3.12 with venv, pip and the conversion script, as the processes would answer.</summary>
    private sealed class FakePython : IVlmToolchain
    {
        public bool Installed { get; set; } = true;

        public bool FailConversion { get; set; }

        public List<string> Calls { get; } = [];

        public Task<PythonCommand?> FindPythonAsync(CancellationToken ct)
        {
            Calls.Add("--version");
            return Task.FromResult(Installed ? new PythonCommand("py", ["-3.12"], new Version(3, 12, 9)) : null);
        }

        public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct)
        {
            if (arguments.Contains("venv"))
            {
                Calls.Add("venv");
                var venv = arguments[^1];
                var python = OperatingSystem.IsWindows() ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", "python");
                Directory.CreateDirectory(Path.GetDirectoryName(python)!);
                await File.WriteAllTextAsync(python, string.Empty, ct);
                return 0;
            }

            if (arguments.Contains("pip"))
            {
                Calls.Add("pip");
                line("Successfully installed torch-2.14.1");
                return 0;
            }

            Calls.Add("convert.py");
            var output = arguments[arguments.ToList().IndexOf("--output") + 1];
            var work = arguments[arguments.ToList().IndexOf("--work") + 1];
            Directory.CreateDirectory(Path.Combine(work, "recipe", "cache"));
            await File.WriteAllTextAsync(Path.Combine(work, "recipe", "cache", "text.onnx.data"), "intermediate", ct);
            foreach (var (step, name) in new[] { (0, "check"), (1, "embedding"), (2, "text"), (3, "vision"), (4, "config") })
            {
                line($"PF-PROGRESS {step}/5 {name}");
            }

            if (FailConversion)
            {
                line("PF-ERROR RuntimeError: out of memory");
                return 1;
            }

            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, ModelRegistry.ConvertedConfig), "{}", ct);
            line("PF-PROGRESS 5/5 done");
            line("PF-DONE");
            return 0;
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
