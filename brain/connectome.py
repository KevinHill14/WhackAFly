"""Load the FlyWire v783 connectome (Shiu et al. 2024 files) and find the
escape-circuit neurons by cell type, verified against the FlyWire annotations.

Two senses feed the escape neurons:
  vision - LPLC2 + LC4 looming detectors (the eyes)
  sound  - Johnston's organ auditory neurons JO-A + JO-B (the antennal ear):
           cell_class "mechanosensory", cell_sub_class "auditory"

Laptop-only: needs pandas + pyarrow. The Pi only ever loads the small
subnetwork file produced by extract.py (NumPy only).

Run:  python brain/connectome.py
"""
from pathlib import Path

import numpy as np
import pandas as pd

DATA = Path(__file__).resolve().parent / "data"
COMPLETENESS = DATA / "Completeness_783.csv"
CONNECTIVITY = DATA / "Connectivity_783.parquet"
ANNOTATIONS = DATA / "Supplemental_file1_neuron_annotations.tsv"

INPUT_TYPES = ["LPLC2", "LC4"]                       # looming detectors
OUTPUT_TYPES = ["DNp01", "DNp02", "DNp04", "DNp06", "DNp11"]  # escape DNs; DNp01 = Giant Fiber

# Expected counts (FlyWire v783 annotations). Checked, not assumed.
EXPECTED = {"LPLC2": 210, "LC4": 104, "DNp01": 2, "DNp02": 2,
            "DNp04": 2, "DNp06": 2, "DNp11": 2}

# Sound: JO-A / JO-B neurons annotated as auditory. Same-named neurons with
# another sub-class (e.g. 9 JO-B4_b labelled wind_gravity) are left out.
SOUND_CLASS = ("mechanosensory", "auditory")
SOUND_TYPE_RE = r"^JO-[AB]\d"
# (left, right) per type, as annotated. Checked, not assumed.
EXPECTED_SOUND = {
    "JO-A1": (11, 10), "JO-A2": (16, 17), "JO-A3": (3, 3), "JO-A4": (1, 1),
    "JO-A5": (17, 20), "JO-B1_a": (77, 23), "JO-B1_b": (14, 13),
    "JO-B1_c": (11, 11), "JO-B2": (18, 18), "JO-B3": (12, 15),
    "JO-B4_a": (7, 8), "JO-B4_b": (14, 2),
}
# Annotated but absent from Completeness_783 (so not in the simulation):
SOUND_NOT_IN_MODEL = {720575940616042508}   # a left JO-B1_a


def load_completeness():
    """Row order of this file defines the neuron index used in the parquet."""
    comp = pd.read_csv(COMPLETENESS, index_col=0)
    return comp.index.to_numpy(dtype=np.int64)


def load_connectivity():
    cols = ["Presynaptic_Index", "Postsynaptic_Index", "Connectivity", "Excitatory"]
    return pd.read_parquet(CONNECTIVITY, columns=cols)


def load_annotations():
    cols = ["root_id", "cell_type", "hemibrain_type", "side", "top_nt",
            "top_nt_conf", "known_nt", "super_class", "cell_class", "cell_sub_class"]
    a = pd.read_csv(ANNOTATIONS, sep="\t", usecols=cols, dtype={"root_id": np.int64},
                    low_memory=False)
    return a


def find_neurons(ann, root_ids):
    """Return a DataFrame of every target neuron: root_id, cell_type, side,
    index (row in Completeness_783), plus NT info. Raises if anything is off."""
    id_to_idx = pd.Series(np.arange(len(root_ids)), index=root_ids)
    rows = ann[ann.cell_type.isin(INPUT_TYPES + OUTPUT_TYPES)].copy()

    problems = []
    for t, n in EXPECTED.items():
        got = (rows.cell_type == t).sum()
        if got != n:
            problems.append(f"{t}: expected {n}, found {got}")
    for t in OUTPUT_TYPES:
        sides = sorted(rows[rows.cell_type == t].side)
        if sides != ["left", "right"]:
            problems.append(f"{t}: sides {sides}, expected one left + one right")
    missing = ~rows.root_id.isin(id_to_idx.index)
    if missing.any():
        problems.append(f"{missing.sum()} target neurons not in Completeness_783")
    if problems:
        raise RuntimeError("Neuron lookup failed:\n  " + "\n  ".join(problems))

    rows["index"] = id_to_idx.loc[rows.root_id].to_numpy()
    rows["role"] = np.where(rows.cell_type.isin(INPUT_TYPES), "input", "output")
    rows["sense"] = np.where(rows.role == "input", "vision", "")
    return rows.sort_values(["role", "cell_type", "side"]).reset_index(drop=True)


