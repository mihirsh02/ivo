# Ivo's ringtone, "crystal bloom": a glassy bell phrase that climbs like the crystal catching light (E5 G#5 B5 E6),
# answers itself a step lower (D#6 B5 G#5), then a soft shimmer; twice, about 4.6 s, made to loop without a seam.
# Bell tones by FM (a sine bending a sine, as a celesta rings), a touch of echo for space.
# Writes ..\..\Assets\ring.wav (44.1 kHz, 16-bit mono). Run: python make_ringtone.py
import math, os, struct, wave

RATE = 44100
LENGTH = 4.6

def bell(freq, start, length, gain, buf):
    """a glassy bell: a carrier whose brightness (FM index) fades faster than its ring, quick soft attack"""
    s0 = int(start * RATE)
    for i in range(int(length * RATE)):
        j = s0 + i
        if j >= len(buf): break
        t = i / RATE
        index = 2.4 * math.exp(-t * 6)                         # bright at the strike, mellow as it rings
        mod = math.sin(2 * math.pi * freq * 3.5 * t) * index   # an inharmonic ratio: glass, not a piano
        tone = math.sin(2 * math.pi * freq * t + mod) + 0.25 * math.sin(2 * math.pi * freq * 2 * t) * math.exp(-t * 9)
        env = min(1.0, t / 0.005) * math.exp(-t * 3.2)
        buf[j] += tone * env * gain

buf = [0.0] * int(LENGTH * RATE)
E5, Gs5, B5, E6, Ds6 = 659.25, 830.61, 987.77, 1318.51, 1244.51
phrase = [(E5, 0.00), (Gs5, 0.13), (B5, 0.26), (E6, 0.39), (Ds6, 0.78), (B5, 0.91), (Gs5, 1.04)]
for half in (0.0, 2.3):
    for f, at in phrase:
        bell(f, half + at, 1.1, 0.5, buf)
    bell(E6 * 2, half + 1.30, 0.6, 0.12, buf)   # the shimmer: a faint high sparkle
    bell(B5 * 2, half + 1.38, 0.6, 0.09, buf)

# a little room: two quiet echoes
for delay, g in ((0.11, 0.22), (0.23, 0.1)):
    d = int(delay * RATE)
    for i in range(len(buf) - 1, d - 1, -1):
        buf[i] += buf[i - d] * g

peak = max(abs(x) for x in buf) or 1
scale = 0.6 / peak
tail = int(0.25 * RATE)                       # fade the very end, so the loop joins softly
for i in range(tail): buf[-tail + i] *= 1 - i / tail

out = os.path.join(os.path.dirname(__file__), "..", "..", "Assets", "ring.wav")
with wave.open(out, "wb") as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
    w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, x * scale)) * 32767)) for x in buf))
print("wrote", os.path.abspath(out))
