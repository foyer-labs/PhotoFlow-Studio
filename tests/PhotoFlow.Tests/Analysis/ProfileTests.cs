// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ProfileTests
{
    [Fact]
    public void The_built_in_profiles_of_the_spec_are_embedded()
    {
        Assert.Equal(["events", "portrait", "motorsport", "animals", "landscape", "night", "archive"], BuiltInProfiles.All.Select(p => p.Id));
        Assert.All(BuiltInProfiles.All, p => Assert.True(p.IsBuiltIn));

        var events = BuiltInProfiles.Get("events");
        Assert.Equal([SharpnessZoneKind.AfArea, SharpnessZoneKind.Faces], events.SharpnessZones);
        Assert.Equal(FaceMode.Recognize, events.Faces);

        var motorsport = BuiltInProfiles.Get("motorsport");
        Assert.Equal(ExposureMode.Tolerant, motorsport.Exposure);
        Assert.Equal(["car", "motorcycle"], motorsport.SubjectClasses);
        Assert.True(motorsport.Ocr);

        Assert.False(BuiltInProfiles.Get("night").AbsoluteSharpness);
        Assert.False(BuiltInProfiles.Get("archive").CullingEnabled);
    }

    [Fact]
    public void Thresholds_are_consistent()
    {
        Assert.All(BuiltInProfiles.All, p =>
        {
            var t = p.Thresholds;
            Assert.True(t.BlurDiscard < t.BlurReview, p.Id);
            Assert.True(t.EyesClosed < t.EyesOpen, p.Id);
            Assert.True(t.EyesMinFaceSize <= t.EyesDiscardFaceSize, p.Id);
        });
    }

    [Fact]
    public void Invalid_documents_are_rejected()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryRoot(), "profiles", "events.json"));
        Assert.Equal(BuiltInProfiles.Get("events") with { IsBuiltIn = false }, BuiltInProfiles.Parse(json, isBuiltIn: false),
            new ProfileComparer());
        Assert.Throws<InvalidDataException>(() => BuiltInProfiles.Parse(json.Replace("\"sharpness\": 35", "\"sharpness\": 36", StringComparison.Ordinal), false));
        Assert.Throws<InvalidDataException>(() => BuiltInProfiles.Parse(json.Replace("\"schema\": 1", "\"schema\": 2", StringComparison.Ordinal), false));
        Assert.ThrowsAny<Exception>(() => BuiltInProfiles.Parse(json.Replace("\"ocr\"", "\"ocrx\"", StringComparison.Ordinal), false));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "PhotoFlow.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    /// <summary>Records with list members compare lists by reference: compare the serialized shape instead.</summary>
    private sealed class ProfileComparer : IEqualityComparer<SceneProfile>
    {
        public bool Equals(SceneProfile? x, SceneProfile? y) =>
            x is not null && y is not null
            && x with { SharpnessZones = Array.Empty<SharpnessZoneKind>(), SubjectClasses = Array.Empty<string>() } == y with { SharpnessZones = Array.Empty<SharpnessZoneKind>(), SubjectClasses = Array.Empty<string>() }
            && x.SharpnessZones.SequenceEqual(y.SharpnessZones) && x.SubjectClasses.SequenceEqual(y.SubjectClasses);

        public int GetHashCode(SceneProfile obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
