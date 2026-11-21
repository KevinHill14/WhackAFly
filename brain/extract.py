"""Extract the vision + sound -> escape subnetwork once (laptop) and save it as
a small .npz that the Pi loads with NumPy only.

Kept neurons: every neuron on some input->output path of <= MAX_HOPS synapses,
using only connections with >= threshold synapses, plus all inputs/outputs.
Inputs are both senses: LPLC2/LC4 (vision) and auditory JO-A/JO-B (sound).
The result is a superset of the vision-only network (which is what the
subnet files held before sound was added; that part is unchanged).

Neuron-panel groups: the first 19 are exactly the vision-only ones, holding
the same neurons as before (Unity reads spikes by index). Sound adds three
groups at the END: JO_L, JO_R (the ears) and sound_inter (interneurons that
are only in the network because of the sound pathway).

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
SOUND_GROUPS = ["JO_L", "JO_R", "sound_inter"]


def hop_distance(n, src, a, b, max_hops):
    """BFS hop distance from src along edges a->b (capped at 99 = unreached)."""
    d = np.full(n, 99, dtype=np.int16)
    d[src] = 0
    for k in range(1, max_hops + 1):
        nxt = np.unique(b[d[a] == k - 1])
        nxt = nxt[d[nxt] > k]
        d[nxt] = k
    return d


def extract(threshold, root_ids, ann, nrn, snd, con):
    n = len(root_ids)
    e = con[con.Connectivity >= threshold]
    pre = e.Presynaptic_Index.to_numpy()
    post = e.Postsynaptic_Index.to_numpy()
    cnt_all = e.Connectivity.to_numpy()

    vis = nrn.loc[nrn.role == "input", "index"].to_numpy()
    aud = snd["index"].to_numpy()
    out = nrn.loc[nrn.role == "output", "index"].to_numpy()
    d_out = hop_distance(n, out, post, pre, MAX_HOPS)    # hops to any output

    def keep_for(inp):
        d_in = hop_distance(n, inp, pre, post, MAX_HOPS)
        return np.union1d(np.where(d_in + d_out <= MAX_HOPS)[0], np.concatenate([inp, out]))

    # Vision-only network: exactly what extract.py produced before sound.
    keep_v = keep_for(vis)
    inter_v = np.setdiff1d(keep_v, np.concatenate([vis, out]))
    # Both senses.
    inp = np.concatenate([vis, aud])
    keep = np.union1d(keep_for(inp), keep_v)
    inter = np.setdiff1d(keep, np.concatenate([inp, out]))
    assert np.isin(inter_v, inter).all()

    # Local indexing: vision inputs, sound inputs, interneurons, outputs.
    order = np.concatenate([vis, aud, inter, out])
    g2l = pd.Series(np.arange(len(order)), index=order)

    m = np.isin(pre, order) & np.isin(post, order)
    sign = e.Excitatory.to_numpy()[m].astype(np.int32)
    count = cnt_all[m].astype(np.int32)
    pre_l = g2l.loc[pre[m]].to_numpy().astype(np.int32)
    post_l = g2l.loc[post[m]].to_numpy().astype(np.int32)

    # DNp01 (Giant Fiber) transmitter conflict in the annotations (one ACh, one
    # Glu, both <0.35 confidence). Force both excitatory/cholinergic, so the
    # left and right fibres are treated symmetrically.
    gf = g2l.loc[nrn.loc[nrn.cell_type == "DNp01", "index"]].to_numpy()
    n_flipped = int(((sign < 0) & np.isin(pre_l, gf)).sum())
    sign[np.isin(pre_l, gf)] = 1
    # JO neurons: mixed low-confidence NT predictions, but every connection is
    # excitatory in Shiu's parquet. Keep that, and stop if it ever isn't.
    aud_l = g2l.loc[aud].to_numpy()
    neg_jo = int(((sign < 0) & np.isin(pre_l, aud_l)).sum())
    if neg_jo:
        raise RuntimeError(f"{neg_jo} inhibitory JO connections kept; revisit sign handling")
    weight = (sign * count).astype(np.int32)

    # Labels for every kept neuron.
    a = ann.set_index("root_id")
    rid = root_ids[order]
    info = a.reindex(rid)
    cell_type = info.cell_type.fillna("unknown").astype(str).to_numpy()
    side = info.side.fillna("unknown").astype(str).to_numpy()
    n_vis, n_aud, n_inter = len(vis), len(aud), len(inter)
    n_in = n_vis + n_aud
    role = np.array(["input"] * n_in + ["inter"] * n_inter + ["output"] * len(out))
    sense = np.array(["vision"] * n_vis + ["sound"] * n_aud + [""] * (n_inter + len(out)))

    # Panel interneuron groups: the cell types that send the most synapses onto
    # the escape neurons in the VISION-ONLY network (same rule and result as
    # before sound was added).
    lab = a.reindex(root_ids[inter_v]).cell_type.fillna("unknown").astype(str)
    lab.index = inter_v
    onto_out = np.isin(pre, inter_v) & np.isin(post, out)
    by_type = (pd.Series(cnt_all[onto_out], index=lab.loc[pre[onto_out]].to_numpy())
                 .groupby(level=0).sum().sort_values(ascending=False))
    top = [t for t in by_type.index if t != "unknown"][:N_INTERMEDIATE_GROUPS]

    groups = np.empty(len(order), dtype=object)
    for i in range(n_vis):
        groups[i] = f"{cell_type[i]}_{side[i][0].upper()}"
    for i in range(n_vis, n_in):
        groups[i] = f"JO_{side[i][0].upper()}"
    in_v = set(inter_v.tolist())
    for i in range(n_in, n_in + n_inter):
        if int(order[i]) not in in_v:
            groups[i] = "sound_inter"
        else:
            groups[i] = cell_type[i] if cell_type[i] in top else "other_inter"
    out_l = np.arange(n_in + n_inter, len(order))
    for i in out_l:
        groups[i] = f"{cell_type[i]}_{side[i][0].upper()}"

    in_names = ["LPLC2_L", "LPLC2_R", "LC4_L", "LC4_R"]
    out_names = [f"{t}_{s}" for t in cx.OUTPUT_TYPES for s in "LR"]
    group_names = in_names + top + ["other_inter"] + out_names + SOUND_GROUPS
    group_id = np.array([group_names.index(g) for g in groups], dtype=np.int16)

    path = OUT_DIR / f"subnet_t{threshold}.npz"
    OUT_DIR.mkdir(exist_ok=True)
    np.savez_compressed(
        path,
        threshold=np.int32(threshold), max_hops=np.int32(MAX_HOPS),
        root_id=rid.astype(np.int64), cell_type=cell_type.astype("U32"),
        side=side.astype("U8"), role=role.astype("U8"), sense=sense.astype("U8"),
        group_id=group_id, group_names=np.array(group_names, dtype="U32"),
        pre=pre_l, post=post_l, weight=weight,
    )
    jo_used = np.isin(aud_l, pre_l)
    return dict(path=path, neurons=len(order), vision_only=len(keep_v), vis=n_vis,
                aud=n_aud, aud_l=int((side[n_vis:n_in] == "left").sum()),
                aud_r=int((side[n_vis:n_in] == "right").sum()),
                jo_used=int(jo_used.sum()), inter=n_inter,
                sound_inter=int((groups == "sound_inter").sum()),
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
    snd = cx.find_sound_neurons(ann, root_ids)
    con = cx.load_connectivity()
    cx.check_sound_signs(con, snd)
    for th in args.threshold:
        r = extract(th, root_ids, ann, nrn, snd, con)
        print(f"threshold >={th:>2}: {r['neurons']:,} neurons "
              f"(inputs {r['vis']} vision + {r['aud']} sound [L {r['aud_l']} / R {r['aud_r']}, "
              f"{r['jo_used']} with a kept connection] / {r['inter']:,} inter "
              f"[{r['sound_inter']} sound-only] / {r['outputs']} out), "
              f"vision-only part {r['vision_only']:,}, "
              f"{r['edges']:,} connections ({r['inhibitory_edges']:,} inhibitory), "
              f"{r['synapses']:,} synapses, GF signs flipped: {r['gf_flipped']}, "
              f"{r['kb']:.0f} KB -> {r['path'].name}")
        print(f"   panel interneuron groups: {r['top']}")


if __name__ == "__main__":
    main()
