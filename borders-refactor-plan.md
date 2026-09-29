# Border visual overhaul — detailed implementation guide

## 1. Locked decisions and scope

Implement the previously chosen direction:

- **Visual changes only.** Preserve current ownership, contested territory, movement, colonization, diplomacy, and station-transfer behavior.
- **Refined sci-fi appearance:** crisp outlines, restrained inward glow, subtle surface texture, and faint territory fills.
- **Contested areas show every known claimant’s color.** Do not limit patterns to two empires.
- Update **both the main map and minimap**.
- Preserve existing border visibility controls and the border-strength setting.

Build on the current working-tree changes. Do not reset, replace, or clean up unrelated work.

### Critical restrictions

1. Do not change `Empire.IsInBorderTerritory`, border growth, connection generation, or AI policies to make the image look better.
2. Do not fill real enclaves or close real passages as a rendering shortcut.
3. Do not use military strength, the player’s identity, or draw order to decide which color wins an overlap.
4. Remove the additional `WavePoint` displacement applied during drawing. Preserve the organic shape already present in the gameplay influence field.
5. Do not render contested areas by stacking several transparent empire layers.
6. Do not read mutable empire, planet, or relationship collections from rendering workers.
7. Do not use an AI-generated full-map image as a territory texture. The material must follow changing territory dynamically.

## 2. Implementation approach and code boundaries

Use a **shared categorical territory raster plus a distance field**. This gives every visible location one presentation result and avoids requiring a new polygon-boolean library.

The data flow is:

```text
Existing simulation border snapshots
    ↓
Immutable visual snapshot, including knowledge and diplomacy
    ↓
Visible claimant classification on a shared world grid
    ↓
Territory labels + shared frontier distances
    ↓
One material evaluation per displayed location
    ↓
Main-map borders, minimap borders, hover information
```

### Existing integration points

| Area | Required action |
|---|---|
| `BorderField.cs`, containing `BorderScene` | Add snapshot-only classification helpers and capture presentation metadata. Preserve gameplay field construction. |
| `BorderNodeCache.cs` | Reuse applicable field utilities. Preserve `BuildConnections`, which is also used by simulation. Retire its independent fill/occupation rendering from the live map path. |
| `UniverseScreen.Draw.cs` and `UniverseScreen.Render.cs` | Replace per-empire border drawing with the shared visual renderer. Keep the existing border render target and overlay ordering. |
| `MiniMap` | Replace political node circles with the new renderer’s overview representation. |
| `UniverseScreen.HandleInput.cs` | Add low-priority border hover information without consuming input. |

Add the following **internal, nonserialized** types:

- `BorderVisualSnapshot`: generation ID, immutable territory inputs, knowledge state, captured names and colors.
- `BorderClaimSet`: immutable, sorted empire IDs with structural equality.
- `BorderVisualTile`: world bounds, sampling level, claimant labels, frontier-distance data, and dependency information.
- `BorderVisualCache`: tile scheduling, generation publication, resource limits, and GPU uploads.
- `BorderVisualRenderer`: shader binding and drawing.
- `BorderVisualStyle`: the constants listed below.

Add explicit compilation entries to the relevant project files. This repository does not automatically include every new C# file.

Do not add new save fields or change public gameplay APIs.

## 3. Authoritative claimant-classification algorithm

This is the most important correctness step. Implement and test it before adding new shading.

### Snapshot preparation

Capture, on the simulation thread after empire updates have joined:

- Empire ID, active/defeated state, and prepared field references.
- Existing war, pirate, Remnant, and shared-system overlap information.
- Whether the empire is known to the viewer.
- Its known border field.
- Display name and color.

Workers may use these captured values and immutable fields. An `Empire` reference may remain for identity mapping, but workers must not dereference it for changing state.

If required simulation snapshots are not ready, retain the last complete visual snapshot. At initial startup, temporarily draw no political overlay rather than inventing ownership.

### Classify gameplay first, then apply visibility

Add a snapshot-only helper equivalent to the existing ownership rules. It must preserve the current raw/smoothed-field handling and the `0.001f` tie tolerance.

Conceptual pseudocode:

