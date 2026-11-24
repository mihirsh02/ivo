# Ivo's stickers from her pictures: she's cut out of the background (OpenCV's GrabCut, started from "the edges are
# background, the middle is her"), cleaned (the biggest piece, holes filled, soft edges), given a white die-cut border and
# a soft shadow, and a caption in one of several styles. Run: python make_stickers.py <photos folder> <out folder>
# Each sticker: (photo, file name, pose folder, caption, caption style, words that call for it[, where she is across the picture])
import sys, os, json, math
import cv2, numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

FONTS = r"C:\Windows\Fonts"
STICKERS = [
    ("image_bf371b8.jpg", "boss-mode", "close-up", "boss mode", "meme", "boss sassy confident smirk serving attitude"),
    ("image_c0bbe5-change-outfit-to-crop-top-and-mini-shorts-change-the-top-to-a-mini-tank-top.jpg", "cute-fit", "close-up", "cute fit", "script", "cute fit outfit look pretty"),
    ("stand (1).jpg", "working-on-it", "standing", "working on it", "neon", "working work busy coding tech on it doing", (.37, .63)),
    ("Neon Split-Hue Cyberpunk Warrior.png", "suit-up", "standing", "suit up!", "bubble", "suit up ready armor lets go battle gym"),
    ("Neon Cyberpunk Warrior Back View.png", "brb", "from-behind", "brb", "marker", "brb bye leaving later gtg cya"),
    ("ElevenLabs_image_gpt-image-2-6-sunburst_change outfit t_2026-11-08T22_38_43.png", "spicy-hey-you", "close-up", "hey you", "gradient", "hey you hi hot bikini flirt", (.36, .64)),
]

def cut_out(path, band=(.22, .78)):
    raw = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_UNCHANGED)
    if raw.ndim == 3 and raw.shape[2] == 4 and (raw[:, :, 3] < 250).mean() > 0.05:   # it already has see-through parts
        out = Image.fromarray(cv2.cvtColor(raw, cv2.COLOR_BGRA2RGBA)); return out.crop(out.getbbox())
    img = raw[:, :, :3] if raw.ndim == 3 else cv2.cvtColor(raw, cv2.COLOR_GRAY2BGR)
    h, w = img.shape[:2]
    k = 1100 / max(h, w); small = cv2.resize(img, (int(w * k), int(h * k)), interpolation=cv2.INTER_AREA) if k < 1 else img.copy()
    sh, sw = small.shape[:2]
    m = max(6, sw // 30)
    edge = np.concatenate([small[:m].reshape(-1, 3), small[-m:].reshape(-1, 3), small[:, :m].reshape(-1, 3), small[:, -m:].reshape(-1, 3)]).astype(np.float32)
    if edge.std(axis=0).mean() < 18:
        # a plain background (art on black or white): everything far enough from its colour is her
        bg = np.median(edge, axis=0)
        dist = np.linalg.norm(small.astype(np.float32) - bg, axis=2)
        fg = np.where(dist > 38, 255, 0).astype(np.uint8)
    else:
        # a scene: GrabCut, sure-background at the edges, "probably her" over the whole middle (hair included)
        mask = np.full((sh, sw), cv2.GC_PR_BGD, np.uint8)
        mask[int(sh * .02): int(sh * .97), int(sw * band[0]): int(sw * band[1])] = cv2.GC_PR_FGD
        mask[:, :int(sw * band[0] * .8)] = cv2.GC_BGD; mask[:, int(sw * (1 - (1 - band[1]) * .8)):] = cv2.GC_BGD   # well outside her: surely background
        mask[int(sh * .18): int(sh * .80), int(sw * .44): int(sw * .56)] = cv2.GC_FGD
        mask[:m, :] = mask[-m // 2:, :] = cv2.GC_BGD; mask[:, :m] = mask[:, -m:] = cv2.GC_BGD
        bgd, fgd = np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64)
        cv2.grabCut(small, mask, None, bgd, fgd, 10, cv2.GC_INIT_WITH_MASK)
        fg = np.where((mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD), 255, 0).astype(np.uint8)
        # thin bright lines (a neon grid floor, light streaks) are cut off her
        fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((7, 7), np.uint8))
    n, labels, stats, _ = cv2.connectedComponentsWithStats(fg, 8)
    if n > 1:
        biggest = 1 + np.argmax(stats[1:, cv2.CC_STAT_AREA]); fg = np.where(labels == biggest, 255, 0).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    flood = fg.copy(); cv2.floodFill(flood, np.zeros((sh + 2, sw + 2), np.uint8), (0, 0), 255); fg = fg | cv2.bitwise_not(flood)
    alpha = cv2.GaussianBlur(cv2.resize(fg, (w, h), interpolation=cv2.INTER_LINEAR), (5, 5), 0)
    rgba = cv2.cvtColor(img, cv2.COLOR_BGR2RGBA); rgba[:, :, 3] = alpha
    out = Image.fromarray(rgba)
    return out.crop(out.getbbox())

