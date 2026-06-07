# Contributing to BottomlessWater

Thanks for helping out. This file is the short version of "what merges quickly and what gets bounced back."

---

## Filing an issue

- **Bugs.** Include:
  - Plugin version (from the `[Info]` attribute, `manifest.json`, or `oxide.plugins`).
  - Oxide / uMod build (`oxide.version`).
  - Rust server build (Facepunch staging vs release, branch).
  - A relevant excerpt from `oxide.log` and / or `oxide/logs/BottomlessWater.txt`.
  - Minimal reproduction steps. "Place a small water bottle in a large water catcher with `FillEmptyContainers=true` and observe X" is great; "doesn't work" is not.
- **Feature requests.** Describe the problem you're trying to solve, not the implementation you want. We may have a simpler answer.
- **Security issues.** Please report privately by opening a draft issue and tagging the maintainer, or via the contact listed on the maintainer's GitHub profile, rather than filing a public issue first.

---

## Pull requests

### Branching

- Branch off `main`.
- Name branches `feat/<short-name>`, `fix/<short-name>`, `perf/<short-name>`, or `docs/<short-name>`.
- Keep one logical change per PR. A perf fix and a doc rewrite belong in separate PRs.

### What to include

- Code changes in `oxide/plugins/BottomlessWater.cs`.
- A matching CHANGELOG entry under a new version heading (we follow [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)).
- Documentation updates when you change config fields, commands, or permissions:
  - `README.md` config table and command list.
  - `docs/config.sample.json` for new defaults.
  - `INSTALL.md` if the install / update story changes.
- Version bump across `manifest.json`, `.umod.yaml`, `docs/config.sample.json`, and the plugin `[Info]` attribute. Use semver:
  - Patch (`x.y.Z`): bug fix or doc-only change, no config-shape changes.
  - Minor (`x.Y.0`): new config field with a backward-compatible default, new optional command, new hook.
  - Major (`X.0.0`): breaking change to config, commands, or stored data layout.

### Code style

- C# 8+. Prefer early returns over nested `if` pyramids.
- Match the existing field-grouping and `// ─── header ───` section convention in `BottomlessWater.cs`.
- Add an XML doc comment to anything non-trivial. The comment should explain *why*, not *what*; well-named identifiers cover the *what*.
- No `System.ValueTuple` usage — the uMod build server has historically rejected it. See the `PlayerState` class for the workaround.
- Don't introduce new dependencies. The plugin must compile with stock Oxide references.

### Things that get bounced

- Removing the per-tick `bottomlesswater.use` re-check, or replacing it with a reactive cache, without addressing every relevant permission/group hook AND covering plugin reload. The risk of "permission was revoked but the player still gets infinite water" outweighs the perf win.
- Removing the water-only fill filter in `FillLiquidItems`. That filter exists to prevent topping up salt water, crude oil, and other liquids with in-game economy value.
- Skipping debounced writes in favour of synchronous I/O on every change. Player state is mutated frequently; a save storm during a wipe is a real concern.
- Logging that includes raw player input without length-capping or sanitising. Chat args are length-capped to `MaxArgLength` before reaching log paths.

### Compiling

Every change must compile against the real Oxide/Rust/Unity assemblies. CI does
this on each push and PR (`.github/workflows/compile.yml`); run it locally first:

```bash
make references-managed   # one-time: fetch the Oxide/Rust reference DLLs
make build                # type-check oxide/plugins/BottomlessWater.cs
```

A clean build ends with `0 Error(s)`; errors mean the plugin would fail to load
on a server. See [`BUILD.md`](BUILD.md) for prerequisites, Windows instructions,
and how to point the build at an existing server's `Managed/` folder.

### Testing

There is no automated test harness for this plugin — it runs inside the Rust server process and exercises Facepunch APIs that aren't unit-testable. Before opening a PR, please verify on a private Rust server:

1. Plugin loads cleanly: `oxide.reload BottomlessWater`, no compile errors in `oxide.log`.
2. `/bw status` works as a non-admin permitted player.
3. `bottomlesswater.toggle <steamid64> on` works from the server console.
4. `bottomlesswater.reload` works after editing the config.
5. Place a water purifier, fill it, drain it via the spout, observe top-up within `TickSeconds * TickBucketCount` seconds.
6. Place a salt-water collector — confirm it is NOT topped up.
7. Restart the server — confirm player toggle state persists.

Note any of these you couldn't verify in the PR description.

---

## Localisation

To add a new language:

1. Copy `oxide/lang/en/BottomlessWater.json` to `oxide/lang/<locale>/BottomlessWater.json`.
2. Translate the values, keeping the keys and `<color=#...>` tags intact.
3. Add the new locale to the README's "Data files" table.
4. PR title: `i18n: add <language> translation`.

The lang file format is a flat key-value JSON map — do NOT wrap the entries in an outer `"BottomlessWater": { ... }` object. Oxide's lang system reads the flat shape.

---

## Releases (maintainers)

1. Verify CHANGELOG, README, INSTALL, sample config, manifest, and `.umod.yaml` all reference the new version.
2. Tag `vX.Y.Z` on `main`.
3. Upload `oxide/plugins/BottomlessWater.cs` to the umod.org listing.
4. Attach the CHANGELOG entry as the release notes.