```text
CollectVisibleClaimants(point):

    compute effective strength for every active empire
    using the current raw/smoothed claim rules

    owners = empty

    for each active empire A:
        if strength[A] < 0:
            continue

        ownsPoint = true

        for each other active empire B:
            if strength[B] >= 0 and overlapAllowed(A, B, point):
                continue

            if strength[B] > strength[A] + 0.001
               or (abs(strength[B] - strength[A]) <= 0.001
                   and B.Id < A.Id):
                ownsPoint = false
                break

        if ownsPoint
           and A is known to the viewer
           and A.KnownField contains point:
            owners.add(A.Id)

    sort owners by empire ID
    return interned claimant set
```

Important details:

- Compute ownership against all relevant active empires, including undiscovered ones. Apply visibility only to the result.
- Do not use the existing `occupied` boolean as the claimant list. It currently depends partly on ID ordering and does not describe all claimants.
- Do not infer neighbors from the nearest influence node. Bridges and merged territory can produce different answers.
- Do not replace this with “choose the strongest empire.” Contested territory may have multiple valid owners.
- Do not assume overlap relationships are transitive.
- Reuse per-worker scratch arrays. Avoid allocating lists or arrays for every sample.

Interpret the visible result as:

| Visible claimant count | Presentation |
|---:|---|
| 0 | No political fill |
| 1 | That empire’s territory material |
| 2 or more | Contested material using the complete visible claimant list |

Intern claimant sets by their sorted ID sequence. Region IDs are opaque rendering identifiers; their numeric value must never affect appearance.

**Gate:** randomized snapshot queries must match current gameplay ownership before proceeding, with visibility filtering tested separately.

## 4. Shared grid, frontier extraction, and distance fields

### World-aligned tiles

Use square tiles with:

- **256 × 256 interior samples**
- **32-sample gutter on every side**
- One shared world origin, `(0, 0)`
- One sampling level across the active main-map view

Sampling coordinates must come from integer grid coordinates:

```text
sampleWorldPosition =
    (globalCellCoordinate + 0.5) * cellWorldSize
```

Use mathematical floor for negative coordinates. C# integer truncation is not equivalent.

Choose sampling levels in quarter-octave increments:

```text
cellWorldSize = 1000 * 2^(level / 4)
```

Negative levels are permitted. Select the level nearest to a projected sample size of **0.9 framebuffer pixels**. Rebuild for a new level when the current size leaves **0.7–1.2 pixels**.

Use existing projection helpers to measure scale. Do not estimate it solely from `CamPos.Z`.

Adjacent tiles must sample identical coordinates in their shared gutters. Do not independently normalize each empire or tile to its own bounds.

### Territory labels

At every sample center, store the interned claimant-set ID returned by the classifier.

This categorical label is the basis for:

- Fill color.
- Contested stripe membership.
- Frontier detection.
- Cached visual inspection.

Never linearly interpolate region IDs. Interpolation is permitted for distances and final premultiplied colors, not identifiers.

### Extract each frontier once

Compare each interior label only with its right and lower neighbor:

```text
if label[x, y] != label[x + 1, y]:
    emit vertical frontier

if label[x, y] != label[x, y + 1]:
    emit horizontal frontier
```

Each frontier record contains:

- Canonical global grid-edge coordinates.
- Region ID on each side.
- World-space endpoints.

The canonical key is `(samplingLevel, globalX, globalY, orientation)`.

Use half-open tile ownership for edges on tile boundaries. Gutters may contain duplicate working data, but final drawing must cover only tile interiors.

This naturally handles three-way and four-way junctions. Do not resolve a junction by drawing several independent empire outlines over one another.

Locate each transition within its cell interval rather than assuming it lies at the midpoint: bisect the line between adjacent sample centers seven times, comparing the classifier result to the starting claimant set. Store the resulting normal offset on the canonical edge. This refines presentation without changing any ownership query.

### Build frontier distances

Use a separable squared Euclidean distance transform, with frontier midpoints as seeds on a half-cell lattice:

1. Represent nominal horizontal and vertical grid-edge midpoints on a half-cell lattice. These seed locations identify candidate edges; the actual edges carry the subcell offsets found above.
2. Run the distance transform and retain the nearest seed identity.
3. At each territory sample, calculate distance to the nearest seed’s actual line segment using clamped point-to-segment projection.
4. Record the region on the opposite side of that frontier.
5. Refine against nearby incident edges in frontier buckets. This resolves midpoint approximation, subcell offsets, endpoint ties, and non-incident candidates near junctions.

