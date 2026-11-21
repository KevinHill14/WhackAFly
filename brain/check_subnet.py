"""Sanity-check a saved subnetwork using NumPy only (what the Pi will do).

Run:  python brain/check_subnet.py brain/subnet/subnet_t5.npz
"""
import sys
import time

import numpy as np


def main(path):
    t0 = time.perf_counter()
    s = np.load(path, allow_pickle=False)
    load_ms = (time.perf_counter() - t0) * 1000
    names = s["group_names"]
    gid = s["group_id"]
    n = len(gid)
    pre, post = s["pre"], s["post"]
    assert pre.max() < n and post.max() < n, "edge index out of range"
    assert (s["weight"] != 0).all(), "zero-weight edge"

    print(f"{path}: loaded in {load_ms:.0f} ms (numpy {np.__version__})")
    print(f"  {n:,} neurons, {len(pre):,} connections, threshold >={int(s['threshold'])}")
    print("  neuron-panel groups (index: name = neuron count):")
    for i, name in enumerate(names):
        print(f"    {i:2d}: {name:14s} = {(gid == i).sum()}")
    # Each escape neuron must receive input inside the subnetwork.
    out = np.where(s["role"] == "output")[0]
    for o in out:
        w = s["weight"][post == o]
        print(f"  {names[gid[o]]:9s} in-connections={len(w):4d}  "
              f"net synapses={w.sum():+6d}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "brain/subnet/subnet_t5.npz")
