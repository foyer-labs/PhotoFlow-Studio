// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Core;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class NamedTensorTests
{
    [Fact]
    public void Shape_must_match_data_length()
    {
        var ok = NamedTensor.Float32("x", new float[6], 1, 2, 3);
        Assert.Equal([1, 2, 3], ok.Shape);
        Assert.Equal(6, ok.ElementCount);
        Assert.Throws<ArgumentException>(() => NamedTensor.Float32("x", new float[5], 1, 2, 3));
    }

    [Fact]
    public void Dimensions_must_not_be_negative()
    {
        Assert.Throws<ArgumentException>(() => NamedTensor.Int64("ids", Array.Empty<long>(), 1, -1));
        Assert.Throws<ArgumentException>(() => NamedTensor.Int64("ids", Array.Empty<long>()));

        // An empty dimension is valid (the cache of a decoder before its first step) and holds no data.
        Assert.Equal(0, NamedTensor.Float32("past", Array.Empty<float>(), 3, 12, 0, 64).ElementCount);
        Assert.Throws<ArgumentException>(() => NamedTensor.Float32("past", new float[1], 3, 12, 0, 64));
    }

    [Fact]
    public void Typed_access_rejects_the_wrong_element_type()
    {
        var tensor = NamedTensor.UInt8("img", new byte[3], 3);
        Assert.Equal(3, tensor.AsUInt8().Length);
        Assert.Throws<InvalidOperationException>(() => tensor.AsFloat32());
    }

    [Fact]
    public void Shape_is_copied_defensively()
    {
        int[] shape = [2, 2];
        var tensor = NamedTensor.Float32("x", new float[4], shape);
        shape[0] = 99;
        Assert.Equal(2, tensor.Shape[0]);
    }
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class RgbImageAndBoxTests
{
    [Fact]
    public void Rgb_image_validates_buffer_size()
    {
        Assert.Throws<ArgumentException>(() => new RgbImage(2, 2, new byte[11]));
        Assert.Equal(2, new RgbImage(2, 2, new byte[12]).Width);
    }

    [Fact]
    public void Iou_of_identical_and_disjoint_boxes()
    {
        var a = new BoundingBox(0.1f, 0.1f, 0.5f, 0.5f);
        Assert.Equal(1f, a.IntersectionOverUnion(a), 5);
        Assert.Equal(0f, a.IntersectionOverUnion(new BoundingBox(0.6f, 0.6f, 0.9f, 0.9f)));
        Assert.Equal(1f / 7f, a.IntersectionOverUnion(new BoundingBox(0.3f, 0.3f, 0.7f, 0.7f)), 4);
    }
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DefaultAppPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pf-paths-test");

    [Fact]
    public void All_locations_live_under_the_data_root_by_default()
    {
        var paths = new DefaultAppPaths(Root);
        string[] all =
        [
            paths.ConfigDirectory, paths.ProfilesDirectory, paths.CatalogPath, paths.BackupsDirectory, paths.ModelsDirectory, paths.GpuRuntimeDirectory,
            paths.CacheDirectory, paths.ThumbnailsDirectory, paths.LogsDirectory, paths.TempDirectory,
        ];
        Assert.All(all, p => Assert.StartsWith(Root, p, StringComparison.Ordinal));
        Assert.EndsWith(".pfdb", paths.CatalogPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_and_models_locations_are_configurable()
    {
        var catalog = Path.Combine(Path.GetTempPath(), "elsewhere", "my.pfdb");
        var models = Path.Combine(Path.GetTempPath(), "models-elsewhere");
        var paths = new DefaultAppPaths(Root, new AppPathOverrides(catalog, models));
        Assert.Equal(catalog, paths.CatalogPath);
        Assert.Equal(models, paths.ModelsDirectory);
    }

    [Fact]
    public void Relative_roots_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new DefaultAppPaths("relative/root"));
}
