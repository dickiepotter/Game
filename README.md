# RP.Game — a from-scratch C# game engine (the reusable middle layer)

`RP.Game` is the **engine and generic game-mechanics library** sitting between the pure-maths
[`RP.Math`](../Math) library and a specific game. It is deliberately a sibling of `RP.Math` in style:
one root namespace equal to the assembly name (`RP.Game`), with area sub-namespaces beneath it, and the
same "the code teaches" ethos — every non-obvious concept is explained where it first appears.

```
RP.Math  ─┐   pure mathematics, no dependencies
          ├─► RP.Game        engine + mechanics, no platform   ← you are here
RP.Sound ─┘        ▲
                   │
              RP.Game.Silk   + Silk.NET: Vulkan · OpenAL · windowing
                   ▲
              RP.Spectre     one specific game
```

**The naming convention:** the base package *is* the engine, and every satellite is named for the
dependency it brings with it — `RP.Game.Silk` is `RP.Game` plus Silk.NET. A future OpenGL or
headless-server backend would be `RP.Game.<whatever it needs>` on the same pattern. The name tells
a consumer what they are taking on, and `RP.Game` on its own always means the part that costs
nothing.

**The boundary rule:** nothing in `RP.Game` may know about any particular game. If a type would need
renaming or gutting to drop into a completely different game, it belongs in the game, not here. The acid
test: it must make sense in a game that has nothing to do with space, ships, or wrecks.

**The second boundary — engine versus platform.** The library ships as two assemblies. `RP.Game`
holds everything that is pure computation: the fixed-timestep loop, logging, mechanics, physics,
scene management, the rendering *data* types and the steering behaviours. `RP.Game.Silk` holds the
ten files that genuinely need a platform underneath them — the Vulkan backend, the OpenAL audio
engine, the windowing layer, and the one camera that reads a keyboard.

The cut is worth the extra project because the two halves have wildly different costs. `RP.Game` is
36 files that reference nothing but `RP.Math` and `RP.Sound`; `RP.Game.Silk` drags in seven
Silk.NET packages, native graphics and audio libraries, and a shader-compilation build step that
wants the Vulkan SDK installed. A 2D WPF application that wants `FixedTimestepAccumulator` and
`JsonStore` should not have to ship any of that to get them — and now it does not.

Two details make the split cheap rather than disruptive. **No namespace moved**: the types in
`RP.Game.Silk.dll` are still `RP.Game.Graphics.Vulkan`, `RP.Game.Platform` and `RP.Game.Audio`, so
not one consumer `using` had to change. And **every existing test already covered the engine half**
— not one of them touched Silk.NET — which is what showed the seam was in the right place before it
was cut.

## Areas (filled in as the build proceeds)

- **Core** — the fixed-timestep loop, time, (later) logging, events, pooling, seedable RNG. *Present.*
- **Graphics** — thin renderer interface with a **Vulkan 1.3** backend (Silk.NET) behind it. *Planned.*
- **Rendering** — meshes, vertex layouts, materials/shaders, instancing, render passes, cameras. *Planned.*
- **Platform** — window, input, audio bring-up. *Planned.*
- **Scene** — entity/component model, transform hierarchy, spatial partitioning, frustum culling. *Planned.*
- **Physics** — rigid-body state, the integration driver, broad/narrow-phase, impulse resolution. *Planned.*
- **Audio** — generic 3D mixer, buses, DSP. *Planned.*
- **Mechanics** — state machine, save/settings framework, input-binding, difficulty scalars. *Planned.*
- **Assets** — resource loading + streaming. *Planned.*

## Core, today: the fixed-timestep loop

`RP.Game.Core.FixedTimestepAccumulator` is the heart of frame-rate-independent simulation. It separates
the variable rate at which frames are *drawn* from the fixed rate at which the game is *simulated*, so
physics behaves identically at 30, 60, or 144 fps. Read the source — it is written as a lesson on the
"Fix Your Timestep" pattern, including the spiral-of-death clamp and the interpolation `Alpha` used to
render smoothly *between* simulation steps.

## Build & test

This library builds standalone via `Game.sln`, or as part of the game via `../Spectre/Spectre.sln`.

```sh
dotnet build Game.sln
dotnet test  Game.sln
```
