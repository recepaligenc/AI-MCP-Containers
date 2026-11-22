"""Render the README workflow with Python 3 and Pillow; no service calls are made."""

from pathlib import Path
import math

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
ASSETS = ROOT / "assets"
WIDTH, HEIGHT, SCALE = 1000, 560, 2
FPS, PHASE_SECONDS = 10, 4
FONT_ROOT = Path("/usr/share/fonts/truetype/dejavu")
COLORS = {
    "background": "#F5F7FB", "ink": "#17243A", "muted": "#5B6980",
    "line": "#DDE4EF", "white": "#FFFFFF", "blue": "#4967D9",
    "claude": "#B9613F", "claude_light": "#FAEEE6",
    "codex": "#087F79", "codex_light": "#E4F3F0",
}
PHASES = [
    {"name": "Plan & challenge", "out": "Proposed plan", "back": "Risks & revisions", "direction": 1,
     "claude": ["Draft the plan", "Goals, constraints", "and implementation steps"],
     "codex": ["Challenge the plan", "Risks, missing cases", "and suggested revisions"],
     "workspace": "Refined plan returned to the client"},
    {"name": "Propose & review", "out": "Patch proposal", "back": "Review & fixes", "direction": -1,
     "claude": ["Review the proposal", "Correctness, clarity", "and edge cases"],
     "codex": ["Propose the code", "Changes, trade-offs", "and rationale"],
     "workspace": "Client applies approved edits"},
    {"name": "Test & refine", "out": "Test strategy", "back": "Results & next steps", "direction": -1,
     "claude": ["Review the results", "Coverage, regressions", "and follow-up analysis"],
     "codex": ["Propose test cases", "Inputs, expected results", "and coverage gaps"],
     "workspace": "Client runs tests and shares results"},
]


def font(size, bold=False):
    name = "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"
    return ImageFont.truetype(str(FONT_ROOT / name), size * SCALE)


FONTS = {size: font(size) for size in (12, 13, 14, 15, 16, 18)}
FONTS.update({f"b{size}": font(size, True) for size in (14, 15, 16, 18, 22, 27)})


def box(draw, bounds, fill, outline=None, radius=16, width=1):
    draw.rounded_rectangle(tuple(int(v * SCALE) for v in bounds), radius=radius * SCALE,
                           fill=fill, outline=outline, width=width * SCALE)


def text(draw, xy, value, selected_font, fill="ink", anchor="la"):
    draw.text(tuple(int(v * SCALE) for v in xy), value, font=FONTS[selected_font],
              fill=COLORS.get(fill, fill), anchor=anchor)


def line(draw, points, fill="line", width=2):
    draw.line([(int(x * SCALE), int(y * SCALE)) for x, y in points],
              fill=COLORS.get(fill, fill), width=width * SCALE)


def arrow(draw, start, end, color):
    line(draw, [start, end], color, 2)
    x, y = end
    direction = 1 if end[0] > start[0] else -1
    draw.polygon([(int(x * SCALE), int(y * SCALE)),
                  (int((x - direction * 7) * SCALE), int((y - 4) * SCALE)),
                  (int((x - direction * 7) * SCALE), int((y + 4) * SCALE))], fill=COLORS[color])


