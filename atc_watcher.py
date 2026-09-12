"""
MSFS 2024 ATC watcher.

Watches a region of the screen where the in-sim ATC panel is pinned, OCRs the
numbered menu options with the built-in Windows OCR engine, and when it sees an
acknowledgement / readback option (Roger, Wilco, "Contact ... on 125.80",
"Descend and maintain ...", etc.) it presses the matching number key so the
call is answered while you are away from the keyboard.

Usage:
    python atc_watcher.py --select-region   # drag a box around the ATC panel (saved to config.json)
    python atc_watcher.py --test            # capture once, show what OCR sees and what it would press
    python atc_watcher.py --test --image f  # same, but from a saved screenshot instead of the live screen
    python atc_watcher.py                   # run the watcher (Ctrl+Alt+A arms / disarms, Ctrl+C quits)
    python atc_watcher.py --dry-run         # run, but never press keys

Requires: Windows 10/11 with the English OCR language pack, Python 3.10+,
and the packages in requirements.txt.
"""

from __future__ import annotations

import argparse
import asyncio
import ctypes
import ctypes.wintypes as wt
import json
import logging
import re
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

HERE = Path(__file__).resolve().parent
CONFIG_PATH = HERE / "config.json"
LOG_PATH = HERE / "atc_watcher.log"
DEBUG_DIR = HERE / "debug"

DEFAULT_CONFIG = {
    # Screen rectangle (physical pixels, absolute virtual-screen coords) of the ATC panel.
    # Set it with --select-region rather than by hand.
    "region": None,

    # Seconds between scans.
    "scan_interval": 1.0,

    # The same acknowledgement option must be seen this many scans in a row before we press.
    # Protects against a single garbled OCR frame.
    "confirm_scans": 2,

    # Never press keys more often than this (seconds).
    "min_press_interval": 4.0,

    # After pressing, wait this long before scanning again (lets the panel redraw).
    "post_press_delay": 2.5,

    # Upscale factor before OCR. 2 works well for the default panel font size.
    "ocr_scale": 2,

    # Your career callsign, e.g. "N172SP" or "Speedbird NAO9210". Optional but strongly recommended:
    # readbacks and check-in calls contain your callsign, generic requests do not.
    "callsign": "",

    # Only press when the foreground window title contains this text.
    "sim_window_title_contains": "Flight Simulator",

    # Keyboard key sent for each ATC option number. Defaults match MSFS's default bindings.
    # Valid names: 0-9, num0-num9, f1-f12, a-z.
    "option_keys": {"1": "1", "2": "2", "3": "3", "4": "4", "5": "5",
                    "6": "6", "7": "7", "8": "8", "9": "9", "0": "0"},

    # Global hotkey that arms / disarms the watcher.
    "toggle_hotkey": "ctrl+alt+a",

    # Start armed?
    "armed_on_start": True,

    # Log but never press.
    "dry_run": False,

    # Save the screenshot that triggered each key press into ./debug (keeps the newest 50).
    "save_trigger_captures": True,

    # Option text that means "this is an acknowledgement / readback". Case-insensitive regex.
    "allow_patterns": [
        r"^(roger|wilco|affirm|affirmative|acknowledge[d]?|cop(y|ied)|understood)\b",
        # Handoff readback or tuning to the new frequency: "Contact Salt Lake Center on 128.05",
        # "Tune COM1 to 128.05", "Switch to 128.05".
        r"\b(contact|switch(ing)?( to)?|monitor|tune|set)\b.*\b1\d{2}[.,]\d{1,3}\b",
        # Check-in with the new controller: "Salt Lake Center, Speedbird NAO9210, 12,000 ft."
        r"^[a-z .'\-]+\b(center|centre|approach|departure|tower|ground|delivery|radio|control|unicom|"
        r"clearance|director|radar)\b.*\b(\d{1,2},?\d{3}\s*(ft|feet)|fl\s?\d{2,3}|flight level|with you|level)\b",
        r"^(climb|descend|maintain|fly heading|turn (left|right)|proceed direct|cleared|expect|"
        r"squawk|reduce speed|increase speed|resume own navigation|radar contact|altimeter|"
        r"cross|continue|hold|taxi|line up|position and hold|frequency change approved)\b",
    ],

    # Option text that must never be pressed, even if an allow pattern matches. Regex, case-insensitive.
    "deny_patterns": [
        r"\b(request|cancel|nearest|declare|emergency|say again|unable|ask|file|close|"
        r"flight following|abort|divert|stay with|check in|ready for|ready to|atis|awos|asos)\b",
        # "Tune ..." is only OK when a frequency follows it (handled by the allow list).
        r"\btune\b(?!.*\b1\d{2}[.,]\d{1,3}\b)",
        r"\bchange\b(?!.*\b1\d{2}[.,]\d{1,3}\b)",
    ],
}


