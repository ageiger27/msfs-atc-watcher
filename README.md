# MSFS 2024 ATC Watcher

Answers routine ATC calls in career mode while you are away from the keyboard.

There are two versions in this repo:

- **`app/`** — the Windows desktop app (C# / WPF). Single `AtcWatcher.exe`, no
  install, finds the ATC panel by itself, tray icon, live view of what it will
  press. This is the one to use. Download it from the Releases page.
- **The Python script in the repo root** — the original prototype. Same rules,
  same behaviour, useful if you want to hack on the logic. Instructions below.

## Desktop app

1. Download `AtcWatcher.exe` from Releases and run it. Windows may show a
   “Windows protected your PC” screen the first time because the build is not
   yet code-signed. Click *More info* then *Run anyway*.
2. In the sim, open the ATC panel and pin it so it stays on screen.
3. Click **Find it for me**. It scans every monitor for the numbered replies and
   sets the watch area. Use **Select manually** if it can't find them.
4. Type your callsign, or click **Detect from panel** once ATC has said something.
5. Leave it **ARMED** and go do something else. `Ctrl+Alt+A` arms and disarms
   from inside the sim. Closing the window keeps it running in the tray.

The **What it sees right now** list shows every reply on the panel and whether it
would be pressed (PRESS), never pressed (DENY), or ignored because no rule
matched (SKIP). If a call gets missed, the SKIP row tells you what text to add
a rule for. Rules live in `%APPDATA%\AtcWatcher\settings.json` and are the
same regexes the Python script uses.

Building it yourself needs the .NET 8 SDK: run `app\publish.bat`, or open
`app\AtcWatcher.sln` in Visual Studio. Tests: `dotnet test app`.

## Python script

It screenshots the pinned ATC panel every few seconds, reads the numbered
options with the Windows OCR engine, and when an acknowledgement or readback
option shows up (Roger, Wilco, "Contact Seattle Center on 125.80, N172SP",
"Descend and maintain 5,000, N172SP", etc.) it presses that number key.
Requests, cancellations, ATIS, "nearest airport" and similar options are never
pressed.

Nothing is injected into the sim. It only presses the same number keys you
would press yourself, and only while the sim is the foreground window.

## One-time setup

1. Run `setup.bat` once. It creates `.venv` and installs the packages in `requirements.txt`.
2. Windows needs the English OCR language pack. It is normally present on
   Windows 11. If the script complains, go to Settings > Time & Language >
   Language & region > English > Language options > Optical character recognition.
3. In the sim, open the ATC panel and **pin it** (the pin icon on its title bar)
   so it stays on screen. Put it somewhere it will not be covered, or pop it out
   to a second monitor. Make the panel opaque if you have transparency turned up.
4. Run `select-region.bat`. The screen dims. Drag a box around the ATC panel,
   including the numbered options area. Press Esc to cancel. If the sim is in
   exclusive fullscreen the overlay may not appear; switch to windowed or
   borderless for this step.
5. `config.json` is created on first run (or copy `config.example.json`). Set `"callsign"` to your career tail number, for
   example `"N172SP"`. This is the strongest signal that an option is a readback,
   so do not skip it.
6. Run `test.bat` with the ATC panel showing something. It prints what OCR read,
   how each option was classified, and which key it would press. Nothing is
   pressed in test mode. Repeat until it looks right.

## Flying with it

1. Start the flight, reach cruise, engage autopilot.
2. Run `run.bat`. It starts armed. Leave the sim as the foreground window.
3. `Ctrl+Alt+A` toggles armed / disarmed while it runs. `Ctrl+C` in the console quits.

It logs a "Still watching" line once a minute. Every time the panel's option list changes it logs each option and how it was
classified (PRESS / DENY / SKIP). When it presses a key it saves the screenshot
that triggered it to `debug/` (newest 50 kept), so you can audit what it did
while you were away. The log is also written to `atc_watcher.log`. If a call
was missed, find the SKIP line for it in the log and the pattern can be added.

`dry-run.bat` runs the full loop with verbose logging but never presses keys.
Use it for a whole flight the first time to see what it would have done.

## Safety rules built in

- Context rules handle the IFR-to-VFR transition. "Cancel IFR" is pressed only
  when ATC's message on the panel says you may cancel, and "Request Flight
  Following" only when the panel is also offering "Retry With Last IFR Flight
  Plan" (meaning IFR just ended). Both are denied at any other time.
- Deny patterns beat allow patterns. Anything containing "request", "cancel",
  "nearest", "declare", "emergency", "unable", "say again" and so on is never
  pressed. "Tune" and "change" are only allowed when a frequency follows them.
- Set `confirm_scans` to 2 if you want an option to be read on two consecutive
  scans before it is pressed, at the cost of a slower reply.
- At most one press every 4 seconds, and never the same option twice in a row
  until it has cleared from the panel.
- Keys are only sent when the foreground window title contains
  "Flight Simulator". If you alt-tab to a browser, nothing is pressed.

## Known limits

- It acknowledges whatever ATC says. If you would have refused an altitude
  change, it will read it back anyway. Intended for cruise, not approach.
- Career missions sometimes show a quick-reply button instead of the full panel.
  Run `test.bat` while one is showing and, if the text does not parse as a
  numbered option, send me the screenshot from `debug/` and the patterns can be
  extended.
- If you move the ATC panel, change resolution, or change the sim's UI scale,
  re-run `select-region.bat`.
- If you have rebound the ATC option keys away from 1-0, edit `option_keys` in
  `config.json`. Valid names: `0`-`9`, `num0`-`num9`, `f1`-`f12`, `a`-`z`.

## config.json reference

| key | default | meaning |
|---|---|---|
| `region` | null | Screen rectangle of the ATC panel. Set with `select-region.bat`. |
| `callsign` | "" | Your tail number. Options ending with it count as readbacks. |
| `scan_interval` | 10.0 | Seconds between scans. |
| `heartbeat_interval` | 60 | Seconds between "Still watching" log lines. 0 disables. |
| `confirm_scans` | 1 | Consecutive scans an option must be seen before pressing. |
| `min_press_interval` | 4.0 | Minimum seconds between presses. |
| `post_press_delay` | 2.5 | Pause after a press so the panel can redraw. |
| `ocr_scale` | 2 | Upscale factor before OCR. Raise to 3 if the panel font is tiny. |
| `sim_window_title_contains` | "Flight Simulator" | Foreground window check. |
| `option_keys` | 1-0 | Key sent for each option number. |
| `toggle_hotkey` | "ctrl+alt+a" | Global arm / disarm hotkey. |
| `armed_on_start` | true | Start armed. |
| `dry_run` | false | Log only, never press. |
| `save_trigger_captures` | true | Save the screenshot behind every press to `debug/`. |
| `allow_patterns` | see file | Regexes that mark an option as an acknowledgement. |
| `deny_patterns` | see file | Regexes that block an option no matter what. |
| `context_rules` | see file | Option + context regex pairs. Pressed only when the rest of the panel matches the context. Beat the deny list. |
