# Ivo Chats' stickers: Microsoft's Fluent Emoji (3D), MIT licence (github.com/microsoft/fluentui-emoji), saved as
# Assets/stickers/<codepoints>.png, made 160 px. Run: python tools/stickers/fetch.py
import io, os, urllib.parse, urllib.request
from PIL import Image
here = os.path.dirname(os.path.abspath(__file__))
out = os.path.join(here, "..", "..", "Assets", "stickers")
os.makedirs(out, exist_ok=True)
STICKERS = {  # the sticker (as sent in a message) -> its Fluent Emoji folder (hands use the default skin tone)
    "😀": "Grinning face", "😂": "Face with tears of joy", "🥹": "Face holding back tears", "😍": "Smiling face with heart-eyes",
    "😎": "Smiling face with sunglasses", "🤔": "Thinking face", "😴": "Sleeping face", "😭": "Loudly crying face", "😡": "Pouting face",
    "🤯": "Exploding head", "🥳": "Partying face", "🙏": "Folded hands", "👍": "Thumbs up", "👎": "Thumbs down", "👏": "Clapping hands",
    "🙌": "Raising hands", "💪": "Flexed biceps", "🔥": "Fire", "✨": "Sparkles", "🎉": "Party popper", "❤️": "Red heart",
    "💔": "Broken heart", "☕": "Hot beverage", "🍕": "Pizza", "🚀": "Rocket", "💯": "Hundred points", "👀": "Eyes", "🤝": "Handshake",
}
base = "https://raw.githubusercontent.com/microsoft/fluentui-emoji/main/assets/"
def get(url):
    with urllib.request.urlopen(url, timeout=30) as r: return r.read()
for emoji, folder in STICKERS.items():
    name = folder.lower().replace(" ", "_")
    tries = [f"{folder}/3D/{name}_3d.png", f"{folder}/Default/3D/{name}_3d_default.png"]
    data = None
    for t in tries:
        try: data = get(base + urllib.parse.quote(t)); break
        except Exception: pass
    key = "-".join(f"{ord(c):x}" for c in emoji if ord(c) != 0xFE0F)
    if data is None: print("missing", folder); continue
    img = Image.open(io.BytesIO(data)).convert("RGBA").resize((160, 160), Image.LANCZOS)
    img.save(os.path.join(out, key + ".png"), optimize=True)
    print("ok", key, folder)
