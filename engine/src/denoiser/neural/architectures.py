"""Ricostruzione in ONNX delle reti DRUNet, SCUNet e Restormer a partire dai pesi originali.

Le architetture seguono fedelmente il codice ufficiale:
- DRUNet: KAIR, ``models/network_unet.py`` (UNetRes), licenza MIT, © Kai Zhang;
- SCUNet: ``models/network_scunet.py`` del repository SCUNet, licenza Apache 2.0, © Kai Zhang;
- Restormer: ``basicsr/models/archs/restormer_arch.py`` del repository Restormer, licenza MIT,
  © Syed Waqas Zamir e altri.
"""

from __future__ import annotations

from typing import Mapping

import numpy as np
import onnx

from .graph import GraphBuilder

StateDict = Mapping[str, np.ndarray]


def _count(sd: StateDict, prefix: str, suffix: str) -> int:
    """Numero di blocchi ``prefix.<i>suffix`` presenti nei pesi."""
    n = 0
    while f"{prefix}.{n}{suffix}" in sd:
        n += 1
    return n


# ============================================================================ DRUNet


def build_drunet(sd: StateDict) -> onnx.ModelProto:
    """Input: (1, 4, H, W) = RGB in [0, 1] + mappa del livello di rumore; H, W multipli di 8."""
    g = GraphBuilder()

    def bias(key: str):
        return sd.get(key.replace(".weight", ".bias"))

    def conv3(x: str, key: str) -> str:
        return g.conv(x, sd[key], bias(key), stride=1, pad=1)

    def resblock(x: str, prefix: str) -> str:
        y = g.relu(conv3(x, f"{prefix}.res.0.weight"))
        y = conv3(y, f"{prefix}.res.2.weight")
        return g.add(x, y)

    def down(x: str, name: str) -> str:
        nb = _count(sd, name, ".res.0.weight")
        for i in range(nb):
            x = resblock(x, f"{name}.{i}")
        key = f"{name}.{nb}.weight"
        return g.conv(x, sd[key], bias(key), stride=2, pad=0)

    def up(x: str, name: str) -> str:
        x = g.conv_transpose(x, sd[f"{name}.0.weight"], stride=2)
        i = 1
        while f"{name}.{i}.res.0.weight" in sd:
            x = resblock(x, f"{name}.{i}")
            i += 1
        return x

    x1 = conv3("input", "m_head.weight")
    x2 = down(x1, "m_down1")
    x3 = down(x2, "m_down2")
    x4 = down(x3, "m_down3")
    x = x4
    for i in range(_count(sd, "m_body", ".res.0.weight")):
        x = resblock(x, f"m_body.{i}")
    x = up(g.add(x, x4), "m_up3")
    x = up(g.add(x, x3), "m_up2")
    x = up(g.add(x, x2), "m_up1")
    out = conv3(g.add(x, x1), "m_tail.weight")
    out = g.op("Identity", [out])
    in_channels = sd["m_head.weight"].shape[1]
    return g.build([("input", [1, in_channels, "H", "W"])], out, "drunet")


# ============================================================================ SCUNet

_WINDOW = 8
_HEAD_DIM = 32


def relative_position_bias(params: np.ndarray, window: int = _WINDOW) -> np.ndarray:
    """(teste, 2p-1, 2p-1) -> (teste, p*p, p*p), come WMSA.relative_embedding."""
    cord = np.array([[i, j] for i in range(window) for j in range(window)])
    relation = cord[:, None, :] - cord[None, :, :] + window - 1
    return params[:, relation[:, :, 0], relation[:, :, 1]].astype(np.float32)


def shifted_window_mask(h_windows: int, w_windows: int, window: int = _WINDOW) -> np.ndarray:
    """Maschera additiva (1, 1, finestre, p*p, p*p), come WMSA.generate_mask per le finestre spostate."""
    p = window
    s = p - window // 2
    mask = np.zeros((h_windows, w_windows, p, p, p, p), dtype=bool)
    mask[-1, :, :s, :, s:, :] = True
    mask[-1, :, s:, :, :s, :] = True
    mask[:, -1, :, :s, :, s:] = True
    mask[:, -1, :, s:, :, :s] = True
    mask = mask.reshape(h_windows * w_windows, p * p, p * p)[None, None]
    return np.where(mask, -1e9, 0.0).astype(np.float32)


