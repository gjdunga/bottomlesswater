# Contributing to BottomlessWater

Thanks for pitching in! A few quick guidelines:

## Issues
- **Bugs** → use the *Bug report* template.
- **Features** → use the *Feature request* template.
- Include your Rust build, uMod/Oxide build, plugin version, and relevant logs.

## Pull Requests
- Fork, create a branch: `feat/<short-name>` or `fix/<short-name>`.
- Keep changes focused. Update `README.md` and `CHANGELOG.md` if you change config/commands.
- Test on a private server if possible; include repro notes.
- Run a quick self-review: null checks, timers cleaned up, no spammy logging.

## Code Style
- C# 8+.
- Use early returns, avoid nested `if` pyramids.
- Prefer small helpers (`ReplyChat`, `LogAction`, etc.) over inline repetition.
