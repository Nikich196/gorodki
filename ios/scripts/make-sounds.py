#!/usr/bin/env python3
"""Звуки бренда «Крепость» (Никита 15.10: «добавь звуки в тему») — синтез, без чужих записей и лицензий.

Пишет короткие WAV (22 050 Гц, 16 бит, моно) в App/Resources/Sounds. Запуск из ios/: python3 scripts/make-sounds.py.
Только стандартная библиотека — пересобрать можно на любом компьютере.

- start   — старт как на стадионе: два коротких сигнала и длинный выше;
- capture — захват: восходящий колокольный аккорд до–ми–соль–до;
- rank    — пьедестал: короткие фанфары;
- chime   — знак прорисовался: мягкий колокольчик;
- swoosh  — листание интро: «свуш» шума с плывущим фильтром;
- tap     — нажатие главной кнопки: тихий щелчок;
- toggle  — отметка: двойной «поп».
"""

import math
import os
import random
import struct
import wave

RATE = 22_050
OUT = os.path.join(os.path.dirname(__file__), "..", "App", "Resources", "Sounds")


def silence(seconds):
    return [0.0] * int(RATE * seconds)


def mix(into, sound, at):
    start = int(RATE * at)
    if len(into) < start + len(sound):
        into.extend([0.0] * (start + len(sound) - len(into)))
    for i, value in enumerate(sound):
        into[start + i] += value


def tone(freq, seconds, harmonics=((1, 1.0),), attack=0.005, release=0.03, decay=None):
    """Тон с гармониками; огибающая — атака, затухание (экспонента `decay` с) и спад в конце."""
    n = int(RATE * seconds)
    out = []
    for i in range(n):
        t = i / RATE
        value = sum(amp * math.sin(2 * math.pi * freq * mult * t) for mult, amp in harmonics)
        env = min(1.0, t / attack) if attack > 0 else 1.0
        if decay:
            env *= math.exp(-t / decay)
        tail = seconds - t
        if tail < release:
            env *= max(0.0, tail / release)
        out.append(value * env)
    return out


def bell(freq, seconds, decay=0.6):
    """Колокол: негармонические обертоны, верхние гаснут быстрее."""
    partials = ((1.0, 1.0, decay), (2.0, 0.45, decay * 0.6), (2.76, 0.25, decay * 0.45), (5.4, 0.1, decay * 0.25))
    n = int(RATE * seconds)
    out = []
    for i in range(n):
        t = i / RATE
        value = 0.0
        for mult, amp, tau in partials:
            value += amp * math.sin(2 * math.pi * freq * mult * t) * math.exp(-t / tau)
        out.append(value * min(1.0, t / 0.002))
    return out


def normalize(samples, peak):
    top = max(abs(v) for v in samples) or 1.0
    return [v / top * peak for v in samples]


def write(name, samples, peak=0.7):
    samples = normalize(samples, peak)
    path = os.path.join(OUT, name + ".wav")
    with wave.open(path, "wb") as file:
        file.setnchannels(1)
        file.setsampwidth(2)
        file.setframerate(RATE)
        file.writeframes(b"".join(struct.pack("<h", int(max(-1.0, min(1.0, v)) * 32767)) for v in samples))
    print(f"{name}.wav  {len(samples) / RATE:.2f} с")


def start():
    beep = ((1, 1.0), (3, 0.22))
    out = silence(1.2)
    mix(out, tone(660, 0.11, beep), 0.0)
    mix(out, tone(660, 0.11, beep), 0.32)
    mix(out, tone(1320, 0.42, beep, release=0.12), 0.64)
    return out


def capture():
    out = silence(1.6)
    for index, freq in enumerate((523.25, 659.25, 783.99, 1046.5)):
        mix(out, [v * (0.8 + 0.1 * index) for v in bell(freq, 1.3)], index * 0.075)
    return out


def rank():
    brass = ((1, 1.0), (3, 0.33), (5, 0.18), (7, 0.08))
    out = silence(1.2)
    notes = ((392.0, 0.0, 0.14), (523.25, 0.15, 0.14), (659.25, 0.3, 0.14), (783.99, 0.45, 0.6))
    for freq, at, length in notes:
        mix(out, tone(freq, length, brass, attack=0.01, release=0.08), at)
    return out


def chime():
    out = silence(1.0)
    mix(out, bell(783.99, 0.95, decay=0.45), 0.0)
    mix(out, [v * 0.5 for v in bell(1174.66, 0.8, decay=0.35)], 0.05)
    return out


def swoosh():
    rnd = random.Random(15)
    seconds = 0.36
    n = int(RATE * seconds)
    out = []
    low = 0.0
    for i in range(n):
        t = i / seconds / RATE
        cutoff = 300 + 2700 * math.sin(math.pi * t)  # фильтр плывёт вверх и обратно
        alpha = 1 - math.exp(-2 * math.pi * cutoff / RATE)
        low += alpha * (rnd.uniform(-1, 1) - low)
        env = math.sin(math.pi * t) ** 1.5
        out.append(low * env)
    return out


def tap():
    return tone(1800, 0.045, ((1, 1.0), (2, 0.3)), attack=0.001, release=0.01, decay=0.009)


def toggle():
    out = silence(0.12)
    mix(out, tone(1200, 0.035, ((1, 1.0),), attack=0.001, release=0.01, decay=0.012), 0.0)
    mix(out, tone(1600, 0.04, ((1, 1.0),), attack=0.001, release=0.012, decay=0.014), 0.045)
    return out


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    write("start", start(), 0.7)
    write("capture", capture(), 0.7)
    write("rank", rank(), 0.6)
    write("chime", chime(), 0.45)
    write("swoosh", swoosh(), 0.4)
    write("tap", tap(), 0.35)
    write("toggle", toggle(), 0.35)
