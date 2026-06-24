# RP.Game — a from-scratch C# game engine (the reusable middle layer)

`RP.Game` is the **engine and generic game-mechanics library** sitting between the pure-maths
[`RP.Math`](../Math) library and a specific game. It is deliberately a sibling of `RP.Math` in style:
one root namespace equal to the assembly name (`RP.Game`), with area sub-namespaces beneath it, and the
same "the code teaches" ethos — every non-obvious concept is explained where it first appears.

```
RP.Math      pure mathematics, no dependencies
   ▲
RP.Game      engine + mechanics valid for ANY game   ← you are here
   ▲
RP.Spectre   one specific game
```

**The boundary rule:** nothing in `RP.Game` may know about any particular game. If a type would need
renaming or gutting to drop into a completely different game, it belongs in the game, not here. The acid
test: it must make sense in a game that has nothing to do with space, ships, or wrecks.

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
