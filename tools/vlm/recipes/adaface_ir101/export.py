# SPDX-License-Identifier: Apache-2.0
"""AdaFace IR-101 (WebFace12M, CVLFace checkpoint) to ONNX for PhotoFlow's face fingerprints (P22).

The network is the IR-101 of the AdaFace paper (arXiv 2204.00964): improved residual units (BN, 3x3 conv, BN, PReLU,
3x3 conv, BN) in stages of 3, 13, 30 and 3 units, then BN, flatten, linear to 512 and a BatchNorm without affine. It
is written here (not the checkpoint's own code) with the parameter names of the checkpoint, loaded strictly. Input: the
aligned 112x112 face, RGB, (x - 127.5) / 127.5, NCHW; output: 512 values (PhotoFlow normalizes them).

The exported model must give the PyTorch embeddings (cosine >= 0.9999 on random faces) or the export stops.
"""
from __future__ import annotations

from pathlib import Path

import torch
from torch import nn

OUTPUT = "adaface_ir101.onnx"


class BasicBlockIR(nn.Module):
    def __init__(self, in_channel: int, depth: int, stride: int):
        super().__init__()
        if in_channel == depth:
            self.shortcut_layer = nn.MaxPool2d(1, stride)
        else:
            self.shortcut_layer = nn.Sequential(nn.Conv2d(in_channel, depth, (1, 1), stride, bias=False), nn.BatchNorm2d(depth))
        self.res_layer = nn.Sequential(
            nn.BatchNorm2d(in_channel),
            nn.Conv2d(in_channel, depth, (3, 3), (1, 1), 1, bias=False),
            nn.BatchNorm2d(depth),
            nn.PReLU(depth),
            nn.Conv2d(depth, depth, (3, 3), stride, 1, bias=False),
            nn.BatchNorm2d(depth))

    def forward(self, x):
        return self.res_layer(x) + self.shortcut_layer(x)


class Flatten(nn.Module):
    def forward(self, x):
        return x.view(x.size(0), -1)


class IR101(nn.Module):
    STAGES = ((64, 64, 3), (64, 128, 13), (128, 256, 30), (256, 512, 3))

    def __init__(self, output_dim: int = 512):
        super().__init__()
        self.input_layer = nn.Sequential(nn.Conv2d(3, 64, (3, 3), 1, 1, bias=False), nn.BatchNorm2d(64), nn.PReLU(64))
        units = []
        for in_channel, depth, count in self.STAGES:
            units.append(BasicBlockIR(in_channel, depth, 2))
            units += [BasicBlockIR(depth, depth, 1) for _ in range(count - 1)]
        self.body = nn.Sequential(*units)
        self.output_layer = nn.Sequential(nn.BatchNorm2d(512), nn.Dropout(0.4), Flatten(), nn.Linear(512 * 7 * 7, output_dim),
                                          nn.BatchNorm1d(output_dim, affine=False))

    def forward(self, x):
        return self.output_layer(self.body(self.input_layer(x)))


def load(weights: Path) -> IR101:
    from safetensors.torch import load_file

    tensors = load_file(str(weights / "model.safetensors"))
    prefix = next(p for p in ("model.net.", "net.", "") if f"{p}input_layer.0.weight" in tensors)
    model = IR101()
    model.load_state_dict({k[len(prefix):]: v for k, v in tensors.items() if k.startswith(prefix)}, strict=True)
    return model.eval()


def export(weights: Path, output: Path, progress) -> None:
    import numpy as np
    import onnxruntime as ort

    progress(1, 3, "load")
    model = load(weights)
    progress(2, 3, "export")
    output.mkdir(parents=True, exist_ok=True)
    target = output / OUTPUT
    example = torch.zeros(1, 3, 112, 112)
    torch.onnx.export(model, example, str(target), input_names=["input"], output_names=["embedding"], opset_version=17,
                      dynamic_axes={"input": {0: "batch"}, "embedding": {0: "batch"}}, dynamo=False)

    progress(3, 3, "check")
    faces = torch.rand(4, 3, 112, 112) * 2 - 1
    with torch.inference_mode():
        expected = model(faces).numpy()
    session = ort.InferenceSession(str(target), providers=["CPUExecutionProvider"])
    actual = session.run(None, {"input": faces.numpy()})[0]
    cosine = np.sum(expected * actual, axis=1) / (np.linalg.norm(expected, axis=1) * np.linalg.norm(actual, axis=1))
    if float(cosine.min()) < 0.9999:
        raise RuntimeError(f"The exported model does not match PyTorch (cosine {cosine.min():.6f}).")
