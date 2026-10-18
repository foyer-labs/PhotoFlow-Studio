// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Inference;

/// <summary>Inference backend (CUDA / DirectML / CoreML / CPU). Only PhotoFlow.Inference depends on ONNX Runtime.</summary>
public interface IInferenceProvider
{
    HardwareInfo Detected { get; }

    Task<IInferenceSession> CreateSessionAsync(ModelDescriptor model, ExecutionTarget target, CancellationToken ct);
}

/// <summary>A loaded model. Disposing it releases the model memory (models are loaded stage by stage).</summary>
public interface IInferenceSession : IDisposable
{
    ModelDescriptor Model { get; }

    ExecutionBackend Backend { get; }

    /// <summary>Custom metadata of the model file (e.g. the dictionary of an OCR recognizer).</summary>
    IReadOnlyDictionary<string, string> Metadata { get; }

    Task<IReadOnlyList<NamedTensor>> RunAsync(IReadOnlyList<NamedTensor> inputs, CancellationToken ct);

    /// <summary>Only the named outputs, in that order (a decoder step does not need its unchanged cache back).</summary>
    Task<IReadOnlyList<NamedTensor>> RunAsync(IReadOnlyList<NamedTensor> inputs, IReadOnlyList<string> outputs, CancellationToken ct);

    /// <summary>Names of the model inputs.</summary>
    IReadOnlyList<string> InputNames { get; }

    /// <summary>Names of the model outputs.</summary>
    IReadOnlyList<string> OutputNames { get; }
}

public enum ExecutionTarget
{
    /// <summary>Best available backend for the detected hardware.</summary>
    Auto,
    Cpu,
    Gpu,
}

public enum ExecutionBackend
{
    Cpu,
    Cuda,
    DirectML,
    CoreML,
}

public sealed record HardwareInfo(
    IReadOnlyList<ExecutionBackend> AvailableBackends,
    int LogicalProcessorCount,
    string? GpuName,
    long? GpuMemoryBytes);

public enum ModelFunction
{
    ObjectDetection,
    Faces,
    Embedding,
    Tagger,
    Caption,
    Vlm,
    Ocr,
    Aesthetic,

    /// <summary>A face recognizer alone (fingerprints), chosen apart from the face detector (P22).</summary>
    FaceRecognition,
}

/// <summary>A model resolved to a local, SHA-256 verified file.</summary>
public sealed record ModelDescriptor(
    string Id,
    ModelFunction Function,
    string Name,
    string Version,
    string FilePath,
    string Sha256,
    string AdapterId);
