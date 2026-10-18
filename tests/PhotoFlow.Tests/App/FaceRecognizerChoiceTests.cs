// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Paths;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The recognizer of the people, chosen apart from the face detector (P22): the default follows the detector; AdaFace
/// is used only once converted, chosen and with its license accepted by the user; its fingerprints carry their own
/// model and thresholds.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class FaceRecognizerChoiceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-recognizer-choice");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdaFace_is_used_only_when_converted_chosen_and_accepted()
    {
        await using var host = await PhotoFlowHost.StartAsync(new DefaultAppPaths(_temp.Path), Ct);
        Assert.Null(host.DescribeAnalysis().FaceRecognizer);

        MarkInstalled(host, "opencv-yunet-2023mar");
        MarkInstalled(host, PhotoFlowHost.SFaceModelId);
        Assert.Equal(("opencv-yunet-2023mar", PhotoFlowHost.SFaceModelId), (host.DescribeAnalysis().FaceDetector, host.DescribeAnalysis().FaceRecognizer));

        // Chosen before it exists, then converted: its license is not accepted yet.
        await host.ChooseModelAsync(ModelFunction.FaceRecognition, PhotoFlowHost.AdaFaceModelId, Ct);
        Assert.Equal(PhotoFlowHost.SFaceModelId, host.DescribeAnalysis().FaceRecognizer);
        var folder = host.Models.InstallDirectory(PhotoFlowHost.AdaFaceModelId);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, AdaFaceEmbedder.File), [1]);
        await host.Models.MarkConvertedAsync(PhotoFlowHost.AdaFaceModelId, Ct);
        Assert.False(host.ModelUsable(PhotoFlowHost.AdaFaceModelId));
        Assert.Equal(PhotoFlowHost.SFaceModelId, host.DescribeAnalysis().FaceRecognizer);

        await host.UpdateSettingsAsync(s => s with { AcceptedModelLicenses = [.. s.AcceptedModelLicenses, PhotoFlowHost.AdaFaceModelId] }, Ct);
        Assert.Equal(PhotoFlowHost.AdaFaceModelId, host.DescribeAnalysis().FaceRecognizer);
        Assert.Equal("opencv-yunet-2023mar", host.DescribeAnalysis().FaceDetector); // the detector does not change
        Assert.Equal(AdaFaceEmbedder.File, PhotoFlowHost.RecognizerFile(PhotoFlowHost.AdaFaceModelId));

        // Back to SFace by the user's choice.
        await host.ChooseModelAsync(ModelFunction.FaceRecognition, PhotoFlowHost.SFaceModelId, Ct);
        Assert.Equal(PhotoFlowHost.SFaceModelId, host.DescribeAnalysis().FaceRecognizer);
    }

    [Fact]
    public void Each_recognizer_has_its_own_threshold()
    {
        Assert.Equal(FaceMatching.AdaFaceThreshold, FaceMatching.Threshold("adaface-ir101-webface12m@x/adaface"));
        Assert.Equal(FaceMatching.SFaceThreshold, FaceMatching.Threshold("opencv-sface-2021dec@x/sface"));
        Assert.Equal(FaceMatching.ArcFaceThreshold, FaceMatching.Threshold("insightface-buffalo-l@x/arcface"));
        Assert.True(FaceMatching.AdaFaceThreshold < FaceMatching.ArcFaceThreshold);
    }

    public void Dispose() => _temp.Dispose();

    private static void MarkInstalled(PhotoFlowHost host, string id)
    {
        var model = host.Models.Get(id);
        var folder = host.Models.InstallDirectory(id);
        Directory.CreateDirectory(folder);
        foreach (var file in model.Files.SelectMany(f => f.Use.Count > 0 ? f.Use : [f.Path]))
        {
            File.WriteAllBytes(Path.Combine(folder, file), [1]);
        }

        File.WriteAllText(Path.Combine(folder, ".installed"), model.Version);
    }
}
