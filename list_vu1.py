import json

with open("vu1_programs.json") as f:
    progs = json.load(f)

print(f"Total VU1 programs: {len(progs)}")
for p in progs:
    print(f"{p['Name']} {p['VAddr']} dest={p['VuDestByteAddr']} size={p['SizeBytes']}")
