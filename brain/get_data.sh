#!/usr/bin/env sh
# Download the public connectome files used to build the fly brain subnetwork.
# Only needed on the laptop (the Pi loads the extracted subnetwork file).
set -e
cd "$(dirname "$0")"
mkdir -p data
cd data
SHIU=https://raw.githubusercontent.com/philshiu/Drosophila_brain_model/main
ANN=https://raw.githubusercontent.com/flyconnectome/flywire_annotations/main/supplemental_files
curl -fL -o Completeness_783.csv "$SHIU/Completeness_783.csv"
curl -fL -o Connectivity_783.parquet "$SHIU/Connectivity_783.parquet"
curl -fL -o Supplemental_file1_neuron_annotations.tsv "$ANN/Supplemental_file1_neuron_annotations.tsv"
ls -l
