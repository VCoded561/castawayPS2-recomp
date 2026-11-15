import json

with open("vu1_programs.json") as f:
    progs = json.load(f)

for p in progs:
    if p["Name"] == "vu1_mpg_0x00490A78_dst_25E0":
        sample = p.get("DisassemblySample", [])
        for s in sample[:40]:
            print(s)
