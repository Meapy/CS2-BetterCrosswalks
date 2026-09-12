"""
Draws Properties/Thumbnail.png, the 512x512 cover Paradox Mods shows for this mod.

Run it with `python3 thumbnail.py` after `pip install cairosvg`; it writes Thumbnail.png and
Thumbnail.svg beside itself. The background, palette and title treatment are sampled from the
Tourism Overhaul cover so the two mods read as a set.

The subject is a junction with crossings through its middle. The ordinary crossings on the four
arms are drawn dimmer and shallower, and the two middle ones brighter and wider, so the feature
reads without painting anything a colour a crossing could not be: the accent colour is on the kerbs.
"""
import math, os, cairosvg

W = 512
BG_TOP, BG_MID, BG_DARK = "#13384a", "#17414f", "#0b1e29"
GLOW      = "#1a4a46"
CYAN      = "#03beff"
CYAN_SOFT = "#56d3e5"
CREAM     = "#faf8ec"
KERB      = "#93a8b0"
KERB_SIDE = "#4e6874"
ASPHALT   = "#1b415a"
ASPHALT_D = "#122f44"
SLAB      = "#081820"

U, V   = 2.62, 1.31
CX, CY = 256, 198

ARM   = 48      # half size of the whole square plate
HW    = 23      # road half width
KERBH = 7       # pavement height, px
DEPTH = 14      # plate thickness, px
CN, CF = HW + 3, HW + 12      # the ordinary crossings' near and far edges
REACH = HW + 3                # how far the middle crossings run past the box

def iso(gx, gy, h=0.0):
    return (CX + (gx - gy) * U, CY + (gx + gy) * V - h)

def poly(pts, fill, op=None):
    d = " ".join(f"{x:.2f},{y:.2f}" for x, y in pts)
    a = f' opacity="{op}"' if op is not None else ""
    return f'<polygon points="{d}" fill="{fill}"{a}/>'

def extrude(outline_g, h, fill, lift=0.0):
    pts = [iso(gx, gy, lift) for gx, gy in outline_g]
    return "".join(
        f'<polygon points="{a[0]:.2f},{a[1]:.2f} {b[0]:.2f},{b[1]:.2f} '
        f'{b[0]:.2f},{b[1]+h:.2f} {a[0]:.2f},{a[1]+h:.2f}" fill="{fill}"/>'
        for a, b in ((pts[i], pts[(i+1) % len(pts)]) for i in range(len(pts))))

def rect(p0, p1, half):
    dx, dy = p1[0]-p0[0], p1[1]-p0[1]
    L = math.hypot(dx, dy); nx, ny = -dy/L*half, dx/L*half
    return [(p0[0]+nx,p0[1]+ny),(p1[0]+nx,p1[1]+ny),(p1[0]-nx,p1[1]-ny),(p0[0]-nx,p0[1]-ny)]

def bars(p0, p1, half, n, frac=0.52):
    dx, dy = p1[0]-p0[0], p1[1]-p0[1]
    L = math.hypot(dx, dy); ux, uy = dx/L, dy/L
    nx, ny = -uy*half, ux*half
    pitch, bar, out = L/n, L/n*frac, []
    for i in range(n):
        a = i*pitch + (pitch-bar)/2
        A = (p0[0]+ux*a, p0[1]+uy*a)
        B = (p0[0]+ux*(a+bar), p0[1]+uy*(a+bar))
        out.append([(A[0]+nx,A[1]+ny),(B[0]+nx,B[1]+ny),(B[0]-nx,B[1]-ny),(A[0]-nx,A[1]-ny)])
    return out

