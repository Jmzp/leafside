"""Generates sample.pdf: a heavy document for scroll/zoom testing (text, vector art, outline, links)."""
import math, random, zlib

PAGES = 300
random.seed(1)
words = ("lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore "
         "et dolore magna aliqua surface arm lector pdf fluido desplazamiento zoom pagina").split()

objs = {}
def obj(n, body): objs[n] = body

page_ids, content_ids, outline_ids = [], [], []
n = 5
for i in range(PAGES):
    page_ids.append(n); content_ids.append(n + 1); outline_ids.append(n + 2); n += 3

for i in range(PAGES):
    ops = []
    # vector art: many curves to make rendering non-trivial
    ops.append("q 0.5 w")
    for k in range(250):
        r, g, b = random.random(), random.random(), random.random()
        x, y = random.uniform(40, 570), random.uniform(60, 330)
        ops.append(f"{r:.2f} {g:.2f} {b:.2f} RG {x:.1f} {y:.1f} m "
                   f"{x+random.uniform(-60,60):.1f} {y+random.uniform(-60,60):.1f} "
                   f"{x+random.uniform(-60,60):.1f} {y+random.uniform(-60,60):.1f} "
                   f"{x+random.uniform(-60,60):.1f} {y+random.uniform(-60,60):.1f} c S")
    ops.append("Q")
    ops.append(f"BT /F2 22 Tf 56 740 Td (Capitulo {i+1}) Tj ET")
    y = 710
    while y > 350:
        line = " ".join(random.choice(words) for _ in range(11))
        ops.append(f"BT /F1 10 Tf 56 {y} Td ({line}) Tj ET")
        y -= 13
    ops.append(f"BT /F1 9 Tf 290 30 Td (Pagina {i+1} de {PAGES}) Tj ET")
    data = zlib.compress("\n".join(ops).encode("latin-1"))
    obj(content_ids[i], (f"<< /Length {len(data)} /Filter /FlateDecode >>\nstream\n".encode() + data + b"\nendstream"))
    annots = f" /Annots [{n} 0 R]" if i == 0 else ""
    obj(page_ids[i], f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {content_ids[i]} 0 R{annots} >>")
    prev = f" /Prev {outline_ids[i-1]} 0 R" if i else ""
    nxt = f" /Next {outline_ids[i+1]} 0 R" if i < PAGES - 1 else ""
    obj(outline_ids[i], f"<< /Title (Capitulo {i+1}) /Parent {n+1} 0 R{prev}{nxt} /Dest [{page_ids[i]} 0 R /XYZ null null null] >>")

obj(n, f"<< /Type /Annot /Subtype /Link /Rect [56 730 250 765] /Border [0 0 0] /Dest [{page_ids[PAGES-1]} 0 R /Fit] >>")
obj(n + 1, f"<< /Type /Outlines /First {outline_ids[0]} 0 R /Last {outline_ids[-1]} 0 R /Count {PAGES} >>")
obj(1, f"<< /Type /Catalog /Pages 2 0 R /Outlines {n+1} 0 R /PageMode /UseOutlines >>")
obj(2, f"<< /Type /Pages /Count {PAGES} /Kids [{' '.join(f'{p} 0 R' for p in page_ids)}] >>")
obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>")

out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = {}
for k in sorted(objs):
    offsets[k] = len(out)
    body = objs[k] if isinstance(objs[k], bytes) else objs[k].encode("latin-1")
    out += f"{k} 0 obj\n".encode() + body + b"\nendobj\n"
xref = len(out); size = max(objs) + 1
out += f"xref\n0 {size}\n0000000000 65535 f \n".encode()
for k in range(1, size): out += f"{offsets[k]:010d} 00000 n \n".encode()
out += f"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
open("sample.pdf", "wb").write(out)
print(f"sample.pdf: {PAGES} pages, {len(out)//1024} KB")
