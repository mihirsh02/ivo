# Ivo's chime: two quick, soft mallet notes rising a fifth (A5 → E6), ~0.28 s, gentle attack and fade.
# Writes ..\..\Assets\chime.wav (44.1 kHz, 16-bit mono). Run: python make_chime.py
import math, os, struct, wave

RATE = 44100

def note(freq, start, length, gain):
    """a mallet-like tone: the fundamental plus a soft octave and a faint 3rd harmonic, quick attack, exponential fade"""
    out = []
    n = int(length * RATE)
    for i in range(n):
        t = i / RATE
        attack = min(1.0, t / 0.004)                 # 4 ms: no click
        fade = math.exp(-t * 18)                     # a short, soft ring
        s = (math.sin(2 * math.pi * freq * t)
             + 0.35 * math.sin(2 * math.pi * freq * 2 * t) * math.exp(-t * 30)
             + 0.10 * math.sin(2 * math.pi * freq * 3 * t) * math.exp(-t * 45))
        out.append((int(start * RATE) + i, s * attack * fade * gain))
    return out

length = 0.30
buf = [0.0] * int(length * RATE)
for idx, v in note(880.0, 0.000, 0.22, 0.55) + note(1318.5, 0.075, 0.225, 0.50):   # A5, then E6
    if idx < len(buf): buf[idx] += v

peak = max(abs(x) for x in buf) or 1
scale = 0.42 / peak                                  # quiet: a chime, not an alarm
tail = int(0.03 * RATE)                              # 30 ms fade to silence at the very end
for i in range(tail): buf[-tail + i] *= 1 - i / tail

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', 'chime.wav')
with wave.open(out, 'wb') as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
    w.writeframes(b''.join(struct.pack('<h', int(max(-1, min(1, x * scale)) * 32767)) for x in buf))
print('wrote', os.path.normpath(out), os.path.getsize(out), 'bytes')
