// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PhotoFlow.Inference.Gpu;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Inference;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class GpuRuntimeTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-gpu");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_embedded_manifest_lists_the_cuda_13_and_cudnn_9_components()
    {
        var manifest = GpuRuntimeManifest.Embedded();
        Assert.Equal("windows-x86_64", manifest.Platform);
        Assert.Equal(["cuda_cudart", "libcublas", "libcufft", "libcurand", "cuda_nvrtc", "libnvjitlink", "cudnn"], manifest.Components.Select(c => c.Name));
        Assert.All(manifest.Components, c =>
        {
            Assert.StartsWith("https://developer.download.nvidia.com/compute/", c.Url, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", c.Sha256);
            Assert.NotEmpty(c.Extract);
        });
    }

    [Fact]
    public async Task Only_the_listed_libraries_are_extracted_and_a_second_install_downloads_nothing()
    {
        var (manifest, handler) = Arrange(("bin/x64/cudart64_13.dll", "bin/readme.txt"));
        var installer = new GpuRuntimeInstaller(new HttpClient(handler), manifest, Path.Combine(_temp.Path, "rt"), Path.Combine(_temp.Path, "dl"));

        await installer.InstallAsync(licensesAccepted: true, null, Ct);
        await installer.InstallAsync(licensesAccepted: true, null, Ct);

        Assert.True(installer.IsInstalled);
        Assert.Equal(1, handler.Requests);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "rt", "cudart64_13.dll")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "rt", "readme.txt")));
    }

    [Fact]
    public async Task Nothing_is_downloaded_without_accepting_the_nvidia_licenses()
    {
        var (manifest, handler) = Arrange(("bin/cudart64_13.dll", null));
        var installer = new GpuRuntimeInstaller(new HttpClient(handler), manifest, Path.Combine(_temp.Path, "rt"), Path.Combine(_temp.Path, "dl"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(licensesAccepted: false, null, Ct));

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_tampered_archive_is_rejected_and_nothing_is_installed()
    {
        var (manifest, handler) = Arrange(("bin/cudart64_13.dll", null));
        handler.Tamper = true;
        var installer = new GpuRuntimeInstaller(new HttpClient(handler), manifest, Path.Combine(_temp.Path, "rt"), Path.Combine(_temp.Path, "dl"));

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(licensesAccepted: true, null, Ct));

        Assert.False(installer.IsInstalled);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "rt", "cudart64_13.dll")));
    }

    private (GpuRuntimeManifest Manifest, FakeServer Handler) Arrange((string Dll, string? Extra) entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { entries.Dll, entries.Extra }.OfType<string>())
            {
                using var writer = new StreamWriter(zip.CreateEntry("cuda_cudart-windows-x86_64-13.0.96-archive/" + name).Open());
                writer.Write("payload " + name);
            }
        }

        var bytes = buffer.ToArray();
        var manifest = new GpuRuntimeManifest("windows-x86_64",
            [new GpuRuntimeComponent("cuda_cudart", "13.0.96", "CUDA Toolkit EULA", "https://example.invalid/cudart.zip",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, ["cudart64_*.dll"])]);
        return (manifest, new FakeServer(bytes));
    }

    public void Dispose() => _temp.Dispose();

    private sealed class FakeServer(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public bool Tamper { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = (byte[])payload.Clone();
            if (Tamper)
            {
                body[^1] ^= 0xFF;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
