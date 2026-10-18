"""Piccolo aiuto per costruire grafi ONNX a partire da pesi numpy."""

from __future__ import annotations

from typing import Any

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

OPSET = 17


class GraphBuilder:
    def __init__(self) -> None:
        self.nodes: list[onnx.NodeProto] = []
        self.initializers: list[onnx.TensorProto] = []
        self._count = 0

    def _name(self, hint: str) -> str:
        self._count += 1
        return f"{hint}_{self._count}"

    # ------------------------------------------------------------------ elementi base

    def const(self, value: np.ndarray | list | float | int, hint: str = "c", dtype=np.float32) -> str:
        name = self._name(hint)
        self.initializers.append(numpy_helper.from_array(np.asarray(value, dtype=dtype), name))
        return name

    def ints(self, values: list[int], hint: str = "shape") -> str:
        return self.const(values, hint, dtype=np.int64)

    def op(self, op_type: str, inputs: list[str], n_out: int = 1, **attrs: Any) -> Any:
        outputs = [self._name(op_type.lower()) for _ in range(n_out)]
        self.nodes.append(helper.make_node(op_type, inputs, outputs, **attrs))
        return outputs[0] if n_out == 1 else outputs

    # ------------------------------------------------------------------ operazioni comuni

    def conv(
        self, x: str, weight: np.ndarray, bias: np.ndarray | None = None, stride: int = 1, pad: int = 0, groups: int = 1
    ) -> str:
        inputs = [x, self.const(weight, "w")]
        if bias is not None:
            inputs.append(self.const(bias, "b"))
        k = weight.shape[2]
        return self.op("Conv", inputs, kernel_shape=[k, k], strides=[stride, stride], pads=[pad] * 4, group=groups)

    def conv_transpose(self, x: str, weight: np.ndarray, stride: int) -> str:
        k = weight.shape[2]
        return self.op("ConvTranspose", [x, self.const(weight, "w")], kernel_shape=[k, k], strides=[stride, stride])

    def linear(self, x: str, weight: np.ndarray, bias: np.ndarray | None) -> str:
        """Come torch.nn.Linear sull'ultima dimensione."""
        y = self.op("MatMul", [x, self.const(np.ascontiguousarray(weight.T), "w")])
        return self.op("Add", [y, self.const(bias, "b")]) if bias is not None else y

    def relu(self, x: str) -> str:
        return self.op("Relu", [x])

    def add(self, a: str, b: str) -> str:
        return self.op("Add", [a, b])

    def mul(self, a: str, b: str) -> str:
        return self.op("Mul", [a, b])

    def gelu(self, x: str) -> str:
        """GELU esatta: 0.5 * x * (1 + erf(x / sqrt(2)))."""
        scaled = self.op("Div", [x, self.const(np.sqrt(2.0), "sqrt2")])
        erf = self.op("Erf", [scaled])
        one_plus = self.op("Add", [erf, self.const(1.0, "one")])
        return self.op("Mul", [self.op("Mul", [x, one_plus]), self.const(0.5, "half")])

    def layer_norm(self, x: str, weight: np.ndarray, bias: np.ndarray, eps: float = 1e-5) -> str:
        return self.op(
            "LayerNormalization", [x, self.const(weight, "ln_w"), self.const(bias, "ln_b")], axis=-1, epsilon=eps
        )

    def reshape(self, x: str, shape: list[int]) -> str:
        return self.op("Reshape", [x, self.ints(shape)])

    def transpose(self, x: str, perm: list[int]) -> str:
        return self.op("Transpose", [x], perm=perm)

    def slice(self, x: str, start: int, end: int, axis: int) -> str:
        return self.op("Slice", [x, self.ints([start]), self.ints([end]), self.ints([axis])])

    def roll(self, x: str, shift: int, axis: int, size: int) -> str:
        """Come torch.roll su un asse di dimensione nota."""
        shift %= size
        if shift == 0:
            return x
        tail = self.slice(x, size - shift, size, axis)
        head = self.slice(x, 0, size - shift, axis)
        return self.op("Concat", [tail, head], axis=axis)

    # ------------------------------------------------------------------ modello

    def build(self, inputs: list[tuple[str, list]], output: str, name: str) -> onnx.ModelProto:
        graph = helper.make_graph(
            self.nodes,
            name,
            [helper.make_tensor_value_info(n, TensorProto.FLOAT, shape) for n, shape in inputs],
            [helper.make_tensor_value_info(output, TensorProto.FLOAT, ["n", "c", "h", "w"])],
            initializer=self.initializers,
        )
        model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", OPSET)], producer_name="denoiser")
        model.ir_version = 8
        return model