# --------------------------------------------------------------------------------------
# Config
# --------------------------------------------------------------------------------------

def load_config() -> dict:
    cfg = json.loads(json.dumps(DEFAULT_CONFIG))
    if CONFIG_PATH.exists():
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            user = json.load(f)
        cfg.update(user)
    else:
        save_config(cfg)
    return cfg


def save_config(cfg: dict) -> None:
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)


# --------------------------------------------------------------------------------------
# Win32 helpers
# --------------------------------------------------------------------------------------

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32


def set_dpi_aware() -> None:
    """Make screen coordinates physical pixels so Tk, mss and SendInput all agree."""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass


def foreground_window_title() -> str:
    hwnd = user32.GetForegroundWindow()
    length = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buf, length + 1)
    return buf.value


# --- SendInput with hardware scan codes (what games expect) ---

ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_SCANCODE = 0x0008


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ULONG_PTR)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ULONG_PTR)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wt.DWORD), ("wParamL", wt.WORD), ("wParamH", wt.WORD)]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTUNION)]


SCANCODES = {
    **{str(d): sc for d, sc in zip("1234567890", range(0x02, 0x0C))},
    "num0": 0x52, "num1": 0x4F, "num2": 0x50, "num3": 0x51, "num4": 0x4B,
    "num5": 0x4C, "num6": 0x4D, "num7": 0x47, "num8": 0x48, "num9": 0x49,
    **{f"f{i}": sc for i, sc in zip(range(1, 11), range(0x3B, 0x45))},
    "f11": 0x57, "f12": 0x58,
    "a": 0x1E, "b": 0x30, "c": 0x2E, "d": 0x20, "e": 0x12, "f": 0x21, "g": 0x22, "h": 0x23,
    "i": 0x17, "j": 0x24, "k": 0x25, "l": 0x26, "m": 0x32, "n": 0x31, "o": 0x18, "p": 0x19,
    "q": 0x10, "r": 0x13, "s": 0x1F, "t": 0x14, "u": 0x16, "v": 0x2F, "w": 0x11, "x": 0x2D,
    "y": 0x15, "z": 0x2C,
}


def press_key(name: str, hold: float = 0.08) -> None:
    sc = SCANCODES.get(name.lower())
    if sc is None:
        raise ValueError(f"Unknown key name in option_keys: {name!r}")
    down = INPUT(type=INPUT_KEYBOARD, u=_INPUTUNION(ki=KEYBDINPUT(0, sc, KEYEVENTF_SCANCODE, 0, 0)))
    up = INPUT(type=INPUT_KEYBOARD,
               u=_INPUTUNION(ki=KEYBDINPUT(0, sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, 0, 0)))
    user32.SendInput(1, ctypes.byref(down), ctypes.sizeof(INPUT))
    time.sleep(hold)
    user32.SendInput(1, ctypes.byref(up), ctypes.sizeof(INPUT))


# --- Global hotkey (RegisterHotKey) ---

MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN, MOD_NOREPEAT = 0x1, 0x2, 0x4, 0x8, 0x4000
WM_HOTKEY = 0x0312


