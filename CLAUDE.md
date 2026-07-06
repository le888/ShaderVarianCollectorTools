# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Unity Editor-only UPM package for collecting shader variants into `.shadervariants` files and stripping unused variants at build time. No runtime code — everything is under `Editor/`.

- **Unity minimum**: 2021.3
- **License**: MIT

## Build / Test / Lint

There are no CLI build commands, test suite, or linter. This is a pure Unity Editor package — verification means importing into a Unity project and using the editor window.

## Architecture

Two independent subsystems with no shared code between them:

1. **ShaderVariantCollector** (`Editor/ShaderVariantCollector/`) — Collects shader variants by rendering materials on spheres in a temporary scene. The core state machine (`ShaderVariantCollector.cs`) runs async via `EditorApplication.update`, not coroutines. The Game window must be open and focused. Collection sleeps 5–10 seconds between batches to let Unity compile shader variants.

2. **ShaderVariantStripper** (`Editor/StriphSaderVariants/`) — Build-time `IPreprocessShaders` implementation that strips variants not present in collected SVC files. Has a hardcoded SVC folder path at `ShaderVariantStripper.cs:13` that must match the consumer project's output path.

Shared utilities live in `EditorTools.cs` (reflection helpers, file ops, progress bar) and `UxmlLoader.cs` (UXML loading by class name).

## Critical Quirks — Do Not "Fix" These

Typos are baked into serialized references and package identity. Fixing them breaks existing users:

- **Package name**: `com.le888.shader-varian-collector-tolls` (not "tools")
- **Folder**: `StriphSaderVariants/` (not "StripShaderVariants")
- **Methods**: `GeFileSavePath`, `GeFileSearchPath`, `GeSecneSearchPath`, `GeProcessCapacity`, `GeBlackPath` (missing 't' in "Get")
- **Class/enum**: `ESteps.CollectWithWaitGlablKeyWords` (not "Global")

Other notable constraints:

- **No namespaces** — all classes are in the global namespace.
- **`ShaderVariantCollectionHelper` uses reflection** into undocumented `ShaderUtil` private APIs — may break across Unity major versions.
- **UXML loading** (`UxmlLoader.cs`) searches by class name — the UXML asset filename must match the window class name or it throws.
- **Settings singleton** — `ShaderVariantCollectorSetting.LoadOrCreateSettings()` creates the asset at a path derived from the script's own location (`Editor/config/`). The `packageName` parameter in Get/Set methods is ignored; there is only one global settings asset.