def _wmsa(g: GraphBuilder, x: str, sd: StateDict, prefix: str, h: int, w: int, c: int, shifted: bool) -> str:
    """Attenzione a finestre (Swin) su x di forma (1, h, w, c)."""
    p = _WINDOW
    heads = c // _HEAD_DIM
    hw, ww = h // p, w // p
    nw, npix = hw * ww, p * p
    shift = p // 2
    if shifted:
        x = g.roll(g.roll(x, -shift, 1, h), -shift, 2, w)
    x = g.reshape(x, [1, hw, p, ww, p, c])
    x = g.transpose(x, [0, 1, 3, 2, 4, 5])
    x = g.reshape(x, [1, nw, npix, c])
    qkv = g.linear(x, sd[f"{prefix}.embedding_layer.weight"], sd[f"{prefix}.embedding_layer.bias"])
    qkv = g.reshape(qkv, [1, nw, npix, 3 * heads, _HEAD_DIM])
    qkv = g.transpose(qkv, [3, 0, 1, 2, 4])  # (3*teste, 1, nw, np, hd)
    q, k, v = g.op("Split", [qkv, g.ints([heads, heads, heads])], n_out=3, axis=0)
    kt = g.transpose(k, [0, 1, 2, 4, 3])
    sim = g.mul(g.op("MatMul", [q, kt]), g.const(_HEAD_DIM**-0.5, "scale"))
    bias = relative_position_bias(sd[f"{prefix}.relative_position_params"])[:, None, None]
    sim = g.add(sim, g.const(bias, "relpos"))
    if shifted:
        sim = g.add(sim, g.const(shifted_window_mask(hw, ww), "mask"))
    probs = g.op("Softmax", [sim], axis=-1)
    out = g.op("MatMul", [probs, v])  # (teste, 1, nw, np, hd)
    out = g.transpose(out, [1, 2, 3, 0, 4])
    out = g.reshape(out, [1, nw, npix, c])
    out = g.linear(out, sd[f"{prefix}.linear.weight"], sd[f"{prefix}.linear.bias"])
    out = g.reshape(out, [1, hw, ww, p, p, c])
    out = g.transpose(out, [0, 1, 3, 2, 4, 5])
    out = g.reshape(out, [1, h, w, c])
    if shifted:
        out = g.roll(g.roll(out, shift, 1, h), shift, 2, w)
    return out


def swin_block(g: GraphBuilder, x: str, sd: StateDict, prefix: str, h: int, w: int, c: int, shifted: bool) -> str:
    """Block di SCUNet su (1, h, w, c)."""
    y = g.layer_norm(x, sd[f"{prefix}.ln1.weight"], sd[f"{prefix}.ln1.bias"])
    x = g.add(x, _wmsa(g, y, sd, f"{prefix}.msa", h, w, c, shifted))
    y = g.layer_norm(x, sd[f"{prefix}.ln2.weight"], sd[f"{prefix}.ln2.bias"])
    y = g.gelu(g.linear(y, sd[f"{prefix}.mlp.0.weight"], sd[f"{prefix}.mlp.0.bias"]))
    y = g.linear(y, sd[f"{prefix}.mlp.2.weight"], sd[f"{prefix}.mlp.2.bias"])
    return g.add(x, y)


def _conv_trans_block(g: GraphBuilder, x: str, sd: StateDict, prefix: str, h: int, w: int, shifted: bool) -> str:
    w11 = sd[f"{prefix}.conv1_1.weight"]
    total = w11.shape[0]
    trans_dim = sd[f"{prefix}.trans_block.ln1.weight"].shape[0]
    conv_dim = total - trans_dim
    y = g.conv(x, w11, sd[f"{prefix}.conv1_1.bias"])
    conv_x, trans_x = g.op("Split", [y, g.ints([conv_dim, trans_dim])], n_out=2, axis=1)
    c = g.relu(g.conv(conv_x, sd[f"{prefix}.conv_block.0.weight"], pad=1))
    c = g.conv(c, sd[f"{prefix}.conv_block.2.weight"], pad=1)
    conv_x = g.add(c, conv_x)
    t = g.transpose(trans_x, [0, 2, 3, 1])
    t = swin_block(g, t, sd, f"{prefix}.trans_block", h, w, trans_dim, shifted)
    t = g.transpose(t, [0, 3, 1, 2])
    res = g.conv(g.op("Concat", [conv_x, t], axis=1), sd[f"{prefix}.conv1_2.weight"], sd[f"{prefix}.conv1_2.bias"])
    return g.add(x, res)