def parse_hotkey(spec: str) -> tuple[int, int]:
    mods = 0
    vk = None
    for part in spec.lower().split("+"):
        part = part.strip()
        if part in ("ctrl", "control"):
            mods |= MOD_CONTROL
        elif part == "alt":
            mods |= MOD_ALT
        elif part == "shift":
            mods |= MOD_SHIFT
        elif part in ("win", "windows"):
            mods |= MOD_WIN
        elif re.fullmatch(r"f([1-9]|1[0-2])", part):
            vk = 0x70 + int(part[1:]) - 1
        elif len(part) == 1 and part.isalnum():
            vk = ord(part.upper())
        else:
            raise ValueError(f"Unrecognised hotkey part {part!r} in {spec!r}")
    if vk is None:
        raise ValueError(f"Hotkey {spec!r} has no main key")
    return mods | MOD_NOREPEAT, vk


class HotkeyThread(threading.Thread):
    """Runs a tiny message loop so RegisterHotKey works regardless of what the main thread does."""

    def __init__(self, spec: str, callback):
        super().__init__(daemon=True)
        self.spec = spec
        self.callback = callback
        self.ok = threading.Event()
        self.error: Optional[str] = None

    def run(self) -> None:
        try:
            mods, vk = parse_hotkey(self.spec)
        except ValueError as e:
            self.error = str(e)
            self.ok.set()
            return
        if not user32.RegisterHotKey(None, 1, mods, vk):
            self.error = f"RegisterHotKey failed for {self.spec!r} (already in use by another program?)"
            self.ok.set()
            return
        self.ok.set()
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) != 0:
            if msg.message == WM_HOTKEY:
                self.callback()
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))


# --------------------------------------------------------------------------------------
# Screen capture + OCR
# --------------------------------------------------------------------------------------

def _import_ocr():
    try:
        from winrt.windows.media.ocr import OcrEngine
        from winrt.windows.graphics.imaging import SoftwareBitmap, BitmapPixelFormat, BitmapAlphaMode
        from winrt.windows.storage.streams import DataWriter
    except ImportError:
        from winsdk.windows.media.ocr import OcrEngine  # older packaging
        from winsdk.windows.graphics.imaging import SoftwareBitmap, BitmapPixelFormat, BitmapAlphaMode
        from winsdk.windows.storage.streams import DataWriter
    return OcrEngine, SoftwareBitmap, BitmapPixelFormat, BitmapAlphaMode, DataWriter


class Ocr:
    def __init__(self):
        (self.OcrEngine, self.SoftwareBitmap, self.BitmapPixelFormat,
         self.BitmapAlphaMode, self.DataWriter) = _import_ocr()
        self.engine = self.OcrEngine.try_create_from_user_profile_languages()
        if self.engine is None:
            raise RuntimeError(
                "Windows OCR engine unavailable. Install an OCR language pack: "
                "Settings > Time & Language > Language & region > (your language) > "
                "Language options > Optical character recognition."
            )
        self.loop = asyncio.new_event_loop()

    def read_lines(self, img) -> list[str]:
        """img: PIL Image (any mode). Returns OCR'd text lines, top to bottom."""
        rgba = img.convert("RGBA")
        data = rgba.tobytes("raw", "BGRA")
        writer = self.DataWriter()
        writer.write_bytes(data)
        buf = writer.detach_buffer()
        bmp = self.SoftwareBitmap(self.BitmapPixelFormat.BGRA8, rgba.width, rgba.height,
                                  self.BitmapAlphaMode.IGNORE)
        bmp.copy_from_buffer(buf)
        result = self.loop.run_until_complete(self.engine.recognize_async(bmp))
        # Sort by vertical position so numbered options come out in order.
        lines = []
        for ln in result.lines:
            words = list(ln.words)
            y = min(w.bounding_rect.y for w in words) if words else 0
            lines.append((y, ln.text))
        lines.sort(key=lambda t: t[0])
        return [t for _, t in lines]


def _mss_ctx():
    import mss
    cls = getattr(mss, "MSS", None)
    return cls() if cls else mss.mss()


def grab_region(sct, region: dict):
    from PIL import Image
    shot = sct.grab({"left": region["left"], "top": region["top"],
                     "width": region["width"], "height": region["height"]})
    return Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")


def prep_for_ocr(img, scale: int):
    from PIL import Image
    if scale and scale != 1:
        img = img.resize((img.width * scale, img.height * scale), Image.LANCZOS)
    return img


