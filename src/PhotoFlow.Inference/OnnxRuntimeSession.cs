// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using PhotoFlow.Core.Inference;
using OrtElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;
using TensorElementType = PhotoFlow.Core.Inference.TensorElementType;

namespace PhotoFlow.Inference;

internal sealed class OnnxRuntimeSession : IInferenceSession
{
    private readonly InferenceSession _session;

    public OnnxRuntimeSession(ModelDescriptor model, ExecutionBackend backend, InferenceSession session)
    {
        Model = model;
        Backend = backend;
        _session = session;
    }

    public ModelDescriptor Model { get; }

    public ExecutionBackend Backend { get; }

    public IReadOnlyDictionary<string, string> Metadata => _session.ModelMetadata.CustomMetadataMap;

    public IReadOnlyList<string> InputNames => _session.InputNames;

    public IReadOnlyList<string> OutputNames => _session.OutputNames;

    public Task<IReadOnlyList<NamedTensor>> RunAsync(IReadOnlyList<NamedTensor> inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return Task.Run(() => Run(inputs, _session.OutputNames, ct), ct);
    }

    public Task<IReadOnlyList<NamedTensor>> RunAsync(IReadOnlyList<NamedTensor> inputs, IReadOnlyList<string> outputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        return Task.Run(() => Run(inputs, outputs, ct), ct);
    }

    public void Dispose() => _session.Dispose();

    private IReadOnlyList<NamedTensor> Run(IReadOnlyList<NamedTensor> inputs, IReadOnlyList<string> outputNames, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inputValues = new List<OrtValue>(inputs.Count);
        try
        {
            foreach (var tensor in inputs)
            {
                inputValues.Add(ToOrtValue(tensor));
            }

            using var runOptions = new RunOptions();
            using var registration = ct.Register(() => runOptions.Terminate = true);
            var inputNames = inputs.Select(t => t.Name).ToArray();
            using var outputs = _session.Run(runOptions, inputNames, inputValues, outputNames);
            ct.ThrowIfCancellationRequested();

            var results = new List<NamedTensor>(outputs.Count);
            for (var i = 0; i < outputs.Count; i++)
            {
                results.Add(FromOrtValue(outputNames[i], outputs[i]));
            }

            return results;
        }
        finally
        {
            foreach (var value in inputValues)
            {
                value.Dispose();
            }
        }
    }

    private static OrtValue ToOrtValue(NamedTensor tensor)
    {
        var shape = tensor.Shape.Select(d => (long)d).ToArray();

        // ONNX Runtime does not write to input buffers: viewing the read-only memory as writable avoids a copy.
        return tensor.ElementType switch
        {
            TensorElementType.Float32 => OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(tensor.AsFloat32()), shape),
            TensorElementType.Int64 => OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(tensor.AsInt64()), shape),
            TensorElementType.UInt8 => OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(tensor.AsUInt8()), shape),
            TensorElementType.Bool => OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(tensor.AsBool()), shape),
            _ => throw new NotSupportedException($"Tensor element type {tensor.ElementType} is not supported."),
        };
    }

    private static NamedTensor FromOrtValue(string name, OrtValue value)
    {
        var info = value.GetTensorTypeAndShape();
        var shape = info.Shape.Select(d => checked((int)d)).ToArray();
        return info.ElementDataType switch
        {
            OrtElementType.Float => NamedTensor.Float32(name, value.GetTensorDataAsSpan<float>().ToArray(), shape),
            OrtElementType.Int64 => NamedTensor.Int64(name, value.GetTensorDataAsSpan<long>().ToArray(), shape),
            OrtElementType.UInt8 => NamedTensor.UInt8(name, value.GetTensorDataAsSpan<byte>().ToArray(), shape),
            OrtElementType.Bool => NamedTensor.Bool(name, value.GetTensorDataAsSpan<bool>().ToArray(), shape),
            _ => throw new NotSupportedException($"Output '{name}' has unsupported element type {info.ElementDataType}."),
        };
    }
}
