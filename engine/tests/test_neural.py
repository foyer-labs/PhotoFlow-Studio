"""Test del motore neurale senza PyTorch e senza i pesi reali.

- I file .pth vengono generati imitando il formato di PyTorch (nuovo e vecchio).
- Le reti ricostruite in ONNX vengono confrontate con implementazioni di riferimento in numpy
  che seguono riga per riga il codice originale.
- Se la variabile DENOISER_TEST_WEIGHTS indica una cartella con i pesi veri, si verifica anche
  la qualità del denoise.
"""

from __future__ import annotations

import io
import os
import pickle
import struct
import sys
import types
import zipfile
from collections import OrderedDict
from pathlib import Path

import numpy as np
import onnxruntime as ort
import pytest

from denoiser.neural.architectures import (
    build_drunet,
    build_restormer,
    relative_position_bias,
    shifted_window_mask,
    swin_block,
)
from denoiser.neural.checkpoint import CheckpointError, load_state_dict
from denoiser.neural.graph import GraphBuilder
from denoiser.neural.tiling import run_tiled

# ----------------------------------------------------------------------------- .pth finti


class _StorageRef:
    def __init__(self, key: str, numel: int):
        self.key, self.numel = key, numel


def _fake_torch(monkeypatch):
    torch = types.ModuleType("torch")
    utils = types.ModuleType("torch._utils")

    class FloatStorage:
        pass

    FloatStorage.__module__ = "torch"
    FloatStorage.__qualname__ = "FloatStorage"

    def _rebuild_tensor_v2(*args):
        raise AssertionError("non usata")

    _rebuild_tensor_v2.__module__ = "torch._utils"
    _rebuild_tensor_v2.__qualname__ = "_rebuild_tensor_v2"
    torch.FloatStorage = FloatStorage
    utils._rebuild_tensor_v2 = _rebuild_tensor_v2
    torch._utils = utils
    monkeypatch.setitem(sys.modules, "torch", torch)
    monkeypatch.setitem(sys.modules, "torch._utils", utils)
    return FloatStorage, _rebuild_tensor_v2


