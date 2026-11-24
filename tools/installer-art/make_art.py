# Draws the installer artwork from Ivo's look: the dark canvas, a soft violet/cyan glow and the app's mark.
# Run: python tools/installer-art/make_art.py          (Ivo: the crystal, into installer/art)
#      python tools/installer-art/make_art.py portal   (Ivo Portal: its own logo, into installer/art-portal)
# Inno gets 100/125/150/200% sizes; the MSI its own dialog sizes.
from PIL import Image, ImageDraw, ImageFilter
import os, sys

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
PORTAL = len(sys.argv) > 1 and sys.argv[1].lower() == 'portal'
ART = os.path.join(ROOT, 'installer', 'art-portal' if PORTAL else 'art')
os.makedirs(ART, exist_ok=True)
BG = (11, 15, 18)          # the dark canvas
PANEL = (21, 28, 34)
VIOLET, CYAN = (124, 92, 255), (34, 211, 238)

def crystal(size):
    """the two-tone diamond, drawn at 4x then scaled down for clean edges"""
    s = size * 4
    im = Image.new('RGBA', (s, int(s * 16 / 14)), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    w, h = im.size
    d.polygon([(w / 2, 0), (w / 2, h), (0, h / 2)], fill=VIOLET + (255,))
    d.polygon([(w / 2, 0), (w, h / 2), (w / 2, h)], fill=CYAN + (255,))
    shine = Image.new('RGBA', im.size, (0, 0, 0, 0))   # the light facet, blended over the cyan (not painted over it)
    ImageDraw.Draw(shine).polygon([(w / 2, 0), (w, h / 2), (w / 2, h / 2)], fill=(255, 255, 255, 56))
    im.alpha_composite(shine)
    return im.resize((size, int(size * 16 / 14)), Image.LANCZOS)

if PORTAL:   # Ivo Portal's mark: its logo (Portal/portal.png) where Ivo has the crystal
    LOGO = Image.open(os.path.join(ROOT, 'Portal', 'portal.png')).convert('RGBA')
    def crystal(size):
        side = int(size * 16 / 14)
        return LOGO.resize((side, side), Image.LANCZOS)

def glow(img, centre, radius, colour, strength):
    layer = Image.new('RGBA', img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    x, y = centre
    d.ellipse([x - radius, y - radius, x + radius, y + radius], fill=colour + (strength,))
    layer = layer.filter(ImageFilter.GaussianBlur(radius * 0.6))
    img.alpha_composite(layer)

def wizard(w, h):
    img = Image.new('RGBA', (w, h), BG + (255,))
    glow(img, (w * 0.3, h * 0.25), w * 0.75, VIOLET, 110)
    glow(img, (w * 0.8, h * 0.7), w * 0.6, CYAN, 70)
    c = crystal(int(w * 0.38))
    img.alpha_composite(c, (int((w - c.width) / 2), int(h * 0.36 - c.height / 2)))
    return img.convert('RGB')




# Inno: one file per scale (100, 125, 150, 200%), listed comma-separated in Ivo.iss
for scale, (ww, wh, _) in {100: (164, 314, 55), 125: (192, 386, 64), 150: (246, 459, 83), 200: (328, 628, 110)}.items():
    wizard(ww, wh).save(os.path.join(ART, f'wizard-{scale}.bmp'))
print('written to', os.path.abspath(ART))

# ---------- dark setups: Inno's page background and corner icon, and the MSI's full-dialog background ----------
def crystal_on_clear(n):   # Inno's small corner image: just the crystal, transparent around it (no tile)
    img = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    c = crystal(int(n * 0.66))
    img.alpha_composite(c, (int((n - c.width) / 2), int((n - c.height) / 2)))
    return img

def page_back(w, h):        # behind Inno's pages: the canvas with a faint violet glow top-left and cyan bottom-right
    img = Image.new('RGBA', (w, h), (18, 23, 28, 255))
    glow(img, (w * 0.05, h * 0.0), w * 0.45, VIOLET, 45)
    glow(img, (w * 1.0, h * 1.0), w * 0.4, CYAN, 28)
    return img.convert('RGB')

def msi_full(w, h, mark=True):   # every MSI dialog: the whole 493 x 312 canvas, art on the left on the first and last page
    img = Image.new('RGBA', (w, h), (18, 23, 28, 255))
    glow(img, (w * 0.0, h * 0.0), w * 0.5, VIOLET, 55)
    glow(img, (w * 1.0, h * 1.0), w * 0.45, CYAN, 30)
    if mark:
        side = wizard(164, h - 49).convert('RGBA')   # the bottom 49 px stay plain for the buttons
        img.alpha_composite(side, (0, 0))
    else:
        c = crystal(26)
        img.alpha_composite(c, (w - c.width - 22, 18))
    line = Image.new('RGBA', (w, 1), (44, 56, 67, 255))   # a quiet line above the buttons
    img.alpha_composite(line, (0, h - 49))
    return img.convert('RGB')

for scale, n in {100: 55, 125: 64, 150: 83, 200: 110}.items():
    crystal_on_clear(n).save(os.path.join(ART, f'small-{scale}.png'))
page_back(1000, 800).save(os.path.join(ART, 'back.png'))
msi_full(493, 312, mark=True).save(os.path.join(ART, 'msi-side.bmp'))
msi_full(493, 312, mark=False).save(os.path.join(ART, 'msi-plain.bmp'))
print('dark art written')

# the MSI's few stock dialogs (browse for a folder, files in use, errors) draw black text, so theirs stay light:
# a white banner with the crystal on the right, and a white page with Ivo's art panel on the left
def light_banner(w, h):
    img = Image.new('RGBA', (w, h), (255, 255, 255, 255))
    c = crystal(int(h * 0.62))
    img.alpha_composite(c, (w - c.width - int(h * 0.3), int((h - c.height) / 2)))
    return img.convert('RGB')

def light_side(w, h):
    img = Image.new('RGBA', (w, h), (255, 255, 255, 255))
    img.alpha_composite(wizard(164, h).convert('RGBA'), (0, 0))
    return img.convert('RGB')

light_banner(493, 58).save(os.path.join(ART, 'msi-stock-banner.bmp'))
light_side(493, 312).save(os.path.join(ART, 'msi-stock-side.bmp'))
print('stock MSI art written')