# --------------------------------------------------------------------------------------
# Parsing + decision
# --------------------------------------------------------------------------------------

@dataclass
class Option:
    number: str
    text: str
    raw: str


# "1. Contact ...", "1 Contact ...", "1) ...". A digit must be followed by a separator or a space
# so history lines like "10,000 feet" don't parse as option 1.
OPTION_RE = re.compile(r"^\s*(\d)(?:\s*[.,:;)\]\-]\s*|\s+)(\S.*)$")


def parse_options(lines: list[str]) -> list[Option]:
    opts = []
    for raw in lines:
        m = OPTION_RE.match(raw)
        if m:
            opts.append(Option(number=m.group(1), text=m.group(2).strip(), raw=raw))
    return opts


_FUZZ = str.maketrans({"O": "0", "I": "1", "L": "1", "S": "5", "B": "8", "Z": "2"})


def fuzzy(s: str) -> str:
    return re.sub(r"[^A-Z0-9]", "", s.upper()).translate(_FUZZ)


class Decider:
    def __init__(self, cfg: dict):
        self.allow = [re.compile(p, re.I) for p in cfg["allow_patterns"]]
        self.deny = [re.compile(p, re.I) for p in cfg["deny_patterns"]]
        self.callsign = fuzzy(cfg.get("callsign") or "")

    def classify(self, opt: Option) -> tuple[str, str]:
        """Returns (verdict, reason) where verdict is 'press', 'deny' or 'skip'."""
        for d in self.deny:
            if d.search(opt.text):
                return "deny", f"deny pattern /{d.pattern[:40]}.../"
        for a in self.allow:
            if a.search(opt.text):
                return "press", f"allow pattern /{a.pattern[:40]}.../"
        if self.callsign and len(self.callsign) >= 4 and self.callsign in fuzzy(opt.text):
            return "press", "contains callsign"
        return "skip", "no match"

    def choose(self, opts: list[Option]) -> tuple[Optional[Option], list[tuple[Option, str, str]]]:
        verdicts = [(o, *self.classify(o)) for o in opts]
        for o, v, _ in verdicts:
            if v == "press":
                return o, verdicts
        return None, verdicts


# --------------------------------------------------------------------------------------
# Region selection overlay
# --------------------------------------------------------------------------------------

def select_region_interactive() -> Optional[dict]:
    import tkinter as tk

    SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 76, 77, 78, 79
    vx, vy = user32.GetSystemMetrics(SM_XVIRTUALSCREEN), user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
    vw, vh = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN), user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)

    root = tk.Tk()
    root.overrideredirect(True)
    root.geometry(f"{vw}x{vh}+{vx}+{vy}")
    root.attributes("-topmost", True)
    root.attributes("-alpha", 0.35)
    root.configure(bg="black")
    canvas = tk.Canvas(root, bg="black", highlightthickness=0, cursor="crosshair")
    canvas.pack(fill="both", expand=True)
    canvas.create_text(vw // 2 - vx, 40, fill="white", font=("Segoe UI", 18),
                       text="Drag a box around the ATC panel (include the numbered options).  Esc = cancel")

    state = {"start": None, "rect": None, "result": None}

    def on_down(e):
        state["start"] = (e.x_root, e.y_root)
        if state["rect"]:
            canvas.delete(state["rect"])
        state["rect"] = canvas.create_rectangle(e.x, e.y, e.x, e.y, outline="red", width=3)

    def on_drag(e):
        if state["start"]:
            sx, sy = state["start"]
            canvas.coords(state["rect"], sx - vx, sy - vy, e.x_root - vx, e.y_root - vy)

    def on_up(e):
        if not state["start"]:
            return
        sx, sy = state["start"]
        x1, y1, x2, y2 = min(sx, e.x_root), min(sy, e.y_root), max(sx, e.x_root), max(sy, e.y_root)
        if x2 - x1 > 20 and y2 - y1 > 20:
            state["result"] = {"left": x1, "top": y1, "width": x2 - x1, "height": y2 - y1}
        root.destroy()

    canvas.bind("<ButtonPress-1>", on_down)
    canvas.bind("<B1-Motion>", on_drag)
    canvas.bind("<ButtonRelease-1>", on_up)
    root.bind("<Escape>", lambda e: root.destroy())
    root.mainloop()
    return state["result"]