def build_scunet(sd: StateDict, size: int) -> onnx.ModelProto:
    """Input: (1, 3, size, size) RGB in [0, 1]; ``size`` multiplo di 64 (le finestre sono fisse)."""
    if size % 64:
        raise ValueError("La dimensione del tassello per SCUNet deve essere multipla di 64")
    g = GraphBuilder()

    def stage(x: str, name: str, first: int, count: int, res: int) -> str:
        # Come nel codice originale: blocchi pari a finestre fisse, dispari a finestre spostate
        # (input_resolution di costruzione = 256, sempre maggiore della finestra).
        for i in range(count):
            x = _conv_trans_block(g, x, sd, f"{name}.{first + i}", res, res, shifted=bool(i % 2))
        return x

    def n_blocks(name: str, first: int) -> int:
        n = 0
        while f"{name}.{first + n}.conv1_1.weight" in sd:
            n += 1
        return n

    x1 = g.conv("input", sd["m_head.0.weight"], pad=1)
    n = n_blocks("m_down1", 0)
    x = stage(x1, "m_down1", 0, n, size)
    x2 = g.conv(x, sd[f"m_down1.{n}.weight"], stride=2)
    n = n_blocks("m_down2", 0)
    x = stage(x2, "m_down2", 0, n, size // 2)
    x3 = g.conv(x, sd[f"m_down2.{n}.weight"], stride=2)
    n = n_blocks("m_down3", 0)
    x = stage(x3, "m_down3", 0, n, size // 4)
    x4 = g.conv(x, sd[f"m_down3.{n}.weight"], stride=2)
    x = stage(x4, "m_body", 0, n_blocks("m_body", 0), size // 8)
    x = g.conv_transpose(g.add(x, x4), sd["m_up3.0.weight"], stride=2)
    x = stage(x, "m_up3", 1, n_blocks("m_up3", 1), size // 4)
    x = g.conv_transpose(g.add(x, x3), sd["m_up2.0.weight"], stride=2)
    x = stage(x, "m_up2", 1, n_blocks("m_up2", 1), size // 2)
    x = g.conv_transpose(g.add(x, x2), sd["m_up1.0.weight"], stride=2)
    x = stage(x, "m_up1", 1, n_blocks("m_up1", 1), size)
    out = g.conv(g.add(x, x1), sd["m_tail.0.weight"], pad=1)
    out = g.op("Identity", [out])
    return g.build([("input", [1, 3, size, size])], out, "scunet")


# ============================================================================ Restormer


def _restormer_norm(g: GraphBuilder, x: str, weight: np.ndarray, bias: np.ndarray | None) -> str:
    """LayerNorm sui canali di ogni pixel (BiasFree: niente sottrazione della media nell'uscita)."""
    mean = g.op("ReduceMean", [x], axes=[1], keepdims=1)
    centered = g.op("Sub", [x, mean])
    var = g.op("ReduceMean", [g.mul(centered, centered)], axes=[1], keepdims=1)
    denom = g.op("Sqrt", [g.add(var, g.const(1e-5, "eps"))])
    y = g.op("Div", [centered if bias is not None else x, denom])
    y = g.mul(y, g.const(weight.reshape(1, -1, 1, 1), "ln_w"))
    return g.add(y, g.const(bias.reshape(1, -1, 1, 1), "ln_b")) if bias is not None else y


def _l2_normalize(g: GraphBuilder, x: str) -> str:
    """Come F.normalize(x, dim=-1)."""
    norm = g.op("Sqrt", [g.op("ReduceSum", [g.mul(x, x), g.ints([-1])], keepdims=1)])
    return g.op("Div", [x, g.op("Max", [norm, g.const(1e-12, "eps")])])


def _restormer_block(g: GraphBuilder, x: str, sd: StateDict, prefix: str, h: int, w: int) -> str:
    dim = sd[f"{prefix}.norm1.body.weight"].shape[0]
    heads = sd[f"{prefix}.attn.temperature"].shape[0]
    # Attenzione tra i canali (MDTA)
    y = _restormer_norm(g, x, sd[f"{prefix}.norm1.body.weight"], sd.get(f"{prefix}.norm1.body.bias"))
    qkv = g.conv(y, sd[f"{prefix}.attn.qkv.weight"])
    qkv = g.conv(qkv, sd[f"{prefix}.attn.qkv_dwconv.weight"], pad=1, groups=3 * dim)
    qkv = g.reshape(qkv, [3, heads, dim // heads, h * w])
    q, k, v = g.op("Split", [qkv, g.ints([1, 1, 1])], n_out=3, axis=0)
    q, k = _l2_normalize(g, q), _l2_normalize(g, k)
    attn = g.op("MatMul", [q, g.transpose(k, [0, 1, 3, 2])])
    attn = g.mul(attn, g.const(sd[f"{prefix}.attn.temperature"][None], "temp"))
    out = g.op("MatMul", [g.op("Softmax", [attn], axis=-1), v])
    out = g.conv(g.reshape(out, [1, dim, h, w]), sd[f"{prefix}.attn.project_out.weight"])
    x = g.add(x, out)
    # Rete feed-forward con porta (GDFN)
    y = _restormer_norm(g, x, sd[f"{prefix}.norm2.body.weight"], sd.get(f"{prefix}.norm2.body.bias"))
    hidden2 = sd[f"{prefix}.ffn.project_in.weight"].shape[0]
    y = g.conv(y, sd[f"{prefix}.ffn.project_in.weight"])
    y = g.conv(y, sd[f"{prefix}.ffn.dwconv.weight"], pad=1, groups=hidden2)
    a, b = g.op("Split", [y, g.ints([hidden2 // 2, hidden2 // 2])], n_out=2, axis=1)
    y = g.conv(g.mul(g.gelu(a), b), sd[f"{prefix}.ffn.project_out.weight"])
    return g.add(x, y)


def build_restormer(sd: StateDict, size: int) -> onnx.ModelProto:
    """Input: (1, 3, size, size) RGB in [0, 1]; ``size`` multiplo di 8.

    L'attenzione di Restormer è calcolata sull'intero tassello: il grafo è costruito per una
    misura fissa (come SCUNet).
    """
    if size % 8:
        raise ValueError("La dimensione del tassello per Restormer deve essere multipla di 8")
    g = GraphBuilder()

    def level(x: str, name: str, res: int) -> str:
        for i in range(_count(sd, name, ".norm1.body.weight")):
            x = _restormer_block(g, x, sd, f"{name}.{i}", res, res)
        return x

    def down(x: str, key: str, res: int) -> str:
        # Conv a metà canali + PixelUnshuffle(2) (ordine dei canali di PyTorch: c*4 + i*2 + j)
        y = g.conv(x, sd[key], pad=1)
        c = sd[key].shape[0]
        y = g.reshape(y, [1, c, res // 2, 2, res // 2, 2])
        y = g.transpose(y, [0, 1, 3, 5, 2, 4])
        return g.reshape(y, [1, c * 4, res // 2, res // 2])

    def up(x: str, key: str) -> str:
        # Conv a doppi canali + PixelShuffle(2) (= DepthToSpace in modalità CRD)
        return g.op("DepthToSpace", [g.conv(x, sd[key], pad=1)], blocksize=2, mode="CRD")

    s = size
    e1 = level(g.conv("input", sd["patch_embed.proj.weight"], pad=1), "encoder_level1", s)
    e2 = level(down(e1, "down1_2.body.0.weight", s), "encoder_level2", s // 2)
    e3 = level(down(e2, "down2_3.body.0.weight", s // 2), "encoder_level3", s // 4)
    lat = level(down(e3, "down3_4.body.0.weight", s // 4), "latent", s // 8)
    d3 = g.op("Concat", [up(lat, "up4_3.body.0.weight"), e3], axis=1)
    d3 = level(g.conv(d3, sd["reduce_chan_level3.weight"]), "decoder_level3", s // 4)
    d2 = g.op("Concat", [up(d3, "up3_2.body.0.weight"), e2], axis=1)
    d2 = level(g.conv(d2, sd["reduce_chan_level2.weight"]), "decoder_level2", s // 2)
    d1 = level(g.op("Concat", [up(d2, "up2_1.body.0.weight"), e1], axis=1), "decoder_level1", s)
    d1 = level(d1, "refinement", s)
    out = g.add(g.conv(d1, sd["output.weight"], pad=1), "input")
    out = g.op("Identity", [out])
    return g.build([("input", [1, 3, size, size])], out, "restormer")
