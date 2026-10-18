// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Level D only with a GPU that can run it (P16, D5b-4 as revised: the 7B int4 peaks at ~10 GB): CUDA in use and
/// 12 GB of memory. A card sold as 12 GB (12288 MiB, or a little less) qualifies; an 8 GB card, no CUDA, or unknown
/// memory: level D stays off.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class VlmAvailabilityTests
{
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(true, 24_576, true)]
    [InlineData(true, 12_288, true)]
    [InlineData(true, 12_050, true)]
    [InlineData(true, 10_240, false)]
    [InlineData(true, 8_188, false)]
    [InlineData(false, 24_576, false)]
    public void Level_D_needs_CUDA_and_12_GB(bool cuda, long memoryMiB, bool expected)
    {
        var hardware = new HardwareInfo(cuda ? [ExecutionBackend.Cuda, ExecutionBackend.Cpu] : [ExecutionBackend.Cpu], 16, "NVIDIA GeForce", memoryMiB * MiB);
        Assert.Equal(expected, PhotoFlowHost.VlmGpuAllows(hardware));
    }

    [Fact]
    public void Unknown_memory_is_not_enough() =>
        Assert.False(PhotoFlowHost.VlmGpuAllows(new HardwareInfo([ExecutionBackend.Cuda, ExecutionBackend.Cpu], 16, "NVIDIA GeForce", null)));
}