def die_cut(her, border=14):
    her.thumbnail((620, 620), Image.LANCZOS)
    pad = border * 3
    canvas = Image.new("RGBA", (her.width + pad * 2, her.height + pad * 2), (0, 0, 0, 0))
    canvas.paste(her, (pad, pad), her)
    a = canvas.split()[3]
    outline = a.filter(ImageFilter.MaxFilter(border * 2 + 1)).filter(ImageFilter.GaussianBlur(1.2))
    white = Image.new("RGBA", canvas.size, (255, 255, 255, 255)); white.putalpha(outline)
    shadow = Image.new("RGBA", canvas.size, (20, 10, 40, 0)); shadow.putalpha(outline.point(lambda v: v * 0.45).filter(ImageFilter.GaussianBlur(8)))
    out = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    out.alpha_composite(shadow, (4, 7)); out.alpha_composite(white); out.alpha_composite(canvas)
    return out

def font(name, size): return ImageFont.truetype(os.path.join(FONTS, name), size)

def caption(sticker, text, style):
    W = max(sticker.width, 420); H = sticker.height + 120
    out = Image.new("RGBA", (W, H), (0, 0, 0, 0)); out.alpha_composite(sticker, ((W - sticker.width) // 2, 0))
    d = ImageDraw.Draw(out); cx, y = W // 2, sticker.height - 34
    if style == "meme":        # bold white, black outline, like a meme
        f = font("impact.ttf", 74); d.text((cx, y), text.upper(), font=f, fill="white", stroke_width=6, stroke_fill="black", anchor="mt")
    elif style == "script":    # handwritten pink, white edge
        f = font("segoescb.ttf", 70); d.text((cx, y), text, font=f, fill=(255, 92, 190), stroke_width=7, stroke_fill="white", anchor="mt")
    elif style == "neon":      # cyan neon with a glow
        f = font("bahnschrift.ttf", 62)
        glow = Image.new("RGBA", out.size, (0, 0, 0, 0)); ImageDraw.Draw(glow).text((cx, y), text.upper(), font=f, fill=(34, 211, 238, 255), stroke_width=6, stroke_fill=(34, 211, 238, 255), anchor="mt")
        out.alpha_composite(glow.filter(ImageFilter.GaussianBlur(9))); d = ImageDraw.Draw(out)
        d.text((cx, y), text.upper(), font=f, fill=(225, 255, 255), stroke_width=3, stroke_fill=(10, 30, 60), anchor="mt")
    elif style == "bubble":    # a yellow comic speech bubble
        f = font("comicbd.ttf", 56); box = d.textbbox((cx, y + 8), text, font=f, anchor="mt"); p = 22
        d.rounded_rectangle((box[0] - p, box[1] - p, box[2] + p, box[3] + p), radius=36, fill=(255, 228, 77), outline="black", width=5)
        d.polygon([(cx - 30, box[1] - p + 2), (cx - 4, box[1] - p - 30), (cx + 12, box[1] - p + 2)], fill=(255, 228, 77), outline="black"); d.line([(cx - 28, box[1] - p + 3), (cx + 10, box[1] - p + 3)], fill=(255, 228, 77), width=6)
        d.text((cx, y + 8), text, font=f, fill="black", anchor="mt")
    elif style == "marker":    # tilted marker, violet
        f = font("Inkfree.ttf", 92); layer = Image.new("RGBA", out.size, (0, 0, 0, 0))
        ImageDraw.Draw(layer).text((cx, y - 6), text, font=f, fill=(139, 92, 255), stroke_width=7, stroke_fill="white", anchor="mt")
        out.alpha_composite(layer.rotate(-7, center=(cx, y + 40), resample=Image.BICUBIC))
    else:                      # gradient pill, pink to cyan
        f = font("seguibl.ttf", 54); box = d.textbbox((cx, y + 10), text, font=f, anchor="mt"); p = 20
        x0, y0, x1, y1 = box[0] - p * 1.6, box[1] - p, box[2] + p * 1.6, box[3] + p
        grad = Image.new("RGBA", (int(x1 - x0), int(y1 - y0)))
        for i in range(grad.width):
            t = i / max(1, grad.width - 1); c = (int(244 + (34 - 244) * t), int(63 + (211 - 63) * t), int(178 + (238 - 178) * t), 255)
            ImageDraw.Draw(grad).line([(i, 0), (i, grad.height)], fill=c)
        pill = Image.new("L", grad.size, 0); ImageDraw.Draw(pill).rounded_rectangle((0, 0, grad.width - 1, grad.height - 1), radius=grad.height // 2, fill=255)
        edge = Image.new("RGBA", (grad.width + 10, grad.height + 10), (0, 0, 0, 0)); ImageDraw.Draw(edge).rounded_rectangle((0, 0, edge.width - 1, edge.height - 1), radius=edge.height // 2, fill="white")
        out.alpha_composite(edge, (int(x0) - 5, int(y0) - 5)); out.paste(grad, (int(x0), int(y0)), pill)
        ImageDraw.Draw(out).text((cx, y + 10), text, font=f, fill="white", anchor="mt")
    return out.crop(out.getbbox())

if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    only = sys.argv[3:]
    words = {}
    for photo, name, pose, text, style, called, *band in STICKERS:
        if only and name not in only: continue
        her = cut_out(os.path.join(src, photo), *band)
        st = caption(die_cut(her), text, style)
        os.makedirs(os.path.join(dst, pose), exist_ok=True)
        st.save(os.path.join(dst, pose, name + ".png"))
        words[name] = called
        print("made", pose + "/" + name + ".png", st.size)
    json.dump(words, open(os.path.join(dst, "new-words.json"), "w"), indent=1)