def base_frame(phase_index):
    phase = PHASES[phase_index]
    canvas = Image.new("RGB", (WIDTH * SCALE, HEIGHT * SCALE), COLORS["background"])
    draw = ImageDraw.Draw(canvas)
    text(draw, (36, 25), "Claude + Codex, coordinated by your client", "b27")
    text(draw, (36, 65), "An example MCP workflow for planning, code review and test analysis", 15, "muted")

    for index, item in enumerate(PHASES):
        left = 36 + index * 318
        active = index == phase_index
        box(draw, (left, 105, left + 292, 149), COLORS["blue"] if active else COLORS["white"],
            None if active else COLORS["line"], radius=12)
        text(draw, (left + 17, 117), f"0{index + 1}  {item['name']}", "b15", "white" if active else "muted")

    for provider, left, label in (("claude", 36, "Claude"), ("codex", 724, "Codex")):
        box(draw, (left, 185, left + 240, 386), COLORS["white"], COLORS["line"])
        box(draw, (left + 18, 202, left + 222, 242), COLORS[f"{provider}_light"], radius=10)
        text(draw, (left + 32, 211), label, "b22", provider)
        text(draw, (left + 18, 267), phase[provider][0], "b16")
        text(draw, (left + 18, 303), phase[provider][1], 14, "muted")
        text(draw, (left + 18, 325), phase[provider][2], 14, "muted")
        text(draw, (left + 18, 357), "Read-only provider", 12, provider)

    box(draw, (393, 234, 607, 338), COLORS["white"], COLORS["blue"], radius=16, width=2)
    text(draw, (500, 253), "Your MCP client", "b18", anchor="ma")
    text(draw, (500, 286), "Routes context", 14, "muted", anchor="ma")
    text(draw, (500, 307), "and returned results", 14, "muted", anchor="ma")
    text(draw, (500, 184), phase["out"], "b14", "blue", anchor="ma")
    text(draw, (500, 361), phase["back"], "b14", "blue", anchor="ma")
    for y, direction in ((249, phase["direction"]), (321, -phase["direction"])):
        for left, right in ((285, 384), (616, 715)):
            arrow(draw, (left, y) if direction == 1 else (right, y),
                  (right, y) if direction == 1 else (left, y), "blue")

    line(draw, [(500, 387), (500, 413)], "blue", 2)
    box(draw, (260, 418, 740, 477), COLORS["white"], COLORS["line"], radius=12)
    text(draw, (500, 428), "HOST WORKSPACE", "b14", "muted", anchor="ma")
    text(draw, (500, 451), phase["workspace"], 15, anchor="ma")
    text(draw, (500, 504), "Providers analyze and propose. The client applies edits and runs approved tests.",
         13, "muted", anchor="ma")
    for index in range(3):
        left = 360 + index * 96
        box(draw, (left, 539, left + 84, 543), COLORS["line"], radius=2)
    return canvas


def render_frame(base, phase_index, fraction):
    canvas = base.copy()
    draw = ImageDraw.Draw(canvas)
    direction = PHASES[phase_index]["direction"]
    # One exchange goes out, then a review returns through the client.
    going_out = fraction < 0.48
    progress = fraction / 0.48 if going_out else (fraction - 0.48) / 0.42
    progress = max(0.0, min(progress, 1.0))
    if progress < 1.0:
        travel = 0.5 - 0.5 * math.cos(progress * math.pi)
        forward = direction if going_out else -direction
        # The packet enters the client and leaves toward the other provider.
        start, end = (286, 714) if forward == 1 else (714, 286)
        x = start + (end - start) * travel
        y = 249 if going_out else 321
        if x < 386 or x > 614:
            box(draw, (x - 6, y - 6, x + 6, y + 6), COLORS["blue"], radius=6)
        else:
            box(draw, (491, 348, 509, 353), COLORS["blue"], radius=2)
    progress_left = 360 + phase_index * 96
    box(draw, (progress_left, 539, progress_left + max(4, 84 * fraction), 543), COLORS["blue"], radius=2)
    return canvas.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    bases = [base_frame(index) for index in range(3)]
    palette_source = Image.new("RGB", (WIDTH, HEIGHT * 3))
    for index, base in enumerate(bases):
        palette_source.paste(base.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS), (0, HEIGHT * index))
    palette = palette_source.quantize(colors=128, method=Image.Quantize.MEDIANCUT)
    frames = []
    for phase_index, base in enumerate(bases):
        for index in range(FPS * PHASE_SECONDS):
            frame = render_frame(base, phase_index, index / (FPS * PHASE_SECONDS - 1))
            frames.append(frame.quantize(palette=palette, dither=Image.Dither.NONE))
    durations = [1000 // FPS] * len(frames)
    durations[-1] += 800
    frames[0].save(ASSETS / "pair-workflow.gif", save_all=True, append_images=frames[1:],
                   duration=durations, loop=0, optimize=True, disposal=1)
    poster = bases[0].resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)
    poster.save(ASSETS / "pair-workflow.png", optimize=True)
    print(f"Rendered {len(frames)} frames and a static overview in {ASSETS}")


if __name__ == "__main__":
    main()
