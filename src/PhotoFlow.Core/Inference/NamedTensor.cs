// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Inference;

public enum TensorElementType
{
    Float32,
    Int64,
    UInt8,
    Bool,
}

/// <summary>
/// Backend-neutral dense tensor (row-major). Lets model adapters live outside PhotoFlow.Inference
/// without depending on ONNX Runtime types.
/// </summary>
public sealed class NamedTensor
{
    private readonly ReadOnlyMemory<float> _float32;
    private readonly ReadOnlyMemory<long> _int64;
    private readonly ReadOnlyMemory<byte> _uint8;
    private readonly ReadOnlyMemory<bool> _bool;
    private readonly int[] _shape;

    private NamedTensor(string name, TensorElementType elementType, int[] shape, int dataLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Length == 0)
        {
            throw new ArgumentException("A tensor needs at least one dimension.", nameof(shape));
        }

        long count = 1;
        foreach (var dim in shape)
        {
            // A dimension of 0 is valid: e.g. the empty cache of a decoder at its first step.
            if (dim < 0)
            {
                throw new ArgumentException($"Tensor '{name}' has a negative dimension ({dim}).", nameof(shape));
            }

            count = checked(count * dim);
        }

        if (count != dataLength)
        {
            throw new ArgumentException(
                $"Tensor '{name}': shape [{string.Join(", ", shape)}] needs {count} elements, data has {dataLength}.",
                nameof(shape));
        }

        Name = name;
        ElementType = elementType;
        _shape = (int[])shape.Clone();
        ElementCount = count;
    }

    private NamedTensor(string name, ReadOnlyMemory<float> data, int[] shape)
        : this(name, TensorElementType.Float32, shape, data.Length) => _float32 = data;

    private NamedTensor(string name, ReadOnlyMemory<long> data, int[] shape)
        : this(name, TensorElementType.Int64, shape, data.Length) => _int64 = data;

    private NamedTensor(string name, ReadOnlyMemory<byte> data, int[] shape)
        : this(name, TensorElementType.UInt8, shape, data.Length) => _uint8 = data;

    private NamedTensor(string name, ReadOnlyMemory<bool> data, int[] shape)
        : this(name, TensorElementType.Bool, shape, data.Length) => _bool = data;

    public string Name { get; }

    public TensorElementType ElementType { get; }

    public IReadOnlyList<int> Shape => _shape;

    public long ElementCount { get; }

    public static NamedTensor Float32(string name, ReadOnlyMemory<float> data, params int[] shape) => new(name, data, shape);

    public static NamedTensor Int64(string name, ReadOnlyMemory<long> data, params int[] shape) => new(name, data, shape);

    public static NamedTensor UInt8(string name, ReadOnlyMemory<byte> data, params int[] shape) => new(name, data, shape);

    public static NamedTensor Bool(string name, ReadOnlyMemory<bool> data, params int[] shape) => new(name, data, shape);

    public ReadOnlyMemory<float> AsFloat32() => ElementType == TensorElementType.Float32 ? _float32 : throw WrongType(TensorElementType.Float32);

    public ReadOnlyMemory<long> AsInt64() => ElementType == TensorElementType.Int64 ? _int64 : throw WrongType(TensorElementType.Int64);

    public ReadOnlyMemory<byte> AsUInt8() => ElementType == TensorElementType.UInt8 ? _uint8 : throw WrongType(TensorElementType.UInt8);

    public ReadOnlyMemory<bool> AsBool() => ElementType == TensorElementType.Bool ? _bool : throw WrongType(TensorElementType.Bool);

    private InvalidOperationException WrongType(TensorElementType requested) =>
        new($"Tensor '{Name}' is {ElementType}, not {requested}.");
}