Use uniform buckets of eight cells for refinement. Its radius is the estimated nearest distance plus one cell, bounded by the stored-distance range.

Clamp stored distance to **16 cell widths**. The 32-cell gutter provides sufficient surrounding data for the intended glow.

This method has bounded sampling error. It must not apply a morphological closing operation, delete holes, or connect separated regions.

### GPU tile data

Use two point-sampled RGBA8 textures per tile:

- **Territory texture:** RGB encodes the 24-bit region ID; alpha encodes clamped normalized frontier distance.
- **Neighbor texture:** RGB encodes the opposite region ID; alpha is reserved for material flags.

Use explicit encode/decode helpers with round-trip tests.

Provide separate point-sampled metadata textures:

- Region ID → claimant-list offset and count.
- Flattened claimant list → empire color.

Use floating-point metadata for offsets/counts and ordinary color textures for colors. Do not pack empire counts into a single byte or impose a two-claimant limit.

## 5. Material and shader specification

All dimensions below are **framebuffer pixels**, independent of camera zoom. No new global UI-scaling setting is introduced.

| Setting | Main map | Minimap |
|---|---:|---:|
| Total frontier core width | 2 px | 1 px |
| Inward glow width | 8 px | 0 px |
| Base fill alpha | 0.06 | 0.14 |
| Maximum core alpha | 0.80 | 0.70 |
| Maximum inward glow alpha | 0.18 | 0 |
| Approximate stripe width | 8 px | 2 px |
| Grain modulation | ±2.5% | Disabled |

Keep these values together in `BorderVisualStyle`. Do not distribute unexplained numbers through drawing code.

### Region color

For a single claimant, use its empire color.

For contested territory:

```text
stripeWidthWorld = stripeWidthInSamples * cellWorldSize

band = floor(
    dot(worldPosition, normalize(float2(1, 1)))
    / stripeWidthWorld
)

claimantIndex = positiveModulo(band, claimantCount)
color = claimantColors[claimantIndex]
```

Requirements:

- Use every claimant, with equal stripe widths.
- Order claimants by empire ID.
- Handle negative world coordinates correctly.
- Anchor the phase to world coordinates.
- Do not introduce a time parameter.
- Do not average the whole contested area into one mixed color.
- At a sampling-level transition, blend the old and new pattern frequency briefly within the same region material; do not crossfade different ownership maps.

A small region may not physically fit a full stripe cycle. Preserve its true size and retain every claimant in its hover information.

### Core, glow, and fill

Shade each output location once:

1. Determine its categorical region.
2. Calculate its region color or contested stripe color.
3. Calculate core coverage from distance to the nearest frontier.
4. Calculate inward glow only when the region is nonempty.
5. Combine these into one material alpha using bounded coverage, rather than repeatedly adding transparent layers.
6. Output premultiplied color.

For a shared frontier, each side receives its own region color. The material therefore produces two correctly colored sides of one seam.

For an outer frontier, the narrow antialiased core may straddle the edge. The glow must remain inside claimed territory.

Decode four neighboring categorical samples. Choose the point-sampled region and sign each scalar distance according to whether that corner belongs to the chosen region. Bilinearly reconstruct the signed distance at the fragment, then apply the screen-space core/glow material with a smooth pixel-width antialiasing ramp. Never interpolate encoded IDs. Do not shade only cell centers and blend those colors: that quantizes line placement and can erase thin outlines when a fallback cell spans many pixels during zoom.

### Texture detail and compositing

- Generate grain procedurally from stable world coordinates.
- Filter or fade grain when it becomes subpixel.
- Preserve the game’s empire colors.
- Draw borders below system labels, ship icons, and selection indicators.
- Apply `GlobalStats.InfluenceNodeAlpha` **once**, in final composition.
- Remove the old repeated strength multipliers and separate additive-alpha border blending.
- Explicitly set and restore graphics state. Restore the previous render targets in `finally`.

### Effect integration

Ship `PoliticalBorders.fx` and its compiled `.mgfx` counterpart.

- Use the existing MonoGame shader infrastructure.
- Pin the local effect compiler to the repository’s MonoGame version, `3.8.1.303`.
- Load the compiled asset through mod-aware file resolution.
- Keep compilation a development/build operation; players load the compiled effect.
- Preserve required `SpriteShader` parameters if using that wrapper: `ViewProjection`, `Texture`, `UseTexture`, and `Color`.

