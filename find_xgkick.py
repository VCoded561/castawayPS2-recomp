import json

with open("vu1_programs.json") as f:
    progs = json.load(f)

for p in progs:
    sample = p.get("DisassemblySample", [])
    for idx, s in enumerate(sample):
        if "XGKICK" in s:
            print(f"{p['Name']} at line {idx}: {s}")
            for j in range(max(0, idx - 15), min(len(sample), idx + 5)):
                print(f"  {sample[j]}")