# --------------------------------------------------------------------------------------
# Main loop
# --------------------------------------------------------------------------------------

def setup_logging(verbose: bool) -> logging.Logger:
    log = logging.getLogger("atc")
    log.setLevel(logging.DEBUG if verbose else logging.INFO)
    fmt = logging.Formatter("%(asctime)s %(levelname)-5s %(message)s", "%H:%M:%S")
    ch = logging.StreamHandler(sys.stdout)
    ch.setFormatter(fmt)
    fh = logging.FileHandler(LOG_PATH, encoding="utf-8")
    fh.setFormatter(logging.Formatter("%(asctime)s %(levelname)-5s %(message)s"))
    log.addHandler(ch)
    log.addHandler(fh)
    return log


def save_debug_capture(img, tag: str) -> Path:
    DEBUG_DIR.mkdir(exist_ok=True)
    path = DEBUG_DIR / f"{time.strftime('%Y%m%d-%H%M%S')}_{tag}.png"
    img.save(path)
    # Keep the folder from growing forever.
    files = sorted(DEBUG_DIR.glob("*.png"), key=lambda p: p.stat().st_mtime)
    for old in files[:-50]:
        try:
            old.unlink()
        except OSError:
            pass
    return path


def run_test(cfg: dict, log: logging.Logger, image_path: Optional[str]) -> int:
    from PIL import Image
    ocr = Ocr()
    decider = Decider(cfg)
    if image_path:
        img = Image.open(image_path)
        log.info("Loaded %s (%dx%d)", image_path, img.width, img.height)
    else:
        if not cfg.get("region"):
            log.error("No region configured. Run with --select-region first.")
            return 2
        with _mss_ctx() as sct:
            img = grab_region(sct, cfg["region"])
        path = save_debug_capture(img, "test")
        log.info("Captured region %s -> %s", cfg["region"], path)

    lines = ocr.read_lines(prep_for_ocr(img, cfg["ocr_scale"]))
    log.info("OCR lines:")
    for ln in lines:
        log.info("   | %s", ln)
    opts = parse_options(lines)
    if not opts:
        log.info("No numbered options found.")
        return 0
    chosen, verdicts = decider.choose(opts)
    log.info("Options:")
    for o, v, why in verdicts:
        log.info("   [%s] %-5s %s   (%s)", o.number, v.upper(), o.text, why)
    if chosen:
        log.info("Would press option %s -> key %r", chosen.number, cfg["option_keys"].get(chosen.number))
    else:
        log.info("Would press nothing.")
    return 0