**Batching warning:** per-tile secondary textures and parameters must be bound before the tile is flushed. Do not queue many tiles and repeatedly change shared shader parameters before `End()`; that can make every tile use the last tile’s bindings.

## 6. Cache, concurrency, minimap, and hover behavior

### Background work and publication

Use the existing `BorderWorker` budget.

- Permit at most one visual worker job at a time.
- Process at most four tiles per job, then release the worker slot.
- Keep one build in progress and one pending request containing the latest desired state.
- Never wait synchronously for a worker from the simulation or rendering thread.
- Perform all texture creation, upload, and disposal on the rendering thread.

Give visual snapshots monotonically increasing generation IDs. Hashes can accelerate comparison, but must not be the only protection against stale publication.

Finish useful in-progress work instead of continually restarting it as new simulation snapshots arrive. Publish generations monotonically. Reject results from another universe, a disposed renderer, or an obsolete knowledge/visibility epoch.

Publish a coherent set of visible tiles. Do not show adjacent tiles from different ownership generations or sampling levels.

### Invalidation

- Claim geometry changes: invalidate tiles intersecting the changed empire’s old or new field bounds, expanded by the gutter.
- Diplomacy or shared-system changes: invalidate the affected empires’ combined bounds.
- Knowledge changes: invalidate affected labels and hover data.
- Color/name changes: refresh presentation metadata without rebuilding geometry.
- Camera movement: reuse existing tiles; request newly visible tiles.
- Border strength changes: update the composite parameter only.
- Hidden or disabled borders: skip drawing and hover handling immediately.

Prefetch one tile beyond the viewport when resources permit.

Use limits of:

- **256 MiB per resident visual generation**
- **512 MiB total GPU border-cache budget during replacement**
- **384 MiB CPU cache/build budget**

Evict offscreen tiles first, then remove prefetching. Never silently delete small claims to fit a budget. If the supported test resolutions exceed these limits, treat that as a failed performance gate.

Limit uploads to **8 MiB or approximately 2 ms per frame**, whichever is reached first.

### Minimap

Build a low-resolution whole-galaxy overview from the same classifier and generation.

- Use the minimap’s actual bounds and projection.
- Draw political regions instead of political node circles.
- Keep sensor coverage as faint neutral shading.
- Keep threats and selected-object markers above territory.
- Use the same claimant ordering as the main map.

Until a complete detailed viewport is available, the overview may serve as a temporary whole-view fallback. Do not mix its coarse edges with fine tiles in the same main-map frame.

### Hover information

After **350 ms** over otherwise unoccupied map space:

- Single claimant: show its name and color.
- Shared peaceful frontier: show both neighboring empires.
- Contested region: show every known claimant.
- Unclaimed space: show no political tooltip.

Use the displayed snapshot for queries so the tooltip agrees with the image.

Reset the delay when the relevant region changes. Suppress border tooltips during dragging, box selection, UI interaction, or when an object tooltip takes priority.

Do not consume clicks, movement orders, or camera-panning input.

## 7. Implementation sequence and stop gates

Implement in this order:

1. **Capture baseline images and timings.** Use fixed scenes and cameras before replacing rendering.
2. **Implement immutable claimant classification.** Stop until gameplay-parity and knowledge tests pass.
3. **Implement tile labels and canonical frontiers.** First render debug flat colors; stop until seams and multiway intersections are correct.
4. **Implement distance fields and the material shader.** Add core, glow, stripes, and finally grain.
5. **Integrate main-map rendering.** Remove the old live per-empire border passes.
6. **Integrate minimap and hover behavior.**
7. **Add bounded caching and verify lifecycle behavior.**
8. **Run visual, performance, and gameplay regression checks.**
9. **Remove only rendering code proven unused by reference searches.** Preserve simulation helpers and unrelated assets.

If a gate fails, fix that stage. Do not compensate for incorrect classification with opacity changes or additional glow.

## 8. Tests and definition of done

### Classification tests

Cover:

- Two peaceful empires with equal and unequal strength.
- Existing tie behavior around `±0.001f`.
- Three-way peaceful junctions.
- Two, three, and five contested claimants.
- Nontransitive overlap relationships.
- Pirate and Remnant overlap.
- Shared systems.
- Enclaves, narrow passages, and disconnected pockets.
- Unknown empires and partially known fields.
- Disabled borders.

