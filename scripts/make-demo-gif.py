#!/usr/bin/env python3
"""Builds docs/assets/gatekeeper-demo.gif from frames captured by the M6 end-to-end test.

The frames come from the test named below, so each one shows a state that test asserted. It is a sequence of captured
frames, not a screen recording. Needs Pillow, and a Chromium browser for the dashboard. It is a documentation tool only.

Usage, from the repository root:   python scripts/make-demo-gif.py
Set LEEWAY_BROWSER if the browser is not in one of the usual places.
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

TEST_FILTER = "Two_offline_gates_cannot_overfill_a_zone_and_a_duplicate_ticket_is_flagged"
STEPS = [
    ("1-online", "Online. North Stand holds 10. Each gate leases 4, and 2 stay in the pool."),
    ("2-offline", "Server down. Each gate spends its lease with no signal. Beyond that, scans are provisional. T-1001 is scanned at both gates."),
    ("3-gate-a-back", "Gate A reconnects. Its spends reach the server, and its provisional scan is funded from the pool."),
    ("4-reconciled", "Both back. 10 of 10 used, never over. The last provisional scan was turned away, and the duplicate T-1001 is flagged everywhere."),
]
HOLD_MS = 4500
SCALE_HEIGHT = 520


def find_browser():
    candidates = [
        os.environ.get("LEEWAY_BROWSER"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        "/usr/bin/google-chrome",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    ]
    return next((c for c in candidates if c and Path(c).exists()), None)


def font(size):
    for name in ("segoeui.ttf", "DejaVuSans.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def wrap(draw, text, face, width):
    lines, line = [], ""
    for word in text.split():
        trial = f"{line} {word}".strip()
        if draw.textlength(trial, font=face) <= width:
            line = trial
        else:
            lines.append(line)
            line = word
    return lines + [line]


def fit(image):
    image = image.convert("RGB")
    return image.resize((round(image.width * SCALE_HEIGHT / image.height), SCALE_HEIGHT), Image.LANCZOS)


def compose(frames, step, caption, number):
    panels = [fit(Image.open(frames / f"{step}-{name}.png")) for name in ("a", "b", "dashboard")]
    gap = 10
    width = sum(p.width for p in panels) + gap * (len(panels) + 1)
    probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    face = font(22)
    lines = wrap(probe, f"{number} of {len(STEPS)}. {caption}", face, width - 2 * gap)
    top = 14 + 30 * len(lines) + 6
    canvas = Image.new("RGB", (width, SCALE_HEIGHT + top + gap), "#1a1a19")
    draw = ImageDraw.Draw(canvas)
    for i, line in enumerate(lines):
        draw.text((gap, 12 + 30 * i), line, font=face, fill="#f0efec")
    x = gap
    for panel in panels:
        canvas.paste(panel, (x, top))
        x += panel.width + gap
    return canvas


def main():
    root = Path(__file__).resolve().parent.parent
    browser = find_browser()
    if browser is None:
        print("No browser found. Set LEEWAY_BROWSER.")
        return 1

    with tempfile.TemporaryDirectory(prefix="leeway-demo-") as temp:
        frames = Path(temp)
        env = dict(os.environ, LEEWAY_CAPTURE_DIR=str(frames), LEEWAY_BROWSER=browser, AVALONIA_TELEMETRY_OPTOUT="1")
        run = subprocess.run(
            ["dotnet", "test", "--project", "tests/GateKeeper.Desktop.Tests", "--", "--filter-method", f"*{TEST_FILTER}*"],
            cwd=root, env=env, capture_output=True, text=True, timeout=400)
        if run.returncode != 0:
            print(run.stdout[-2000:])
            print("FAILED: the demo test did not pass, so its frames do not count.")
            return 1

        images = [compose(frames, step, caption, i + 1) for i, (step, caption) in enumerate(STEPS)]
        target = root / "docs" / "assets" / "gatekeeper-demo.gif"
        target.parent.mkdir(parents=True, exist_ok=True)
        palette = [im.convert("P", palette=Image.ADAPTIVE, colors=128) for im in images]
        palette[0].save(target, save_all=True, append_images=palette[1:], duration=HOLD_MS, loop=0, optimize=True)
        for (step, _), image in zip(STEPS, images):
            image.save(target.parent / f"gatekeeper-{step}.png", optimize=True)
        print(f"Wrote {target} ({target.stat().st_size // 1024} KB)")
        return 0


if __name__ == "__main__":
    sys.exit(main())