def write_fake_pth(path: Path, state: dict[str, np.ndarray], monkeypatch, legacy: bool = False) -> None:
    storage_cls, rebuild = _fake_torch(monkeypatch)

    class _Tensor:
        def __init__(self, key: str, arr: np.ndarray):
            self.key, self.arr = key, arr

        def __reduce__(self):
            strides = tuple(s // self.arr.itemsize for s in self.arr.strides)
            ref = _StorageRef(self.key, self.arr.size)
            return rebuild, (ref, 0, self.arr.shape, strides, False, OrderedDict())

    class _Pickler(pickle.Pickler):
        def persistent_id(self, obj):
            if isinstance(obj, _StorageRef):
                pid = ("storage", storage_cls, obj.key, "cpu", obj.numel)
                return pid + (None,) if legacy else pid
            return None

    arrays = {str(i): np.ascontiguousarray(v, dtype=np.float32) for i, v in enumerate(state.values())}
    payload = OrderedDict((k, _Tensor(str(i), arrays[str(i)])) for i, k in enumerate(state))
    buf = io.BytesIO()
    _Pickler(buf, protocol=2).dump(payload)
    if legacy:
        with open(path, "wb") as f:
            pickle.dump(0x1950A86A20F9469CFC6C, f, protocol=2)
            pickle.dump(1001, f, protocol=2)
            pickle.dump({"protocol_version": 1001, "little_endian": True}, f, protocol=2)
            f.write(buf.getvalue())
            pickle.dump(list(arrays), f, protocol=2)
            for arr in arrays.values():
                f.write(struct.pack("<q", arr.size))
                f.write(arr.tobytes())
    else:
        with zipfile.ZipFile(path, "w") as zf:
            zf.writestr("archive/data.pkl", buf.getvalue())
            for key, arr in arrays.items():
                zf.writestr(f"archive/data/{key}", arr.tobytes())


@pytest.mark.parametrize("legacy", [False, True])
def test_checkpoint_roundtrip(tmp_path, monkeypatch, legacy):
    rng = np.random.default_rng(0)
    state = OrderedDict(
        [("a.weight", rng.random((4, 3, 3, 3))), ("b.bias", rng.random(7)), ("c", rng.random((2, 5)).T)]
    )
    path = tmp_path / "pesi.pth"
    write_fake_pth(path, state, monkeypatch, legacy=legacy)
    loaded = load_state_dict(path)
    assert list(loaded) == list(state)
    for k in state:
        np.testing.assert_allclose(loaded[k], state[k].astype(np.float32))


def test_checkpoint_rejects_arbitrary_code(tmp_path):
    class Evil:
        def __reduce__(self):
            return (os.system, ("echo pericolo",))

    path = tmp_path / "cattivo.pth"
    with zipfile.ZipFile(path, "w") as zf:
        zf.writestr("archive/data.pkl", pickle.dumps(Evil(), protocol=2))
    with pytest.raises(CheckpointError):
        load_state_dict(path)


# ----------------------------------------------------------------------------- riferimenti numpy


def conv2d(x, w, b=None, stride=1, pad=0):
    x = np.pad(x, ((0, 0), (0, 0), (pad, pad), (pad, pad)))
    k = w.shape[2]
    oh = (x.shape[2] - k) // stride + 1
    ow = (x.shape[3] - k) // stride + 1
    out = np.zeros((x.shape[0], w.shape[0], oh, ow))
    for i in range(k):
        for j in range(k):
            patch = x[:, :, i : i + stride * oh : stride, j : j + stride * ow : stride]
            out += np.einsum("nchw,oc->nohw", patch, w[:, :, i, j])
    if b is not None:
        out += b[None, :, None, None]
    return out


def conv_transpose2(x, w):
    """ConvTranspose2d con kernel 2 e passo 2 (blocchi che non si sovrappongono)."""
    n, _, h, wd = x.shape
    out = np.zeros((n, w.shape[1], 2 * h, 2 * wd))
    for i in range(2):
        for j in range(2):
            out[:, :, i::2, j::2] = np.einsum("nchw,co->nohw", x, w[:, :, i, j])
    return out


def drunet_reference(sd, x0):
    relu = lambda v: np.maximum(v, 0)  # noqa: E731

    def res(x, p):
        return x + conv2d(relu(conv2d(x, sd[f"{p}.res.0.weight"], pad=1)), sd[f"{p}.res.2.weight"], pad=1)

    def down(x, name, nb):
        for i in range(nb):
            x = res(x, f"{name}.{i}")
        return conv2d(x, sd[f"{name}.{nb}.weight"], stride=2)

    def up(x, name, nb):
        x = conv_transpose2(x, sd[f"{name}.0.weight"])
        for i in range(1, nb + 1):
            x = res(x, f"{name}.{i}")
        return x

    x1 = conv2d(x0, sd["m_head.weight"], pad=1)
    x2 = down(x1, "m_down1", 1)
    x3 = down(x2, "m_down2", 1)
    x4 = down(x3, "m_down3", 1)
    x = res(x4, "m_body.0")
    x = up(x + x4, "m_up3", 1)
    x = up(x + x3, "m_up2", 1)
    x = up(x + x2, "m_up1", 1)
    return conv2d(x + x1, sd["m_tail.weight"], pad=1)


def tiny_drunet_weights(rng):
    nc = [8, 16, 32, 64]
    sd = OrderedDict()

    def w(o, i, k=3):
        return (rng.standard_normal((o, i, k, k)) * np.sqrt(1 / (i * k * k))).astype(np.float32)

    sd["m_head.weight"] = w(nc[0], 4)
    for level, name in enumerate(["m_down1", "m_down2", "m_down3"]):
        sd[f"{name}.0.res.0.weight"] = w(nc[level], nc[level])
        sd[f"{name}.0.res.2.weight"] = w(nc[level], nc[level])
        sd[f"{name}.1.weight"] = w(nc[level + 1], nc[level], 2)
    sd["m_body.0.res.0.weight"] = w(nc[3], nc[3])
    sd["m_body.0.res.2.weight"] = w(nc[3], nc[3])
    for level, name in zip([2, 1, 0], ["m_up3", "m_up2", "m_up1"]):
        sd[f"{name}.0.weight"] = (rng.standard_normal((nc[level + 1], nc[level], 2, 2)) * 0.2).astype(np.float32)
        sd[f"{name}.1.res.0.weight"] = w(nc[level], nc[level])
        sd[f"{name}.1.res.2.weight"] = w(nc[level], nc[level])
    sd["m_tail.weight"] = w(3, nc[0])
    return sd


def _run(model, x):
    sess = ort.InferenceSession(model.SerializeToString(), providers=["CPUExecutionProvider"])
    return sess.run(None, {"input": x.astype(np.float32)})[0]


def test_drunet_graph_matches_reference():
    rng = np.random.default_rng(1)
    sd = tiny_drunet_weights(rng)
    x = rng.random((1, 4, 16, 24)).astype(np.float32)
    np.testing.assert_allclose(_run(build_drunet(sd), x), drunet_reference(sd, x), rtol=1e-4, atol=1e-4)


def wmsa_reference(x, sd, prefix, shifted, p=8, hd=32):
    """Come WMSA.forward del codice originale (einops tradotto in numpy)."""
    b, h, w, c = x.shape
    nh = c // hd
    if shifted:
        x = np.roll(x, (-(p // 2), -(p // 2)), axis=(1, 2))
    w1, w2 = h // p, w // p
    x = x.reshape(b, w1, p, w2, p, c).transpose(0, 1, 3, 2, 4, 5).reshape(b, w1 * w2, p * p, c)
    qkv = x @ sd[f"{prefix}.embedding_layer.weight"].T + sd[f"{prefix}.embedding_layer.bias"]
    qkv = qkv.reshape(b, w1 * w2, p * p, 3 * nh, hd).transpose(3, 0, 1, 2, 4)
    q, k, v = qkv[:nh], qkv[nh : 2 * nh], qkv[2 * nh :]
    sim = np.einsum("hbwpc,hbwqc->hbwpq", q, k) * hd**-0.5
    cord = np.array([[i, j] for i in range(p) for j in range(p)])
    rel = cord[:, None, :] - cord[None, :, :] + p - 1
    sim = sim + sd[f"{prefix}.relative_position_params"][:, rel[:, :, 0], rel[:, :, 1]][:, None, None]
    if shifted:
        s = p - p // 2
        mask = np.zeros((w1, w2, p, p, p, p), dtype=bool)
        mask[-1, :, :s, :, s:, :] = True
        mask[-1, :, s:, :, :s, :] = True
        mask[:, -1, :, :s, :, s:] = True
        mask[:, -1, :, s:, :, :s] = True
        sim = np.where(mask.reshape(1, 1, w1 * w2, p * p, p * p), -np.inf, sim)
    sim = sim - sim.max(-1, keepdims=True)
    probs = np.exp(sim) / np.exp(sim).sum(-1, keepdims=True)
    out = np.einsum("hbwij,hbwjc->hbwic", probs, v)
    out = out.transpose(1, 2, 3, 0, 4).reshape(b, w1 * w2, p * p, nh * hd)
    out = out @ sd[f"{prefix}.linear.weight"].T + sd[f"{prefix}.linear.bias"]
    out = out.reshape(b, w1, w2, p, p, c).transpose(0, 1, 3, 2, 4, 5).reshape(b, h, w, c)
    if shifted:
        out = np.roll(out, (p // 2, p // 2), axis=(1, 2))
    return out


def _layer_norm(x, w, b, eps=1e-5):
    mu = x.mean(-1, keepdims=True)
    var = x.var(-1, keepdims=True)
    return (x - mu) / np.sqrt(var + eps) * w + b


def block_reference(x, sd, prefix, shifted):
    from math import erf

    gelu = np.vectorize(lambda v: 0.5 * v * (1 + erf(v / np.sqrt(2))))
    x = x + wmsa_reference(_layer_norm(x, sd[f"{prefix}.ln1.weight"], sd[f"{prefix}.ln1.bias"]), sd, f"{prefix}.msa", shifted)
    y = _layer_norm(x, sd[f"{prefix}.ln2.weight"], sd[f"{prefix}.ln2.bias"])
    y = gelu(y @ sd[f"{prefix}.mlp.0.weight"].T + sd[f"{prefix}.mlp.0.bias"])
    return x + y @ sd[f"{prefix}.mlp.2.weight"].T + sd[f"{prefix}.mlp.2.bias"]


@pytest.mark.parametrize("shifted", [False, True])
def test_swin_block_matches_reference(shifted):
    rng = np.random.default_rng(2)
    c, h, w, heads = 64, 16, 24, 2

    def r(*shape, scale=0.2):
        return (rng.standard_normal(shape) * scale).astype(np.float32)

    p = "blk"
    sd = {
        f"{p}.ln1.weight": 1 + r(c), f"{p}.ln1.bias": r(c),
        f"{p}.ln2.weight": 1 + r(c), f"{p}.ln2.bias": r(c),
        f"{p}.msa.embedding_layer.weight": r(3 * c, c), f"{p}.msa.embedding_layer.bias": r(3 * c),
        f"{p}.msa.relative_position_params": r(heads, 15, 15, scale=1.0),
        f"{p}.msa.linear.weight": r(c, c), f"{p}.msa.linear.bias": r(c),
        f"{p}.mlp.0.weight": r(4 * c, c), f"{p}.mlp.0.bias": r(4 * c),
        f"{p}.mlp.2.weight": r(c, 4 * c), f"{p}.mlp.2.bias": r(c),
    }
    g = GraphBuilder()
    out = g.op("Identity", [swin_block(g, "input", sd, p, h, w, c, shifted)])
    model = g.build([("input", [1, h, w, c])], out, "blocco")
    x = rng.standard_normal((1, h, w, c)).astype(np.float32)
    np.testing.assert_allclose(_run(model, x), block_reference(x, sd, p, shifted), rtol=1e-3, atol=1e-3)


def test_relative_bias_and_mask_shapes():
    assert relative_position_bias(np.zeros((3, 15, 15))).shape == (3, 64, 64)
    mask = shifted_window_mask(2, 3)
    assert mask.shape == (1, 1, 6, 64, 64)
    assert mask[0, 0, 0].max() == 0  # la prima finestra non è al bordo: nessuna esclusione


# ----------------------------------------------------------------------------- tasselli


@pytest.mark.parametrize("shape", [(3, 100, 130), (3, 40, 50), (1, 300, 257)])
def test_tiling_identity_is_exact(shape):
    img = np.random.default_rng(3).random(shape).astype(np.float32)
    calls = []

    def fn(t):
        calls.append(t.shape)
        return t * 1.0

    out = run_tiled(fn, img, tile=64, overlap=8)
    assert out.shape == img.shape
    np.testing.assert_allclose(out, img, atol=1e-6)
    assert all(s[1:] == (64, 64) for s in calls)


# ----------------------------------------------------------------------------- Restormer


def _depthwise3(x, w):
    xp = np.pad(x, ((0, 0), (0, 0), (1, 1), (1, 1)))
    out = np.zeros_like(x, dtype=np.float64)
    for i in range(3):
        for j in range(3):
            out += xp[:, :, i : i + x.shape[2], j : j + x.shape[3]] * w[None, :, 0, i, j, None, None]
    return out


def _gelu(x):
    from math import erf

    return 0.5 * x * (1 + np.vectorize(erf)(x / np.sqrt(2)))


def restormer_reference(sd, x0, blocks=(1, 1, 1, 1), refinement=1):
    """Come Restormer.forward (basicsr/models/archs/restormer_arch.py), con LayerNorm BiasFree."""

    def norm(x, w):
        mu = x.mean(1, keepdims=True)
        var = ((x - mu) ** 2).mean(1, keepdims=True)
        return x / np.sqrt(var + 1e-5) * w[None, :, None, None]

    def block(x, p):
        n, c, h, w = x.shape
        heads = sd[f"{p}.attn.temperature"].shape[0]
        y = norm(x, sd[f"{p}.norm1.body.weight"])
        qkv = _depthwise3(conv2d(y, sd[f"{p}.attn.qkv.weight"]), sd[f"{p}.attn.qkv_dwconv.weight"])
        q, k, v = (a.reshape(heads, c // heads, h * w) for a in np.split(qkv[0], 3))
        q = q / np.maximum(np.linalg.norm(q, axis=-1, keepdims=True), 1e-12)
        k = k / np.maximum(np.linalg.norm(k, axis=-1, keepdims=True), 1e-12)
        attn = q @ k.transpose(0, 2, 1) * sd[f"{p}.attn.temperature"]
        attn = np.exp(attn - attn.max(-1, keepdims=True))
        attn /= attn.sum(-1, keepdims=True)
        out = (attn @ v).reshape(1, c, h, w)
        x = x + conv2d(out, sd[f"{p}.attn.project_out.weight"])
        y = norm(x, sd[f"{p}.norm2.body.weight"])
        y = _depthwise3(conv2d(y, sd[f"{p}.ffn.project_in.weight"]), sd[f"{p}.ffn.dwconv.weight"])
        a, b = np.split(y, 2, axis=1)
        return x + conv2d(_gelu(a) * b, sd[f"{p}.ffn.project_out.weight"])

    def level(x, name, count):
        for i in range(count):
            x = block(x, f"{name}.{i}")
        return x

    def unshuffle(x):  # torch.nn.PixelUnshuffle(2)
        n, c, h, w = x.shape
        return x.reshape(n, c, h // 2, 2, w // 2, 2).transpose(0, 1, 3, 5, 2, 4).reshape(n, c * 4, h // 2, w // 2)

    def shuffle(x):  # torch.nn.PixelShuffle(2)
        n, c, h, w = x.shape
        return x.reshape(n, c // 4, 2, 2, h, w).transpose(0, 1, 4, 2, 5, 3).reshape(n, c // 4, h * 2, w * 2)

    e1 = level(conv2d(x0, sd["patch_embed.proj.weight"], pad=1), "encoder_level1", blocks[0])
    e2 = level(unshuffle(conv2d(e1, sd["down1_2.body.0.weight"], pad=1)), "encoder_level2", blocks[1])
    e3 = level(unshuffle(conv2d(e2, sd["down2_3.body.0.weight"], pad=1)), "encoder_level3", blocks[2])
    lat = level(unshuffle(conv2d(e3, sd["down3_4.body.0.weight"], pad=1)), "latent", blocks[3])
    d3 = np.concatenate([shuffle(conv2d(lat, sd["up4_3.body.0.weight"], pad=1)), e3], 1)
    d3 = level(conv2d(d3, sd["reduce_chan_level3.weight"]), "decoder_level3", blocks[2])
    d2 = np.concatenate([shuffle(conv2d(d3, sd["up3_2.body.0.weight"], pad=1)), e2], 1)
    d2 = level(conv2d(d2, sd["reduce_chan_level2.weight"]), "decoder_level2", blocks[1])
    d1 = level(np.concatenate([shuffle(conv2d(d2, sd["up2_1.body.0.weight"], pad=1)), e1], 1), "decoder_level1", blocks[0])
    d1 = level(d1, "refinement", refinement)
    return conv2d(d1, sd["output.weight"], pad=1) + x0


def tiny_restormer_weights(rng, dim=8):
    sd = OrderedDict()

    def w(o, i, k=1):
        return (rng.standard_normal((o, i, k, k)) * np.sqrt(1 / (i * k * k))).astype(np.float32)

    def blocks(name, c, heads, count=1):
        hidden = int(c * 2.66)
        for i in range(count):
            p = f"{name}.{i}"
            sd[f"{p}.norm1.body.weight"] = (1 + 0.1 * rng.standard_normal(c)).astype(np.float32)
            sd[f"{p}.attn.temperature"] = (1 + 0.2 * rng.random((heads, 1, 1))).astype(np.float32)
            sd[f"{p}.attn.qkv.weight"] = w(3 * c, c)
            sd[f"{p}.attn.qkv_dwconv.weight"] = (rng.standard_normal((3 * c, 1, 3, 3)) / 3).astype(np.float32)
            sd[f"{p}.attn.project_out.weight"] = w(c, c)
            sd[f"{p}.norm2.body.weight"] = (1 + 0.1 * rng.standard_normal(c)).astype(np.float32)
            sd[f"{p}.ffn.project_in.weight"] = w(2 * hidden, c)
            sd[f"{p}.ffn.dwconv.weight"] = (rng.standard_normal((2 * hidden, 1, 3, 3)) / 3).astype(np.float32)
            sd[f"{p}.ffn.project_out.weight"] = w(c, hidden)

    sd["patch_embed.proj.weight"] = w(dim, 3, 3)
    blocks("encoder_level1", dim, 1)
    sd["down1_2.body.0.weight"] = w(dim // 2, dim, 3)
    blocks("encoder_level2", dim * 2, 2)
    sd["down2_3.body.0.weight"] = w(dim, dim * 2, 3)
    blocks("encoder_level3", dim * 4, 4)
    sd["down3_4.body.0.weight"] = w(dim * 2, dim * 4, 3)
    blocks("latent", dim * 8, 8)
    sd["up4_3.body.0.weight"] = w(dim * 16, dim * 8, 3)
    sd["reduce_chan_level3.weight"] = w(dim * 4, dim * 8)
    blocks("decoder_level3", dim * 4, 4)
    sd["up3_2.body.0.weight"] = w(dim * 8, dim * 4, 3)
    sd["reduce_chan_level2.weight"] = w(dim * 2, dim * 4)
    blocks("decoder_level2", dim * 2, 2)
    sd["up2_1.body.0.weight"] = w(dim * 4, dim * 2, 3)
    blocks("decoder_level1", dim * 2, 1)
    blocks("refinement", dim * 2, 1)
    sd["output.weight"] = w(3, dim * 2, 3)
    return sd


def test_restormer_graph_matches_reference():
    rng = np.random.default_rng(3)
    sd = tiny_restormer_weights(rng)
    x = rng.random((1, 3, 16, 16)).astype(np.float32)
    np.testing.assert_allclose(_run(build_restormer(sd, 16), x), restormer_reference(sd, x), rtol=1e-3, atol=1e-4)


# ----------------------------------------------------------------------------- pesi reali (opzionale)

_WEIGHTS = os.environ.get("DENOISER_TEST_WEIGHTS")


@pytest.mark.skipif(not _WEIGHTS, reason="pesi reali non disponibili")
@pytest.mark.parametrize("key,minimum", [("drunet", 31.0), ("scunet", 29.5), ("restormer", 26.5)])
def test_real_models_denoise(tmp_path, monkeypatch, key, minimum):
    from skimage.metrics import peak_signal_noise_ratio as psnr

    from denoiser.algorithms import get_algorithm
    from synthetic import clean_display

    monkeypatch.setenv("DENOISER_MODELS_DIR", _WEIGHTS)
    clean = clean_display(256)
    noisy = (clean + np.random.default_rng(0).normal(0, 25 / 255, clean.shape)).astype(np.float32)
    out = get_algorithm(key).denoise_rgb(noisy, 25 / 255, {"tile": "256"})
    assert psnr(clean, np.clip(out, 0, 1), data_range=1) > minimum