At sample centers, visible claimant lists must match the current gameplay decisions plus knowledge filtering.

### Geometry and cache tests

Verify:

- Reordering empire iteration does not change claimant membership or rendered colors.
- Each grid frontier has one canonical key.
- Adjacent tile gutters produce identical samples and distance data.
- Negative coordinates do not cause seams.
- Region ID encoding round-trips correctly.
- No obsolete result is published after unloading or changing universe.
- Camera movement at an unchanged sampling level does not rebuild resident tiles.
- Color or strength changes do not rebuild territory labels.
- The cache remains within its limits.

### Render tests

Capture fixed-camera output at:

- 1920 × 1080
- 2560 × 1440
- 3840 × 2160
- 3440 × 1440

Include close, sector, and galaxy views.

Check:

- Frontier widths remain consistent in pixels.
- Shared borders have no double brightness.
- Contested patterns contain all configured claimant colors.
- Textures remain anchored during panning.
- Junctions and tile edges have no cracks.
- Real passages remain unfilled.
- Hidden claimants contribute no colors or names.
- Zero border strength produces no political overlay.
- Increasing border strength produces a monotonic, approximately linear increase in opacity.
- Main-map and minimap classifications agree at the same world locations.

Use tolerant image assertions for antialiasing; do not require byte-identical screenshots across GPU drivers.

### Performance fixture

Use a deterministic scene containing:

- 12 major empires.
- Four factions.
- 1,000 border sources.
- Peaceful frontiers, contested clusters, shared systems, and enclaves.

Compare baseline and new rendering on the same machine, build configuration, resolution, and camera sequence.

Acceptance targets:

- No more than a **10% regression** in median or 95th-percentile frame time.
- No steady-state worker jobs or texture uploads when territory, knowledge, camera sampling level, and viewport coverage are unchanged.
- No synchronous worker waits.
- Memory remains within the specified budgets.

Record hardware, timings, allocations, uploads, and memory usage with the comparison images.

### Final verification

Run the existing border-policy, border-field, border-scene, and gravity-well-router tests, plus the new visual tests. Reuse the established separate build output directory to avoid locking a running game.

Store generated comparison PNGs under ignored test-result directories. Commit shader source, compiled runtime effect, implementation, and tests; exclude review screenshots.

The work is complete only when the geometry, visuals, performance, and existing gameplay regression gates pass. Report any remaining in-game validation honestly rather than describing untested appearance as verified.

## 13. Implementation record

Implemented in the working tree on 2026-09-28:

- `BorderField.cs`: `BorderScene` is the immutable visual snapshot boundary. It now captures names, colors, knowledge, and a monotonic generation alongside the existing fields and overlap rules. `Classify` uses caller-owned scratch buffers, exact gameplay competition, structural claimant sets, and ID ordering. Snapshot equality verifies captured inputs rather than trusting a hash alone.
- `BorderVisualTile.cs`: global-origin 256-cell tiles with 32-cell gutters, canonical right/down frontiers, a half-cell separable Euclidean distance transform, exact incident-edge refinement in spatial buckets, categorical RGBA8 labels/distances, neighbor labels, and unrestricted claimant palettes.
- `BorderVisualRenderer.cs`: the cache and renderer share one graphics-thread owner. Workers create CPU products only, four tiles per job through `BorderWorker`. Overview/detail publication is coherent; unchanged tiles are retained, palette changes avoid raster work, withdrawn knowledge suppresses stale presentation, and camera movement reuses/prefetches tiles. GPU resources are reference-counted across generations and disposed on universe unload. Uploads are limited by the frame budget; individual graphics API calls cannot be preempted.
- `PoliticalBorders.fx` and `PoliticalBorders.mgfx`: premultiplied procedural material, screen-space outlines/glow, all-claimant world-anchored stripes, subtle grain, point-decoded labels, subcell signed-distance reconstruction, and screen-space antialiasing. LOD changes interpolate stripe frequency over 200 ms without blending ownership.
- Main-map and minimap rendering now share this cache. The minimap clips political tiles to its container, uses neutral sensor halos, and draws system/selection/warning markers above the political material. The border-strength slider applies once per presentation.
- Stationary map hover waits 350 ms, reads the displayed tile, lists all contested claimants or both sides of a shared frontier, and yields to UI/object tooltips and camera/selection input.
- Solar debug now shows border job/upload counts and GPU cache size instead of obsolete blend controls.