def run_watch(cfg: dict, log: logging.Logger, dry_run: bool) -> int:
    if not cfg.get("region"):
        log.error("No region configured. Run with --select-region first.")
        return 2

    ocr = Ocr()
    decider = Decider(cfg)
    armed = threading.Event()
    if cfg.get("armed_on_start", True):
        armed.set()

    def toggle():
        if armed.is_set():
            armed.clear()
            log.info("DISARMED (hotkey)")
        else:
            armed.set()
            log.info("ARMED (hotkey)")

    hk = HotkeyThread(cfg["toggle_hotkey"], toggle)
    hk.start()
    hk.ok.wait(2)
    if hk.error:
        log.warning("%s. Continuing without a toggle hotkey.", hk.error)
    else:
        log.info("Toggle hotkey: %s", cfg["toggle_hotkey"])

    dry_run = dry_run or bool(cfg.get("dry_run"))
    title_needle = cfg.get("sim_window_title_contains") or ""
    confirm_needed = max(1, int(cfg.get("confirm_scans", 2)))
    log.info("Watching region %s every %.1fs%s. Ctrl+C to quit.",
             cfg["region"], cfg["scan_interval"], " [DRY RUN]" if dry_run else "")
    log.info("Status: %s", "ARMED" if armed.is_set() else "disarmed")

    candidate_key: Optional[str] = None      # normalised text of the option we're confirming
    candidate_count = 0
    last_pressed_key: Optional[str] = None
    last_press_time = 0.0
    last_seen_pressed = 0.0                   # last time the pressed option was still on screen
    last_status_reported = None
    last_signature = None

    with _mss_ctx() as sct:
        while True:
            try:
                time.sleep(cfg["scan_interval"])
                if not armed.is_set():
                    if last_status_reported != "disarmed":
                        log.debug("disarmed, idle")
                        last_status_reported = "disarmed"
                    continue
                last_status_reported = "armed"

                img = grab_region(sct, cfg["region"])
                lines = ocr.read_lines(prep_for_ocr(img, cfg["ocr_scale"]))
                opts = parse_options(lines)
                chosen, verdicts = decider.choose(opts)

                signature = tuple((o.number, o.text) for o in opts)
                if signature != last_signature:
                    last_signature = signature
                    if opts:
                        log.info("Panel options changed:")
                        for o, v, why in verdicts:
                            log.info("   [%s] %-5s %s  (%s)", o.number, v.upper(), o.text, why)
                    else:
                        log.info("Panel shows no numbered options")

                now = time.time()
                if chosen is None:
                    if candidate_key is not None:
                        log.debug("candidate gone before confirmation")
                    candidate_key, candidate_count = None, 0
                    continue

                key = fuzzy(chosen.text)
                if key == last_pressed_key:
                    # Same option still (or again) on screen after we pressed it.
                    if now - last_press_time < cfg["min_press_interval"] * 3:
                        last_seen_pressed = now
                        log.debug("already pressed this option recently; waiting for it to clear")
                        continue
                    # It's been a long time; the panel may have shown the same readback again legitimately.

                if key != candidate_key:
                    candidate_key, candidate_count = key, 1
                else:
                    candidate_count += 1
                if candidate_count < confirm_needed:
                    log.debug("candidate [%s] %s seen %d/%d", chosen.number, chosen.text,
                              candidate_count, confirm_needed)
                    continue

                if now - last_press_time < cfg["min_press_interval"]:
                    log.debug("rate limited")
                    continue

                title = foreground_window_title()
                if title_needle and title_needle.lower() not in title.lower():
                    log.info("Would answer [%s] %s but the sim is not in the foreground (%r). Waiting.",
                             chosen.number, chosen.text, title)
                    continue

                keyname = cfg["option_keys"].get(chosen.number)
                if not keyname:
                    log.warning("No key mapped for option %s; check option_keys in config.json", chosen.number)
                    continue

                if dry_run:
                    log.info("DRY RUN: would press %r for [%s] %s", keyname, chosen.number, chosen.text)
                else:
                    log.info("Answering ATC: pressing %r for [%s] %s", keyname, chosen.number, chosen.text)
                    press_key(keyname)
                if cfg.get("save_trigger_captures", True):
                    save_debug_capture(img, f"press{chosen.number}")

                last_pressed_key, last_press_time, last_seen_pressed = key, now, now
                candidate_key, candidate_count = None, 0
                time.sleep(cfg["post_press_delay"])

            except KeyboardInterrupt:
                log.info("Stopped.")
                return 0
            except Exception:
                log.exception("Scan failed; continuing")
                time.sleep(2)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--select-region", action="store_true", help="drag a box around the ATC panel and save it")
    ap.add_argument("--test", action="store_true", help="capture once and show what would happen")
    ap.add_argument("--image", help="with --test: use this PNG instead of the live screen")
    ap.add_argument("--dry-run", action="store_true", help="watch and log, but never press keys")
    ap.add_argument("-v", "--verbose", action="store_true", help="debug logging")
    args = ap.parse_args()

    set_dpi_aware()
    log = setup_logging(args.verbose)
    cfg = load_config()

    if args.select_region:
        region = select_region_interactive()
        if region is None:
            log.info("Selection cancelled; config unchanged.")
            return 1
        cfg["region"] = region
        save_config(cfg)
        log.info("Saved region %s to %s", region, CONFIG_PATH)
        return 0

    if args.test:
        return run_test(cfg, log, args.image)

    return run_watch(cfg, log, args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
