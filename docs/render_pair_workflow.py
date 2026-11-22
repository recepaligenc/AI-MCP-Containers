"""Render the README workflow with Python 3 and Pillow; no service calls are made."""

from pathlib import Path
import math

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
ASSETS = ROOT / "assets"
WIDTH, HEIGHT, SCALE = 1000, 560, 2
FPS, PHASE_SECONDS = 10, 4
SCENE_HOLD_MS, FINAL_HOLD_MS = 2000, 8000
GIF_SIZE_LIMIT = 1_500_000
FONT_ROOT = Path("/usr/share/fonts/truetype/dejavu")
COLORS = {
    "background": "#F5F7FB", "ink": "#17243A", "muted": "#5B6980",
    "line": "#DDE4EF", "white": "#FFFFFF", "blue": "#4967D9",
    "claude": "#B9613F", "claude_light": "#FAEEE6",
    "codex": "#087F79", "codex_light": "#E4F3F0",
    "skin": "#D9B18F", "hair": "#374151", "shirt_a": "#667F9B",
    "shirt_b": "#7D8BA5", "wood": "#DCC4A5", "floor": "#E9EEF5",
}
STAGES = ("Plan & critique", "Choose & implement", "Verify & iterate")
PHASES = [
    {"name": "Agree the goal, critique the plan",
     "workspace": "Keep the authorized outcome in view",
     "detail": "Strictly critique this plan. A supported READY is valid; do not invent defects.",
     "speech": ["Let's agree the goal.", "Strictly critique this plan."]},
    {"name": "Evidence-led revisions",
     "workspace": "Host verifies findings before applying fixes",
     "detail": "Security + performance + architectural fit. New scope needs user approval.",
     "speech": ["What evidence supports that risk?", "Verify first. Keep the change focused."]},
    {"name": "For UI: choose first",
     "workspace": "User approval before building",
     "detail": "For UI design: three choices, explicit approval, existing fonts and simple flows.",
     "speech": ["Three options. Keep familiar fonts.", "Clear labels. Ask the user."]},
    {"name": "Host applies changes",
     "workspace": "Host applies justified, in-scope changes",
     "detail": "Providers remain read-only. Routine authorized choices need no repeated approval.",
     "speech": ["Does this fit the approved goal?", "Yes. Propose only the necessary patch."]},
    {"name": "Reuse & verify",
     "workspace": "Reuse passed checks; run justified changes",
     "detail": "For UI: host inspects rendered views + working controls, feedback and error states.",
     "speech": ["Reuse the checks that already passed.", "Verify changes, UI views and controls."]},
    {"name": "A joint recommendation, with evidence",
     "workspace": "READY only when required gates pass",
     "detail": "Critique this implementation and test evidence. Stop if review is blocked or stalled.",
     "speech": ["User acceptance and evidence are recorded.", "Joint recommendation: ready to report."]},
]
STEP_LABELS = ("Goal", "Critique", "UI choice", "Propose", "Verify", "Deliver")
BENEFITS = ("Less unnecessary change", "Earlier risk discovery",
            "Consistent, usable UI", "Less repeated testing")


def font(size, bold=False):
    name = "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"
    return ImageFont.truetype(str(FONT_ROOT / name), size * SCALE)


FONTS = {size: font(size) for size in (12, 13, 14)}
FONTS.update({f"b{size}": font(size, True) for size in (12, 13, 14, 16, 18, 27)})


def box(draw, bounds, fill, outline=None, radius=16, width=1):
    draw.rounded_rectangle(tuple(int(v * SCALE) for v in bounds), radius=radius * SCALE,
                           fill=fill, outline=outline, width=width * SCALE)


def text(draw, xy, value, selected_font, fill="ink", anchor="la"):
    draw.text(tuple(int(v * SCALE) for v in xy), value, font=FONTS[selected_font],
              fill=COLORS.get(fill, fill), anchor=anchor)


def line(draw, points, fill="line", width=2):
    draw.line([(int(x * SCALE), int(y * SCALE)) for x, y in points],
              fill=COLORS.get(fill, fill), width=width * SCALE)


def progress_bounds(index):
    width, gap = 40, 8
    start = (WIDTH - (width * len(PHASES) + gap * (len(PHASES) - 1))) / 2
    left = start + index * (width + gap)
    return left, left + width