The production ownership, growth, routing, diplomacy, and station-transfer algorithms were not changed by this visual implementation. The old render helper remains only in the comparison tests; `BorderNodeCache.BuildConnections` remains available to gameplay.

### Reproducible build and verification

The local tool manifest pins `dotnet-mgfxc` to `3.8.1.303`, matching the game runtime:

```powershell
dotnet tool restore
dotnet tool run mgfxc game/Content/Effects/PoliticalBorders.fx game/Content/Effects/PoliticalBorders.mgfx /Profile:DirectX_11
dotnet build UnitTests/SDUnitTests.csproj --no-restore -p:Platform=x64 -p:OutDir=D:/Aurrix/StarDrive/UnitTests/bin/BorderVisualReview/ -v:minimal
dotnet test UnitTests/SDUnitTests.csproj --no-build --no-restore -p:Platform=x64 -p:OutDir=D:/Aurrix/StarDrive/UnitTests/bin/BorderVisualReview/ --filter 'FullyQualifiedName~BorderVisualTests|FullyQualifiedName~BorderFieldTests|FullyQualifiedName~BorderSceneTests|FullyQualifiedName~BorderPolicyTests|FullyQualifiedName~TestGravityWellRouter' -v:minimal
```

`BorderVisualTests` covers gameplay/classifier parity, enumeration independence, epsilon ties, non-transitive conflicts, five claimants, hidden competition, negative tile coordinates, gutter distances, label packing, cache recoloring, disposal during work, minimap clipping, and linear strength scaling. Render fixtures produce close/sector/galaxy captures at 1920×1080, 2560×1440, 3840×2160, and 3440×1440. The 1,000-source benchmark contains 12 majors and four factions, synchronizes the GPU with readback, compares 30 measured frames after warmup, and enforces median/p95 non-regression and warm-cache inactivity.

Review artifacts are intentionally ignored under `UnitTests/TestResults/Borders/`. `performance.txt` records the adapter, timings, allocations, uploads, and cache sizes for the latest run. These are isolated border-pass measurements, not full-game FPS claims.

### Remaining manual acceptance

The automated fixtures do not replace a live saved-game playthrough. Verify hover priority alongside the sidebar/dashboard, camera panning and tilted projection, sustained ownership/knowledge changes, and complete game-frame timings in a large running galaxy before treating the full in-game acceptance gate as signed off.

### Zoom-transition regression

Rapid zooming exposed a material bug: shading only coarse fallback cell centers erased the thin outline when one cell covered many framebuffer pixels. The regression test reproduced a peak alpha of 15 (the fill alone) at 20 world units per pixel. The corrected material reconstructs signed distance at the fragment before applying screen-space line width whenever fallback cells are magnified. IDs remain point sampled. The cache also chooses the closest complete resident LOD before using the galaxy overview, and bounds candidate tile enumeration so zooming out from a very fine cached level cannot allocate an enormous key list.

`BorderOutlineSurvivesPendingZoomLevel` jumps repeatedly between close and wide views without waiting for replacement tiles and requires a visible outline on every frame. The old shader fails this test; the corrected shader passes. The separate planet-view cutoff was removed so the political overlay does not switch off at close zoom. Minimap vertex buffers are explicitly recycled each frame; the comparison fixture mirrors the shared game renderer's buffer recycling as well.

Geometry shrinkage is also kept separate from knowledge withdrawal: declining influence radius must rebuild affected tiles without blanking the current overlay. `BorderShrinkageDoesNotBlankKnownTerritory` verifies that distinction and still requires suppression when a previously known source becomes hidden.

### Recorded automated results

- Build: successful, zero warnings and errors.
- Border regression/render suite: 56 tests passed, including rapid zoom, influence shrinkage, subcell smoothness, developer visibility, and captures at all four specified resolutions.
- Final synchronized benchmark with normal vertex-buffer recycling, NVIDIA GeForce RTX 3060 Laptop GPU, 1920×1080: legacy median/p95 **104.858 / 110.746 ms**; new median/p95 **0.741 / 1.303 ms**. These are isolated border-pass timings, not complete game-frame timings.
- Warm cache: zero new worker jobs or texture uploads; 84 resident tiles, **68.25 MiB** of tile GPU textures and **32.81 MiB** of resident label buffers. Average measured render-thread allocations were 6,747 bytes per frame for the new pass.
- Earlier exploratory benchmark numbers predated the fixture's normal buffer recycling and are superseded by these results.

