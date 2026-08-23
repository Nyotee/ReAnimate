import struct, zlib, os, sys

# usage: extract_testfiles.py <outdir> [path to game\sqpack\ffxiv]
OUT = sys.argv[1] if len(sys.argv) > 1 else "."
GAME = sys.argv[2] if len(sys.argv) > 2 else r"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack\ffxiv"

def load_index(path):
    with open(path, 'rb') as f:
        data = f.read()
    hdr = struct.unpack_from('<I', data, 0x0c)[0]
    off = struct.unpack_from('<I', data, hdr + 8)[0]
    size = struct.unpack_from('<I', data, hdr + 12)[0]
    entries = {}
    for o in range(off, off + size, 16):
        h = struct.unpack_from('<Q', data, o)[0]
        d = struct.unpack_from('<I', data, o + 8)[0]
        entries[h] = d
    return entries

def key(path):
    folder, file = path.lower().rsplit('/', 1)
    fc = (zlib.crc32(folder.encode()) & 0xFFFFFFFF) ^ 0xFFFFFFFF
    gc = (zlib.crc32(file.encode()) & 0xFFFFFFFF) ^ 0xFFFFFFFF
    return fc << 32 | gc

def extract(entries, path):
    d = entries[key(path)]
    dat_id = (d & 0b1110) >> 1
    offset = (d & ~0xF) * 8
    with open(os.path.join(GAME, f'040000.win32.dat{dat_id}'), 'rb') as f:
        f.seek(offset)
        header = f.read(24)
        header_len, ftype, raw_size = struct.unpack_from('<III', header, 0)
        assert ftype == 2, f"unexpected file type {ftype}"
        num_blocks = struct.unpack_from('<I', header, 0x14)[0]
        blocks = [struct.unpack('<IHH', f.read(8))[0] for _ in range(num_blocks)]
        out = b''
        for boff in blocks:
            f.seek(offset + header_len + boff)
            bh = f.read(16)
            _, _, comp, uncomp = struct.unpack('<IIII', bh)
            if comp == 32000:
                out += f.read(uncomp)
            else:
                out += zlib.decompress(f.read(comp), -15)
        assert len(out) == raw_size, f"size mismatch {len(out)} vs {raw_size}"
        return out

entries = load_index(os.path.join(GAME, '040000.win32.index'))
targets = {
    'idle_c0801.pap': 'chara/human/c0801/animation/a0001/bt_common/resident/idle.pap',
    'pose01_c0801.pap': 'chara/human/c0801/animation/a0001/bt_common/emote/pose01_loop.pap',
    'sit_c0801.pap': 'chara/human/c0801/animation/a0001/bt_common/emote/sit.pap',
    'skl_c0801.sklb': 'chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb',
}
for name, path in targets.items():
    data = extract(entries, path)
    with open(os.path.join(OUT, name), 'wb') as f:
        f.write(data)
    print(f"{name}: {len(data)} bytes, magic={data[:4]}")
