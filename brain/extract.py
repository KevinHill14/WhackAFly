"""Extract the looming -> escape subnetwork once (laptop) and save it as a small
.npz that the Pi loads with NumPy only.

Kept neurons: every neuron on some input->output path of <= MAX_HOPS synapses,
using only connections with >= threshold synapses, plus all inputs/outputs.

Run:  python brain/extract.py              # builds thresholds 5, 10, 20
      python brain/extract.py --threshold 10
"""
import argparse
from pathlib import Path

import numpy as np
import pandas as pd

import connectome as cx

MAX_HOPS = 3
OUT_DIR = Path(__file__).resolve().parent / "subnet"
N_INTERMEDIATE_GROUPS = 4   # named interneuron groups on the neuron panel (+ "other")


def hop_distance(n, src, a, b, max_hops):
    """BFS hop distance from src along edges a->b (capped at 99 = unreached)."""
    d = np.full(n, 99, dtype=np.int16)
    d[src] = 0
    for k in range(1, max_hops + 1):
        nxt = np.unique(b[d[a] == k - 1])
        nxt = nxt[d[nxt] > k]
        d[nxt] = k
    return d


def extract(threshold, root_ids, ann, nrn, con):
    n = len(root_ids)
    e = con[con.Connectivity >= threshold]
    pre = e.Presynaptic_Index.to_numpy()
    post = e.Postsynaptic_Index.to_numpy()

    inp = nrn.loc[nrn.role == "input", "index"].to_numpy()
    out = nrn.loc[nrn.role == "output", "index"].to_numpy()
    d_in = hop_distance(n, inp, pre, post, MAX_HOPS)     # hops from any input
    d_out = hop_distance(n, out, post, pre, MAX_HOPS)    # hops to any output
    keep = np.union1d(np.where(d_in + d_out <= MAX_HOPS)[0], np.concatenate([inp, out]))

    # Local indexing: inputs first, then interneurons, then outputs (fixed order).
    inter = np.setdiff1d(keep, np.concatenate([inp, out]))
    order = np.concatenate([inp, inter, out])
    g2l = pd.Series(np.arange(len(order)), index=order)

    m = np.isin(pre, order) & np.isin(post, order)
    sign = e.Excitatory.to_numpy()[m].astype(np.int32)
    count = e.Connectivity.to_numpy()[m].astype(np.int32)
    pre_l = g2l.loc[pre[m]].to_numpy().astype(np.int32)
    post_l = g2l.loc[post[m]].to_numpy().astype(np.int32)

    # DNp01 (Giant Fiber) transmitter conflict in the annotations (one ACh, one
    # Glu, both <0.35 confidence). Force both excitatory/cholinergic, so the
    # left and right fibres are treated symmetrically.
    gf = g2l.loc[nrn.loc[nrn.cell_type == "DNp01", "index"]].to_numpy()
    n_flipped = int(((sign < 0) & np.isin(pre_l, gf)).sum())
    sign[np.isin(pre_l, gf)] = 1
    weight = (sign * count).astype(np.int32)

    # Labels for every kept neuron.
    a = ann.set_index("root_id")
    rid = root_ids[order]
    info = a.reindex(rid)
    cell_type = info.cell_type.fillna("unknown").astype(str).to_numpy()
    side = info.side.fillna("unknown").astype(str).to_numpy()
    n_in, n_inter = len(inp), len(inter)
    role = np.array(["input"] * n_in + ["inter"] * n_inter + ["output"] * len(out))

    # Neuron-panel groups. Inputs by type+side; interneurons by the cell types
    # that send the most synapses onto the escape neurons; outputs one each.
    groups = np.empty(len(order), dtype=object)
    for i in range(n_in):
        groups[i] = f"{cell_type[i]}_{side[i][0].upper()}"
    out_l = np.arange(n_in + n_inter, len(order))
    onto_out = np.isin(post_l, out_l) & (pre_l >= n_in) & (pre_l < n_in + n_inter)
    by_type = (pd.Series(count[onto_out], index=cell_type[pre_l[onto_out]])
                 .groupby(level=0).sum().sort_values(ascending=False))
    top = [t for t in by_type.index if t != "unknown"][:N_INTERMEDIATE_GROUPS]
    for i in range(n_in, n_in + n_inter):
        groups[i] = cell_type[i] if cell_type[i] in top else "other_inter"
    for i in out_l:
        groups[i] = f"{cell_type[i]}_{side[i][0].upper()}"

    in_names = ["LPLC2_L", "LPLC2_R", "LC4_L", "LC4_R"]
    out_names = [f"{t}_{s}" for t in cx.OUTPUT_TYPES for s in "LR"]
    group_names = in_names + top + ["other_inter"] + out_names
    group_id = np.array([group_names.index(g) for g in groups], dtype=np.int16)

    path = OUT_DIR / f"subnet_t{threshold}.npz"
    OUT_DIR.mkdir(exist_ok=True)
    np.savez_compressed(
        path,
        threshold=np.int32(threshold), max_hops=np.int32(MAX_HOPS),
        root_id=rid.astype(np.int64), cell_type=cell_type.astype("U32"),
        side=side.astype("U8"), role=role.astype("U8"),
        group_id=group_id, group_names=np.array(group_names, dtype="U32"),
        pre=pre_l, post=post_l, weight=weight,
    )
    return dict(path=path, neurons=len(order), inputs=n_in, inter=n_inter,
                outputs=len(out), edges=int(m.sum()), synapses=int(count.sum()),
                inhibitory_edges=int((weight < 0).sum()), gf_flipped=n_flipped,
                top=top, kb=path.stat().st_size / 1024)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--threshold", type=int, nargs="*", default=[5, 10, 20])
    args = ap.parse_args()

    root_ids = cx.load_completeness()
    ann = cx.load_annotations()
    nrn = cx.find_neurons(ann, root_ids)
    con = cx.load_connectivity()
    for th in args.threshold:
        r = extract(th, root_ids, ann, nrn, con)
        print(f"threshold >={th:>2}: {r['neurons']:,} neurons "
              f"({r['inputs']} in / {r['inter']:,} inter / {r['outputs']} out), "
              f"{r['edges']:,} connections ({r['inhibitory_edges']:,} inhibitory), "
              f"{r['synapses']:,} synapses, GF signs flipped: {r['gf_flipped']}, "
              f"{r['kb']:.0f} KB -> {r['path'].name}")
        print(f"   panel interneuron groups: {r['top']}")


if __name__ == "__main__":
    main()