### Smooth outlines and developer visibility

Frontiers are no longer fixed to the midpoint between raster samples. Each differing-label edge brackets its true classifier transition with seven bisection steps; the distance transform then refines distances against those subcell edge positions. The shader reconstructs the signed contour before shading at every LOD, uses a smooth pixel-width antialiasing ramp, and suppresses grain on the bright line itself. Ownership IDs and claimant palettes remain categorical.

`BorderScene.RevealAll` is an explicit presentation-only debug override. It bypasses both the empire-known check and the known-node field, while keeping normal ownership competition unchanged. The render thread can switch this override on a captured immutable snapshot while paused, so developer full-map visibility does not wait for simulation updates. Leaving debug invalidates the reveal cache and immediately suppresses stale debug information. Normal gameplay knowledge is never modified.

The screen retains its last ordinary snapshot when developer simulation expands known-node fields. Exiting developer mode while paused restores that ordinary snapshot rather than treating expanded debug fields as normal knowledge; if no ordinary snapshot exists yet, presentation waits for one.

Visible detail tiles are scheduled before the offscreen prefetch ring, so the renderer resolves a coarse zoom fallback as soon as the visible area is ready.

`SmoothContourTracksSubcellGameplayBoundary` checks mean contour-position error against gameplay below 0.08 raster cells. `DeveloperViewRevealsUnknownBordersAndCanBeRevokedWhilePaused` verifies reveal, cache invalidation, and restoring normal visibility without new simulation data.

### Reducing rebuild pressure during music playback

After reports that hiding borders stops periodic audio glitches, visual geometry refreshes now coalesce simulation snapshots during a two-second cooldown after publication. Camera detail requests remain independent; revoked knowledge and changes to developer reveal bypass the cooldown. Gameplay border updates retain their existing frequency. Under load, visual ownership changes may take the cooldown plus tile-generation time to appear.

Each renderer reuses a private distance-transform scratch workspace across its sequential CPU jobs, eliminating approximately 3.3 MB of temporary allocations per tile. Jobs now build one tile instead of four, bounding completed-tile upload bursts to one tile per update. Tile classification, contour resolution, and antialiasing are unchanged. `ReusedTileScratchPreservesContoursAndReducesAllocations` checks identical raster output after reusing dirty scratch buffers and at least 3 MB less allocation per tile.

These changes address rebuild allocation pressure; uninterrupted audio still requires validation in the affected live save. Earlier warm-cache rendering timings do not measure audio underruns or active rebuild pressure.

Following continued (less frequent) glitches, music now uses a separate WASAPI output and mixer with a 300 ms requested native buffer. Effects retain their 50 ms output. Music categories use the same case-insensitive name classification as the existing music volume setting. Both mixers receive video mute; device teardown stops outputs before disposing their sample providers. This isolates music from effects mixing and adds tolerance for short scheduling/GC stalls, but does not isolate it from process-wide GC or guarantee uninterrupted playback during longer stalls. Music control response can be delayed by buffered audio. Six audio tests pass, including separate routing, mute/unmute, and initialization-failure cleanup; live playback validation remains required.

### Treaty stability and urgent ownership updates

Routine low-trust cancellation of open borders now requires at least 50 relationship turns and total anger of at least 20 as well as trust below 5. Signing a new agreement resets its age; refreshing an existing agreement does not. War declarations and alliance cancellation remain independent. Open borders already suppress trespass anger and border-violation attacks; they now also prevent the strength-based colonial provocation policy. Transit does not grant settlement rights or a general non-aggression guarantee.

Visual scenes capture colony IDs. Colony ownership changes, influence-source additions/removals, defeat, and visibility changes bypass the two-second geometry cooldown. Urgent replacements are accepted after completing and publishing a coherent overview, rather than discarding progress after each tile. This prevents a changing galaxy from starving the first visible overview. Actual knowledge withdrawal and leaving developer reveal still suppress stale information. Urgent means scheduling promptly, not synchronously constructing expensive geometry on the render thread.

### Paused saved-game initialization