def find_sound_neurons(ann, root_ids):
    """The auditory JO-A/JO-B neurons, verified like find_neurons: per-type and
    per-side counts must match EXPECTED_SOUND, and the only annotated neurons
    missing from Completeness_783 must be the known SOUND_NOT_IN_MODEL."""
    id_to_idx = pd.Series(np.arange(len(root_ids)), index=root_ids)
    cls, sub = SOUND_CLASS
    rows = ann[(ann.cell_class == cls) & (ann.cell_sub_class == sub)
               & ann.cell_type.str.match(SOUND_TYPE_RE, na=False)].copy()

    problems = []
    got = rows.groupby(["cell_type", "side"]).size()
    for t, (nl, nr) in EXPECTED_SOUND.items():
        gl, gr = int(got.get((t, "left"), 0)), int(got.get((t, "right"), 0))
        if (gl, gr) != (nl, nr):
            problems.append(f"{t}: expected L {nl} / R {nr}, found L {gl} / R {gr}")
    extra = set(rows.cell_type) - set(EXPECTED_SOUND)
    if extra:
        problems.append(f"unexpected auditory JO types: {sorted(extra)}")
    bad_side = set(rows.side) - {"left", "right"}
    if bad_side:
        problems.append(f"unexpected sides: {sorted(bad_side)}")
    absent = set(rows.root_id[~rows.root_id.isin(id_to_idx.index)])
    if absent != SOUND_NOT_IN_MODEL:
        problems.append(f"neurons missing from Completeness_783: {sorted(absent)}, "
                        f"expected {sorted(SOUND_NOT_IN_MODEL)}")
    if problems:
        raise RuntimeError("Sound neuron lookup failed:\n  " + "\n  ".join(problems))

    rows = rows[rows.root_id.isin(id_to_idx.index)].copy()
    rows["index"] = id_to_idx.loc[rows.root_id].to_numpy()
    rows["role"] = "input"
    rows["sense"] = "sound"
    return rows.sort_values(["cell_type", "side"]).reset_index(drop=True)


def check_sound_signs(con, snd):
    """Every connection out of the JO neurons must be excitatory in Shiu's
    connectivity (their per-neuron NT predictions are mixed and low
    confidence; see README). Raises otherwise."""
    o = con[con.Presynaptic_Index.isin(snd["index"])]
    neg = int((o.Excitatory < 0).sum())
    if neg:
        raise RuntimeError(f"{neg} inhibitory JO connections in the parquet; the sound "
                           "pathway's sign handling needs revisiting")
    return len(o), int(o.Connectivity.sum())


def main():
    root_ids = load_completeness()
    ann = load_annotations()
    print(f"Completeness_783: {len(root_ids):,} neurons | annotations: {len(ann):,} rows")
    nrn = find_neurons(ann, root_ids)

    print("\nTarget neurons (verified against annotations):")
    summary = (nrn.groupby(["role", "cell_type", "side"]).size()
                  .unstack(fill_value=0))
    print(summary.to_string())

    snd = find_sound_neurons(ann, root_ids)
    print(f"\nSound inputs (auditory JO-A/JO-B, verified): {len(snd)} in the model, "
          f"{len(SOUND_NOT_IN_MODEL)} annotated but absent from Completeness_783")
    print(snd.groupby(["cell_type", "side"]).size().unstack(fill_value=0).to_string())
    print("  per side:", snd.side.value_counts().to_dict())

    print("\nEscape neurons (one per side):")
    out = nrn[nrn.role == "output"]
    print(out[["cell_type", "side", "root_id", "index", "top_nt", "top_nt_conf",
               "known_nt"]].to_string(index=False))

    # DNp01 transmitter conflict: show what the connectivity file actually uses.
    con = load_connectivity()
    print("\nDNp01 (Giant Fiber) synapse sign check:")
    for _, r in out[out.cell_type == "DNp01"].iterrows():
        o = con[con.Presynaptic_Index == r["index"]]
        i = con[con.Postsynaptic_Index == r["index"]]
        print(f"  {r.side:5s}  annotation top_nt={r.top_nt} (conf {r.top_nt_conf:.2f})  "
              f"parquet sign={sorted(o.Excitatory.unique().tolist())}  "
              f"in-synapses={i.Connectivity.sum():,}  out-synapses={o.Connectivity.sum():,}")
    print("  -> Both treated as EXCITATORY (cholinergic). See README.")

    n_edges, n_syn = check_sound_signs(con, snd)
    print(f"\nJO sign check: {n_edges:,} outgoing connections ({n_syn:,} synapses), "
          f"all excitatory in the parquet.")
    print("  annotation top_nt:", snd.top_nt.value_counts().to_dict(),
          "| known_nt:", snd.known_nt.value_counts(dropna=False).to_dict())


if __name__ == "__main__":
    main()
