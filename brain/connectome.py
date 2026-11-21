"""Load the FlyWire v783 connectome (Shiu et al. 2024 files) and find the
escape-circuit neurons by cell type, verified against the FlyWire annotations.

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


def load_completeness():
    """Row order of this file defines the neuron index used in the parquet."""
    comp = pd.read_csv(COMPLETENESS, index_col=0)
    return comp.index.to_numpy(dtype=np.int64)


def load_connectivity():
    cols = ["Presynaptic_Index", "Postsynaptic_Index", "Connectivity", "Excitatory"]
    return pd.read_parquet(CONNECTIVITY, columns=cols)


def load_annotations():
    cols = ["root_id", "cell_type", "hemibrain_type", "side", "top_nt",
            "top_nt_conf", "known_nt", "super_class"]
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
    return rows.sort_values(["role", "cell_type", "side"]).reset_index(drop=True)


def main():
    root_ids = load_completeness()
    ann = load_annotations()
    print(f"Completeness_783: {len(root_ids):,} neurons | annotations: {len(ann):,} rows")
    nrn = find_neurons(ann, root_ids)

    print("\nTarget neurons (verified against annotations):")
    summary = (nrn.groupby(["role", "cell_type", "side"]).size()
                  .unstack(fill_value=0))
    print(summary.to_string())

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


if __name__ == "__main__":
    main()