Loading starts asynchronous field builds, including empty fields for empires with no influence sources. Previously, a paused simulation never drained those jobs or scheduled empires that could not acquire a worker slot; the visual renderer waited indefinitely for complete snapshots. Paused object updates now finish only outstanding border geometry and publish its immutable scene, without advancing time, contacts, diplomacy, or station transfers. Once all geometry is ready, the paused path stops rebuilding it. `PausedLoadCompletesAndPublishesBorderGeometry` verifies completion without unpausing or changing the stardate. The focused validation run passed 40 load/UI tests and 13 border visual regressions.

An attempted synchronous first-frame warm-up caused long stalls in final screen initialization (the reported log showed approximately 33 seconds). That path has been removed: neither initial fields nor overview rasterization are awaited by save loading. Field generation continues while paused, and the renderer publishes its overview asynchronously before refining detail. GPU tile uploads remain on the drawing thread; saved-game screen setup runs on a background loader. Loading progress is capped at 99% until the load result is actually complete and can be entered. Borders can appear shortly after entering the game. Render textures remain derived caches, not serialized save data. `BackgroundOverviewPublishesAndDrawsWithoutSimulationTicks` verifies visible outlines from the background-built overview without advancing the simulation.

### Preventing background-render starvation

The renderer no longer gates all progress on every active empire having a prepared field. It classifies available snapshots, publishes ready borders, and incorporates late fields into subsequent generations. Pending fields can temporarily change the visible ownership boundary as they arrive. Simulation fields and visual tiles have separate single-job slots (two jobs total), so repeated simulation builds cannot take every presentation slot. An overview is completed and published before urgent replacement, avoiding perpetual cancellation under colony/source churn. Regression coverage includes an indefinitely missing rival field, requests changing after every tile, and a visual task completing while the simulation slot is occupied.

### Complete borders before entering a saved game

The loading screen now has a separate asynchronous border preparation phase after the background loader returns. Before universe drawing starts signaling the simulation thread, it polls outstanding empire geometry without advancing game time. Only after every non-defeated empire has a snapshot does it build a frozen, complete overview. Tile computation remains on the visual worker; bounded GPU uploads run in loading-screen Draw on the graphics thread. The loading screen shows “Preparing empire borders...” and enables entry only when that overview is published. Hidden or disabled borders skip this phase. This supersedes the earlier behavior of entering immediately and revealing ready empires progressively; progressive updates remain available during ordinary gameplay. No synchronous GPU warm-up is added to background LoadContent, and no border textures are serialized. LoadingScreenPreparesAllEmpiresBeforeEntryWithoutSimulation covers initial rejection, complete publication, and unchanged game time.

### Saved border caches

New saves now serialize an optional SavedBorderCache on each empire and a BorderOverviewCache on UniverseState. This supersedes the previous decision not to serialize derived border data. Territory caches contain raw/smoothed fields and a SHA-256 identity covering source IDs, positions, radii, growth, player knowledge, and projector radius. Initialization reconnects current game objects, validates the identity and field dimensions, and reuses valid fields. Missing or mismatched caches use the asynchronous rebuild path. No graphics resources or task objects enter the save graph.

Only the complete galaxy overview is retained as compressed CPU tile data, not all camera-detail levels. Compression runs on the existing visual worker before upload arrays are released. A deterministic scene identity includes all field data, empire ordering, active/known flags, colors, contested relationships, shared systems, and debug visibility. Loading restores only matching overview tiles and uploads them with the normal bounded graphics-thread budget; detailed zoom levels refine afterward. Cache validation uses stable hashes, never process-randomized HashCode values. Immutable field digests are reused without copying large arrays for every scene. Palette-only GPU changes invalidate the corresponding compressed pixels rather than marking old colors as current.

Bump SavedBorderGeometry.CurrentVersion whenever field/connection algorithms change, and SavedBorderOverview.CurrentVersion whenever tile encoding, rasterization, or shader interpretation changes. Invalid, absent, or incompatible caches rebuild normally. Old saves remain loadable and gain caches when saved again after preparation. A save made while geometry is changing may contain an older overview; validation deliberately rejects it. Save size increases by serialized fields and compressed overview data. Tests cover full-save serialization, field reuse, zero rasterization jobs on cached overview restore, changed inputs, version changes, damaged tile data, and the deserialize/initialize/loading-screen path.
