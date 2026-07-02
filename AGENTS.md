# AGENTS.md — ShaderVarianCollectorTools

## What this is

Unity Editor-only UPM package for collecting shader variants into `.shadervariants` files and stripping unused variants at build time. No runtime code — everything lives under `Editor/`.

- **Unity minimum**: 2021.3 (per `package.json`; code has `#if UNITY_2019_4_OR_NEWER` guards)
- **Assembly**: `ShaderVarianCollectionTools.Editor.asmdef` — Editor platform only, no references
- **License**: MIT

## Architecture

Two independent subsystems, no shared code between them:

### 1. ShaderVariantCollector (`Editor/ShaderVariantCollector/`)

Collects shader variants by rendering materials on spheres in a temp scene.

| File | Role |
|---|---|
| `ShaderVariantCollectorWindow.cs` | EditorWindow (menu: **Tools > 着色器变种收集器**). UI built with UIElements/UXML |
| `ShaderVariantCollector.cs` | Core state machine (`ESteps`) driving the collection loop via `EditorApplication.update` |
| `ShaderVariantCollectorSetting.cs` | ScriptableObject persisted at `Editor/config/ShaderVariantCollectorSetting.asset` |
| `ShaderVariantCollectionHelper.cs` | Thin wrappers around `ShaderUtil` private methods via reflection |
| `ShaderVariantCollectionManifest.cs` | Serializes SVC data to JSON manifest |
| `LocalKeywordData.cs` | Data classes for per-shader local keyword config |
| `PathSelector.cs` | Reusable UIElements folder-picker widget |
| `ShaderVariantCollectorWindow.uxml` | Window layout |

### 2. ShaderVariantStripper (`Editor/StriphSaderVariants/`)

Build-time `IPreprocessShaders` implementation that strips variants not present in collected SVC files.

| File | Role |
|---|---|
| `ShaderVariantStripper.cs` | Reads SVCs from a hardcoded path, strips during build |

### Shared utilities

| File | Role |
|---|---|
| `EditorTools.cs` | Static helpers: reflection invocation, file ops, path conversion, progress bar |
| `UxmlLoader.cs` | Loads UXML by window type name via `AssetDatabase.FindAssets` |
| `CustomPackRule.cs` | Entirely commented out — dead code (YooAsset pack rules) |

## Critical quirks

- **Typos are baked into names** — do not "fix" them; they would break serialized references:
  - Package name: `com.le888.shader-varian-collector-tolls`
  - Folder: `StriphSaderVariants/` (not "StripShaderVariants")
  - Methods: `GeFileSavePath`, `GeFileSearchPath`, `GeSecneSearchPath`, `GeProcessCapacity`, `GeBlackPath` (missing 't' in "Get")
  - Class/enum: `ESteps.CollectWithWaitGlablKeyWords` (not "Global")

- **Stripper has a hardcoded SVC folder path**: `ShaderVariantStripper.cs:13` — `Assets/ResourcesAB/Config/ShaderVarians`. This must match the consumer project's actual SVC output path.

- **`ShaderVariantCollectionHelper` uses reflection** into `ShaderUtil` private APIs (`ClearCurrentShaderVariantCollection`, `SaveCurrentShaderVariantCollection`, etc.). These are undocumented Unity internals; they may break across Unity major versions.

- **No namespaces** — all classes are in the global namespace.

- **Collection is async via EditorUpdate** — the collector state machine runs on `EditorApplication.update`, not coroutines. The Game window must be open and focused (`EditorTools.FocusUnityGameWindow()`). Collection sleeps 5–10 seconds between batches to let Unity compile shader variants.

- **UXML loading** (`UxmlLoader.cs`) searches by class name — the UXML asset filename must match the window class name (`ShaderVariantCollectorWindow`) or it throws.

- **Settings singleton** — `ShaderVariantCollectorSetting.LoadOrCreateSettings()` creates the asset at a path derived from the script's own location (`Editor/config/`). The `packageName` parameter in all Get/Set methods is ignored; there is only one global settings asset.

## No build/test/lint

This is a pure Unity Editor package. There are no CLI build commands, no test suite, no linter config, and no CI workflows. Verification means importing into a Unity project and using the editor window.