def ui_options(draw, top, approved=False, left=278, width=132, gap=24):
    # These are illustrative wireframes, not a product design or automatic approval.
    for index in range(3):
        x = left + index * (width + gap)
        chosen = approved and index == 1
        box(draw, (x, top, x + width, top + 40),
            COLORS["codex_light"] if chosen else COLORS["white"],
            COLORS["codex"] if chosen else COLORS["line"], radius=6, width=2 if chosen else 1)
        text(draw, (x + 8, top + 10), chr(65 + index), "b14", "codex" if chosen else "muted")
        line(draw, [(x + 32, top + 9), (x + width - 10, top + 9)], "muted", 2)
        if index == 0:
            box(draw, (x + 32, top + 16, x + width - 10, top + 31), COLORS["codex_light"], radius=2)
        elif index == 1:
            for offset in (16, 23, 30):
                line(draw, [(x + 32, top + offset), (x + width - 10, top + offset)], "muted", 1)
        else:
            box(draw, (x + 32, top + 16, x + 47, top + 32), COLORS["claude_light"], radius=2)
            line(draw, [(x + 55, top + 18), (x + width - 10, top + 18)], "muted", 1)
            line(draw, [(x + 55, top + 28), (x + width - 10, top + 28)], "muted", 1)


def disc(draw, center, radius, color):
    x, y = center
    draw.ellipse(tuple(int(v * SCALE) for v in (x - radius, y - radius, x + radius, y + radius)),
                 fill=COLORS.get(color, color))


def limb(draw, shoulder, hand, facing, shirt, length=60):
    dx, dy = hand[0] - shoulder[0], hand[1] - shoulder[1]
    distance = max(1, math.hypot(dx, dy))
    bend = math.sqrt(max(0, length ** 2 - (distance / 2) ** 2))
    elbow = ((shoulder[0] + hand[0]) / 2 - facing * dy / distance * bend,
             (shoulder[1] + hand[1]) / 2 + facing * dx / distance * bend)
    line(draw, [shoulder, elbow], shirt, 18)
    disc(draw, elbow, 9, shirt)
    line(draw, [elbow, hand], "skin", 13)
    disc(draw, hand, 6.5, "skin")


def person(draw, cx, facing, shirt, fraction, final=False):
    # Shared neutral features represent personified agents, not actual people.
    nod = math.sin(fraction * math.pi * 2) * 1.5 if not final else 0
    head_y = 292 + nod
    box(draw, (cx - 42, 335, cx + 42, 431), COLORS["line"], radius=18)
    box(draw, (cx - 27, 398, cx + 27, 439), COLORS["hair"], radius=12)
    box(draw, (cx - 10, 313 + nod, cx + 10, 340), COLORS["skin"], radius=6)
    box(draw, (cx - 34, 330, cx + 34, 412), COLORS[shirt], radius=20)
    line(draw, [(cx - 9, 333), (cx, 343), (cx + 9, 333)], "white", 3)
    limb(draw, (cx - facing * 27, 345), (cx - facing * 46, 405), facing, shirt, length=45)
    disc(draw, (cx, head_y), 27, "skin")
    disc(draw, (cx - 26, head_y + 2), 5, "skin")
    disc(draw, (cx + 26, head_y + 2), 5, "skin")
    draw.pieslice(tuple(int(v * SCALE) for v in (cx - 28, head_y - 29, cx + 28, head_y + 19)),
                  180, 355, fill=COLORS["hair"])
    for eye_x in (cx - 9 + facing * 2, cx + 9 + facing * 2):
        disc(draw, (eye_x, head_y + 1), 1.8, "ink")
    line(draw, [(cx + facing * 3, head_y + 4), (cx + facing * 6, head_y + 10)], "muted", 1)
    draw.arc(tuple(int(v * SCALE) for v in (cx - 9, head_y + 8, cx + 9, head_y + 18)),
             5, 175, fill=COLORS["ink"], width=2 * SCALE)
    if final:
        reach = min(1.0, fraction / 0.6)
        reach = 0.5 - 0.5 * math.cos(math.pi * reach)
        hand = (cx + facing * 72 + (500 - cx - facing * 72) * reach,
                400 + (381 - 400) * reach)
    else:
        talking = (fraction < 0.5) == (facing == 1)
        hand = (cx + facing * (86 if talking else 72),
                369 + math.sin(fraction * math.pi * 2) * 7 if talking else 400)
    limb(draw, (cx + facing * 27, 345), hand, facing, shirt, length=85 if final else 60)


def speech(draw, left, speaker, value, active):
    box(draw, (left, 184, left + 300, 250), COLORS["white"],
        COLORS["blue"] if active else COLORS["line"], radius=12, width=2 if active else 1)
    text(draw, (left + 14, 192), speaker, "b13", "blue" if active else "muted")
    words, lines = value.split(), []
    while words:
        value = ""
        while words and draw.textlength(value + (" " if value else "") + words[0], font=FONTS[13]) <= 272 * SCALE:
            value += (" " if value else "") + words.pop(0)
        lines.append(value)
    for index, value in enumerate(lines):
        text(draw, (left + 14, 213 + index * 18), value, 13)


