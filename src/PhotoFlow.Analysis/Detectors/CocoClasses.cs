// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Analysis.Detectors;

/// <summary>The 80 COCO classes in the order used by the D-FINE checkpoints (config.json id2label).</summary>
public static class CocoClasses
{
    private static readonly string[] Names =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
        "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
        "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
        "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
        "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
        "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone",
        "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear",
        "hair drier", "toothbrush",
    ];

    public static int Count => Names.Length;

    public static string NameOf(int classId) =>
        classId >= 0 && classId < Names.Length ? Names[classId] : $"class_{classId}";
}