def scene():
    s = []
    plate = [(-ARM,-ARM), (ARM,-ARM), (ARM,ARM), (-ARM,ARM)]
    s.append(extrude(plate, DEPTH, SLAB))
    s.append(poly([iso(*g) for g in plate], ASPHALT))

    # the four corner blocks: what makes a junction read as a junction
    G = 0.5
    for sx in (1, -1):
        for sy in (1, -1):
            a, b = (HW+G)*sx, ARM*sx
            c, d = (HW+G)*sy, ARM*sy
            block = [(a,c), (b,c), (b,d), (a,d)]
            s.append(extrude(block, KERBH + 1, KERB_SIDE, KERBH))
            s.append(poly([iso(*g, KERBH) for g in block], KERB))

    for sx in (1, -1):
        for sy in (1, -1):
            a, b = (HW+0.5)*sx, ARM*sx
            c = (HW+0.5)*sy
            p, q = iso(a, c, KERBH), iso(b, c, KERBH)
            s.append(f'<line x1="{p[0]:.1f}" y1="{p[1]:.1f}" x2="{q[0]:.1f}" y2="{q[1]:.1f}" '
                     f'stroke="{CYAN}" stroke-width="2.4" opacity="0.55"/>')
            p, q = iso(c, a, KERBH), iso(c, b, KERBH)
            s.append(f'<line x1="{p[0]:.1f}" y1="{p[1]:.1f}" x2="{q[0]:.1f}" y2="{q[1]:.1f}" '
                     f'stroke="{CYAN}" stroke-width="2.4" opacity="0.55"/>')

    # the junction box itself, a shade darker so the middle reads
    s.append(poly([iso(-HW,-HW), iso(HW,-HW), iso(HW,HW), iso(-HW,HW)], ASPHALT_D))

    # the four ordinary crossings
    for side in "NSEW":
        n, pitch = 6, 2*HW/6
        bar = pitch*0.54
        for i in range(n):
            a = -HW + i*pitch + (pitch-bar)/2; b = a + bar
            q = {"N": ((a,-CF),(b,-CF),(b,-CN),(a,-CN)),
                 "S": ((a, CN),(b, CN),(b, CF),(a, CF)),
                 "W": ((-CF,a),(-CN,a),(-CN,b),(-CF,b)),
                 "E": (( CN,a),( CF,a),( CF,b),( CN,b))}[side]
            s.append(poly([iso(*g) for g in q], CREAM, op=0.62))

    # the mod's own: corner to corner, ending on the corner paving
    for p0, p1 in ((( REACH, REACH), (-REACH,-REACH)),
                   (( REACH,-REACH), (-REACH, REACH))):
        for st in bars(p0, p1, 9.5, 9, 0.56):
            s.append(poly([iso(*g, 0.5) for g in st], CREAM))

    
    return "".join(s)

def build(path, t1="BETTER", t2="CROSSWALKS", font="Liberation Sans",
          s1=46, s2=54, ls1=30, ls2=5, y1=414, y2=474):
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{W}" viewBox="0 0 {W} {W}">
<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="0.35" y2="1">
    <stop offset="0" stop-color="{BG_TOP}"/><stop offset="0.55" stop-color="{BG_MID}"/>
    <stop offset="1" stop-color="{BG_DARK}"/></linearGradient>
  <radialGradient id="glow" cx="0.5" cy="0.4" r="0.5">
    <stop offset="0" stop-color="{GLOW}" stop-opacity="0.9"/>
    <stop offset="1" stop-color="{GLOW}" stop-opacity="0"/></radialGradient>
  <radialGradient id="sh" cx="0.5" cy="0.5" r="0.5">
    <stop offset="0" stop-color="#04121a" stop-opacity="0.8"/>
    <stop offset="1" stop-color="#04121a" stop-opacity="0"/></radialGradient>
  <clipPath id="sq"><rect width="{W}" height="{W}" rx="96" ry="96"/></clipPath>
</defs>
<rect width="{W}" height="{W}" fill="#000"/>
<g clip-path="url(#sq)">
  <rect width="{W}" height="{W}" fill="url(#bg)"/>
  <rect width="{W}" height="{W}" fill="url(#glow)"/>
  <ellipse cx="256" cy="212" rx="215" ry="102" fill="url(#sh)"/>
  {scene()}
  <g font-family="{font}" font-weight="bold" text-anchor="middle">
    <text x="{256+ls1/2}" y="{y1}" font-size="{s1}" letter-spacing="{ls1}" fill="{CREAM}">{t1}</text>
    <text x="{256+ls2/2}" y="{y2}" font-size="{s2}" letter-spacing="{ls2}" fill="{CYAN_SOFT}">{t2}</text>
  </g>
</g></svg>'''
    open(path.replace(".png", ".svg"), "w").write(svg)
    cairosvg.svg2png(bytestring=svg.encode(), write_to=path, output_width=512, output_height=512)

if __name__ == "__main__":
    build(os.path.join(os.path.dirname(os.path.abspath(__file__)), "Thumbnail.png"))
    print("ok")