def check_row(draw, y, value, done=True):
    disc(draw, (384, y + 7), 7, "codex_light" if done else "line")
    if done:
        line(draw, [(381, y + 7), (384, y + 10), (388, y + 4)], "codex", 2)
    text(draw, (399, y), value, 14)


def project_board(draw, phase_index, fraction):
    box(draw, (356, 181, 644, 353), COLORS["line"], radius=14)
    box(draw, (360, 177, 640, 349), COLORS["white"], COLORS["line"], radius=14)
    if phase_index == 0:
        text(draw, (500, 192), "Shared project brief", "b16", anchor="ma")
        for y, value in zip((226, 254, 282), ("Goal + scope", "Acceptance criteria", "Strict plan critique")):
            check_row(draw, y, value)
        text(draw, (500, 323), "The user's goal stays stable", 12, "muted", anchor="ma")
    elif phase_index == 1:
        text(draw, (500, 192), "Discuss, verify, revise", "b16", anchor="ma")
        for y, value in zip((226, 254, 282), ("Required: evidence-backed", "Optional: record, don't add", "Unverified: investigate narrowly")):
            text(draw, (375, y), value, 13, "muted")
        text(draw, (500, 323), "Security · performance · scope", 12, "blue", anchor="ma")
    elif phase_index == 2:
        text(draw, (500, 192), "For UI: compare 3 options", "b16", anchor="ma")
        text(draw, (500, 222), "Existing fonts · simple flows", 13, "muted", anchor="ma")
        ui_options(draw, 246, approved=fraction >= 0.6, left=378, width=76, gap=8)
        box(draw, (385, 296, 615, 319), COLORS["codex_light"] if fraction >= 0.6 else COLORS["background"], radius=6)
        text(draw, (500, 299), "USER approved option B" if fraction >= 0.6 else "Waiting for USER approval",
             "b13", "codex" if fraction >= 0.6 else "muted", anchor="ma")
        text(draw, (500, 329), "Labels · focus · responsive layouts", 12, "muted", anchor="ma")
    elif phase_index == 3:
        text(draw, (500, 192), "Host workspace", "b16", anchor="ma")
        for index, value in enumerate(("Reviewed plan", "Minimal patch proposal", "Host applies in-scope edits")):
            check_row(draw, 226 + index * 28, value, fraction >= index * 0.2)
        text(draw, (500, 323), "Providers propose; host writes", 12, "muted", anchor="ma")
    elif phase_index == 4:
        text(draw, (500, 192), "Verification evidence", "b16", anchor="ma")
        for y, value in zip((226, 254, 282), ("Passed checks: reused", "Affected checks: verified", "For UI: views + controls")):
            check_row(draw, y, value)
        text(draw, (500, 323), "Illustrative results, not a live run", 12, "muted", anchor="ma")
    else:
        text(draw, (500, 188), "Example shared delivery", "b16", anchor="ma")
        for index, value in enumerate(("Reviewed plan", "Minimal patch", "Verification evidence")):
            if fraction >= index * 0.16:
                check_row(draw, 216 + index * 25, value)
        if fraction >= 0.4:
            box(draw, (378, 295, 622, 318), COLORS["codex_light"], radius=6)
            text(draw, (500, 299), "USER acceptance recorded", "b13", "codex", anchor="ma")
        if fraction >= 0.7:
            text(draw, (500, 327), "READY · joint recommendation", "b13", "blue", anchor="ma")


