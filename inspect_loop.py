import json

with open("vu1_programs.json") as f:
    progs = json.load(f)

for p in progs:
    sample = p.get("DisassemblySample", [])
    for s in sample:
        if "[1175]" in s:
            idx = sample.index(s)
            for j in range(idx, min(len(sample), idx + 35)):
                print(sample[j])
