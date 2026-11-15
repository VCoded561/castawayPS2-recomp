import struct

with open('Data/DVD/SLUS_216.64', 'rb') as f:
    elf = f.read()

file_off = 0x3902F0 # 0x00490270
mpg_data = elf[file_off : file_off + 2048]

for idx in range(len(mpg_data) // 8):
    lo, up = struct.unpack_from('<II', mpg_data, idx * 8)
    inst_num = 956 + idx
    if 1042 <= inst_num <= 1056:
        print(f'[{inst_num}] up=0x{up:08X} lo=0x{lo:08X}')