def office_frame(phase_index, fraction, overview=False):
    phase = PHASES[phase_index]
    canvas = Image.new("RGB", (WIDTH * SCALE, HEIGHT * SCALE), COLORS["background"])
    draw = ImageDraw.Draw(canvas)
    text(draw, (36, 22), "Claude + Codex: think, challenge, build", "b27")
    text(draw, (36, 62), "Illustrative agents: providers review read-only; the host applies changes and justified checks", 14, "muted")
    for index, label in enumerate(STEP_LABELS if overview else STAGES):
        left, width = (36 + index * 156, 148) if overview else (36 + index * 318, 292)
        active = overview or index == phase_index // 2
        box(draw, (left, 99, left + width, 132), COLORS["blue"] if active else COLORS["white"],
            None if active else COLORS["line"], radius=9)
        text(draw, (left + 12, 108), f"{index + 1:02}  {label}", "b13", "white" if active else "muted")
    text(draw, (500, 147), phase["name"], "b18", "blue", anchor="ma")
    line(draw, [(36, 438), (964, 438)], "line", 2)
    final = phase_index == 5
    approach = min(1, fraction / 0.45) if final else 0
    approach = 0.5 - 0.5 * math.cos(math.pi * approach)
    left_x, right_x = 230 + 90 * approach, 770 - 90 * approach
    person(draw, left_x, 1, "shirt_a", fraction, final)
    person(draw, right_x, -1, "shirt_b", fraction, final)
    project_board(draw, phase_index, fraction)
    box(draw, (170, 409, 830, 425), COLORS["wood"], radius=6)
    for x in (202, 786):
        box(draw, (x, 425, x + 12, 461), COLORS["muted"], radius=4)
    for x in (270, 642):
        height = 34 * (1 - approach)
        box(draw, (x, 407 - height, x + 88, 409), COLORS["hair"], radius=5)
        if height > 12:
            line(draw, [(x + 12, 392 - height / 2), (x + 70, 392 - height / 2)], "line", 2)
        box(draw, (x - 8, 408, x + 96, 414), COLORS["muted"], radius=3)
    if final and fraction >= 0.6:
        # Both forearms end at one fixed clasp point; a single silhouette joins them.
        box(draw, (488, 374, 512, 388), COLORS["skin"], COLORS["muted"], radius=6)
        line(draw, [(500, 378), (505, 383), (500, 386)], "muted", 1)
    speech(draw, 36, "Claude · agent A", phase["speech"][0], fraction < 0.5)
    speech(draw, 664, "Codex · agent B", phase["speech"][1], fraction >= 0.5)
    text(draw, (230, 446), "Claude", "b16", anchor="ma")
    text(draw, (770, 446), "Codex", "b16", anchor="ma")
    box(draw, (300, 440, 700, 473), COLORS["white"], COLORS["line"], radius=9)
    result = "Reviewed plan + patch + evidence" if final else phase["workspace"]
    text(draw, (500, 449), result, 13, anchor="ma")
    if final:
        text(draw, (500, 477), "Usage by agent + coverage · Credits unavailable · Intended benefits", "b12", "muted", anchor="ma")
        for index, label in enumerate(BENEFITS):
            left = 36 + index * 236
            box(draw, (left, 494, left + 220, 526), COLORS["white"], COLORS["line"], radius=8)
            text(draw, (left + 110, 503), label, 12, "codex", anchor="ma")
    elif phase_index == 4:
        text(draw, (500, 493), "Illustration: 10 main calls + 2 reviews = 20% more requests", 13, "muted", anchor="ma")
        text(draw, (500, 516), "20% cost only at equal average request cost · Actual usage varies by model and context",
             12, "muted", anchor="ma")
    else:
        text(draw, (500, 493), phase["detail"], 13, "muted", anchor="ma")
        text(draw, (500, 516), "Up to 20 requests · Evidence before fixes · New scope needs user authority",
             12, "muted", anchor="ma")
    for index in range(len(PHASES)):
        left, right = progress_bounds(index)
        box(draw, (left, 539, right, 543), COLORS["line"], radius=2)
    left, right = progress_bounds(phase_index)
    box(draw, (left, 539, left + max(4, (right - left) * fraction), 543), COLORS["blue"], radius=2)
    return canvas.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    bases = [office_frame(index, 1.0) for index in range(len(PHASES))]
    palette_source = Image.new("RGB", (WIDTH, HEIGHT * len(bases)))
    for index, base in enumerate(bases):
        palette_source.paste(base, (0, HEIGHT * index))
    palette = palette_source.quantize(colors=128, method=Image.Quantize.MEDIANCUT)
    frames = []
    for phase_index in range(len(PHASES)):
        for index in range(FPS * PHASE_SECONDS):
            frame = office_frame(phase_index, index / (FPS * PHASE_SECONDS - 1))
            frames.append(frame.quantize(palette=palette, dither=Image.Dither.NONE))
    durations = [1000 // FPS] * len(frames)
    for index in range(len(PHASES)):
        durations[(index + 1) * FPS * PHASE_SECONDS - 1] += FINAL_HOLD_MS if index == len(PHASES) - 1 else SCENE_HOLD_MS
    frames[0].save(ASSETS / "pair-workflow.gif", save_all=True, append_images=frames[1:],
                   duration=durations, loop=0, optimize=True, disposal=1)
    office_frame(len(PHASES) - 1, 1.0, overview=True).save(ASSETS / "pair-workflow.png", optimize=True)
    if (ASSETS / "pair-workflow.gif").stat().st_size > GIF_SIZE_LIMIT:
        raise RuntimeError("GIF exceeds the 1.5 MB documentation asset budget")
    print(f"Rendered {len(frames)} frames and a static overview in {ASSETS}")


if __name__ == "__main__":
    main()
