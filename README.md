# Dynamic Vessel Weathering

**Physically based aging, heat damage and dust for Kerbal Space Program spacecraft.**

![KSP](https://img.shields.io/badge/KSP-1.12.5-blue)
![Unity](https://img.shields.io/badge/Unity-2019.4.18f1-lightgrey)
![Status](https://img.shields.io/badge/status-alpha-orange)
![License](https://img.shields.io/badge/license-MIT-green)

Dynamic Vessel Weathering (DVW) makes your spacecraft carry the marks of what they have been through. A heat shield chars a little more with every reentry. A stainless-steel hull shows heat-tint colors on the windward side. A Duna rover collects red dust, and a satellite that has spent years in orbit has visibly faded paint and solar panels.

Every effect is driven by simulation, not by a timer or a global tint. The mod looks at the actual flow direction, the temperatures computed by KSP, the time spent at each temperature, sunlight exposure and the surface the vessel sits on. It then works out which surfaces are affected and by how much.

<!-- Screenshots: add before/after images here, e.g. docs/images/reentry.png -->

---

## Table of contents

- [Features](#features)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Surface materials](#surface-materials)
- [Compatibility](#compatibility)
- [Testing tools](#testing-tools)
- [Building from source](#building-from-source)
- [Project structure](#project-structure)
- [Known limitations](#known-limitations)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)
- [References](#references)

---

## Features

### Physically based reentry heating
- The skin temperature KSP computes for each part is redistributed over the part's surfaces using a **Newtonian / Lees heating distribution**. Windward surfaces take the heat; leeward surfaces receive about 3 % of the peak flux, in line with Space Shuttle flight measurements (1–5 %).
- **Aerodynamic shadowing**: upstream raycasts detect surfaces sitting in the wake of other parts. A tail fin behind the fuselage stays clean, and so do the upper surfaces of a high angle-of-attack glider.
- **Nose-radius effect (Sutton–Graves)**: noses and leading edges run hotter than broad, flat bellies.
- **Radiative equilibrium**: local surface temperature scales with the fourth root of the local heat flux.
- Flow-aligned streaks, so the marks follow the direction of the airflow during reentry.

### Material-aware discoloration
Each part is treated as one of five surface types, and each reacts to heat differently:

| Material | Behavior |
|---|---|
| **Paint** | Yellows, browns, then chars. Driven by Arrhenius kinetics, so time at temperature matters as much as peak temperature. |
| **Steel** | Stainless-steel heat-tint (temper) colors set by the peak temperature reached: straw, gold, bronze, muted purple and blue-grey, then grey oxide and dark scale. |
| **Ablator** | Progressive charring proportional to the accumulated heat load above the pyrolysis temperature. Ablation products leave soot on the windward side. |
| **Foil** | Aluminized Kapton tape over an ablator, as on the Apollo Command Module. It scorches from silver to gold and bronze, then peels off in sharp-edged patches that expose rust-colored and then charred ablator. |
| **SolarPanel** | Gradual UV and radiation aging: duller, greyer, lower contrast. |

### Space aging
- UV aging accumulates while the vessel is in sunlight. It accounts for **planetary shadow**, **distance to the Sun** (inverse-square law) and **atmospheric filtering**.
- It follows a saturating curve: fast at first, then slower, and capped at a configurable maximum.
- Elapsed time is **caught up when a vessel loads**, including time spent unloaded or under time warp. A satellite left alone for five years looks five years old when you come back to it.
- Optionally, solar panel aging can reduce power output through KSP's `efficiencyMult`. This is off by default.

### Surface dust
- Dust settles on upward-facing surfaces while landed.
- Dust color and deposition rate are configured per celestial body: grey regolith on the Mun, red-orange dust on Duna, and so on.

### Non-destructive rendering
- No texture, model or material belonging to KSP or another mod is modified.
- Weathering is drawn as a lightweight overlay of each part's own mesh.
- It uses the real scene lighting, including sun direction, shadows and ambient light.
- The original texture is sampled, so faded paint keeps its markings and panel lines.

### Persistence
- All weathering state is saved per part in your save file and in `.craft` files.

---

## How it works

### Rendering

For each renderer of a part, DVW adds a child `MeshRenderer` that shares the original mesh and draws it again with the `DVW/WeatheringOverlay` shader.

- **One shared material for the whole game.** Every per-renderer value goes through a `MaterialPropertyBlock`, set per sub-mesh: weathering state, the part-space transform, and a reference to the original texture. Memory use therefore stays flat no matter how many vessels are loaded.
- **Overlay only when needed.** A part gets its overlay drawn only once it has visible weathering, and only while its original renderer is enabled.
- **Proper scene integration.** The shader runs in the `ForwardBase` pass, so it receives the main directional light, screen-space shadows and spherical-harmonics ambient light.
- **Six directions per part.** Weathering is stored for the six axis-aligned faces of each part (±X, ±Y, ±Z, in part space). The shader blends them using the surface normal, with ambient-cube weighting, and breaks up the result with procedural noise computed in part space.

### Reentry heating model

For each face *i*, with θᵢ the angle between the face normal and the relative wind:

$$
f_i = f_{lee} + (1 - f_{lee})\,\max(0,\cos\theta_i)^{1.5}, \qquad f_{lee} = 0.03
$$

- **Wake.** Faces found to be in another part's wake (by upstream raycasts) fall back to the wake fraction.
- **Nose radius.** Windward faces are scaled by a Sutton–Graves nose-radius factor, $q \propto R^{-1/2}$.
- **Face temperature.** It is obtained by radiative equilibrium, using KSP's exposed skin temperature $T_{exp}$ as the reference:

$$
T_i^4 = T_{air}^4 + \left(T_{exp}^4 - T_{air}^4\right)\frac{f_i}{f_{max}}
$$

### Thermal damage kinetics

Thermal damage *D* (from 0 to 1) is accumulated with a saturating law: $D \leftarrow 1 - (1 - D)\,e^{-k\,\Delta t}$.

- **Paint, panels and foil** follow Arrhenius kinetics:

$$
k(T) = \frac{1}{t_{ref}} \exp\!\left[-\frac{E_a}{R}\left(\frac{1}{T} - \frac{1}{T_{ref}}\right)\right]
$$

  With the default values (paint: $E_a$ = 100 kJ/mol, $T_{ref}$ = 650 K, $t_{ref}$ = 120 s), a surface at 550 K is practically unaffected after a full reentry, while a surface at 1300 K chars within seconds.
- **Ablators** use $k = (T - T_{pyrolysis}) / L$. $T_{pyrolysis}$ is read from the part's own `ModuleAblator`, so charring starts exactly where KSP starts consuming ablator.

### Simulation loop

- A `VesselModule` ticks each loaded vessel at a configurable interval (1 s by default). It captures the environment once — atmosphere, velocity, sunlight, body — and passes it to every part.
- Parts no longer run any per-frame logic.
- Effects that depend on physics (reentry, dust) only run while the vessel is off rails.
- Space aging accepts any time step, so time warp is handled exactly.

---

## Requirements

| Dependency | Version | Notes |
|---|---|---|
| Kerbal Space Program | 1.12.5 | Other 1.12.x versions may work but are untested |
| [ModuleManager](https://github.com/sarbian/ModuleManager) | 4.x | **Required.** Adds the weathering module to parts and applies material patches |

DVW runs on stock KSP and does not need either DLC.

---

## Installation

1. Install ModuleManager.
2. Download the latest release.
3. Copy the `DynamicVesselWeathering` folder into your KSP `GameData` folder:

```
Kerbal Space Program/
└── GameData/
    ├── ModuleManager.4.x.x.dll
    └── DynamicVesselWeathering/
        ├── Config/Weathering.cfg
        ├── Localization/
        ├── Patches/
        ├── Plugins/DVW.dll
        └── Shaders/dvw-windows.bundle   (or -linux / -macos)
```

To uninstall, delete the folder. Weathering data left in your saves is ignored by KSP once the mod is removed.

---

## Configuration

All tuning lives in `GameData/DynamicVesselWeathering/Config/Weathering.cfg`, inside a `WEATHERING_CONFIG` node. The file is read when KSP starts.

| Key | Default | Description |
|---|---|---|
| `TickInterval` | `1.0` | Real-time seconds between two updates of a vessel |
| `MaxPhysicsDt` | `5.0` | Largest time step used for reentry and dust |
| `UVEnabled` | `true` | Enables space aging |
| `UVRate` | `1.0` | Global multiplier for UV aging |
| `UVTimeConstantYears` | `4.0` | Years of full sunlight at Kerbin's distance to reach ~63 % of `UVMax` |
| `UVMax` | `0.6` | Maximum discoloration |
| `SunlitFractionOnRails` | `0.7` | Fraction of time assumed in sunlight while a vessel is on rails or unloaded |
| `AtmosphereUVFactor` | `0.1` | UV transmitted through an atmosphere |
| `ReentryEnabled` | `true` | Enables reentry effects |
| `SootRate` | `1.0` | Multiplier for ablation soot |
| `AblatorCharLoad` | `250000` | Heat load (K·s above pyrolysis) that gives ~63 % charring. Higher means slower charring |
| `PaintActivationEnergy` | `100000` | Activation energy for paint burn kinetics (J/mol) |
| `PaintRefTemp` / `PaintRefTime` | `650` / `120` | ~63 % paint damage after `RefTime` seconds at `RefTemp` kelvin |
| `FoilActivationEnergy` | `150000` | Same as above, for the Foil material |
| `FoilRefTemp` / `FoilRefTime` | `700` / `120` | Same as above, for the Foil material |
| `DustEnabled` | `true` | Enables surface dust |
| `DustRate` | `1.0` | Global multiplier for dust deposition |
| `SolarPanelDegradation` | `0.0` | `0` = visual only; `1` = power loss equal to the discoloration |

The same file holds:
- **`MATERIAL`** nodes: per-material UV sensitivity.
- **`BODY_DUST`** nodes: dust color and rate for each celestial body.

```cfg
BODY_DUST { name = Duna  color = 0.62,0.33,0.20  rate = 1.2 }
```

Bodies with no entry use a neutral default.

---

## Surface materials

By default each part's material is detected automatically (`material = Auto`):

| Detected module | Material |
|---|---|
| `ModuleDeployableSolarPanel` | SolarPanel |
| `ModuleEngines` with throttle control | Steel (solid boosters are excluded) |
| `ModuleAblator` on an uncrewed part | Ablator |
| Anything else | Paint |

You can override this with a ModuleManager patch:

```cfg
@PART[myStainlessTank*]:HAS[@MODULE[ModuleWeathering]]:AFTER[DynamicVesselWeathering]
{
    @MODULE[ModuleWeathering] { %material = Steel }
}
```

The material is saved with the vessel. Existing crafts keep their stored value; parts placed afterwards pick up the new default. In the editor, you can also change the material of a single part from its right-click menu.

---

## Compatibility

- **Stock parts and both DLCs**: supported automatically.
- **Part mods**: renderers are discovered at runtime, so most part packs work without any patch. A dedicated patch is only needed to assign a non-default material.
- **Bluedog Design Bureau**: a bundled patch (`Patches/BDB.cfg`, applied only when BDB is installed) gives the Apollo Command Module the Foil material. BDB heat shields are detected as Ablator automatically.
- **Waterfall and particle effects**: renderers that use effect shaders are excluded automatically.
- **Visual mods** (Scatterer, EVE, TUFX, TexturesUnlimited, Parallax): DVW never replaces their shaders or textures. The weathering overlay uses its own simple lighting model, so on PBR-shaded parts it may not match the base material exactly.

---

## Testing tools

The right-click menu of every part shows debug fields and test actions, in the editor and in flight:

| Action | Effect |
|---|---|
| `DVW: next material` | Cycles Auto → Paint → Steel → Ablator → SolarPanel → Foil |
| `DVW: TEST 1 simulated reentry (+X windward)` | Integrates a 150 s low-orbit reentry (peak 1300 K) with the full thermal model. Click again to simulate further missions |
| `DVW: +50 K all faces` | Steps through the steel heat-tint palette |
| `DVW: +1 year in sunlight` | Adds one year of UV exposure |
| `DVW: +25% soot on -Y face` | Adds soot on one face |
| `DVW: +25% dust on +Y face` | Adds dust on one face |
| `DVW: clean` | Resets the part |

The debug fields show, per face:
- peak temperature;
- thermal damage;
- the fraction outside the wake (flight only);
- soot, dust and UV aging.

> All in-game text is localized. English (`en-us`) and French (`fr-fr`) are included; other languages fall back to the localization keys. Translations are welcome, see [Contributing](#contributing).

---

## Building from source

### Plugin

Prerequisites: the .NET SDK (any recent version that can target `net472`) and a KSP 1.12.5 installation.

1. Set `KSPDir` in `DVW/DVW.csproj` to your KSP installation.
2. Build:

```bash
cd DVW
dotnet build -c Release
```

A post-build step copies `DVW.dll` and every `.cfg` from `DynamicVesselWeathering/` into `KSPDir/GameData/DynamicVesselWeathering/`.

### Shaders

Shaders must be compiled with **exactly Unity 2019.4.18f1**, the version KSP 1.12 is built on.

1. Open the `DVW_Shaders` folder as a Unity project.
2. Set `KSPDir` in `Assets/Editor/BuildBundles.cs`.
3. Run **DVW → Build AssetBundle (Windows)**, or **Build AssetBundles (all platforms)** if the Linux and macOS build modules are installed.

The bundles are compiled for Direct3D 11 and OpenGL Core (plus Metal on macOS), then copied straight into `GameData`.

---

## Project structure

```
├── DVW/                              C# plugin (net472)
│   ├── Core/WeatheringSettings.cs    Config loading, materials, per-body dust
│   ├── Parts/ModuleWeathering.cs     Per-part state, simulation, persistence, debug UI
│   ├── Parts/ShaderLibrary.cs        AssetBundle loading, shared material
│   ├── Rendering/OverlayController.cs Overlay renderers and MaterialPropertyBlocks
│   └── Simulation/
│       ├── DVWVesselModule.cs        Per-vessel tick and environment capture
│       ├── VesselEnv.cs              Environment snapshot (sunlight, distance, atmosphere)
│       └── ReentryHeating.cs         Heating distribution, wake raycasts, kinetics
├── DVW_Shaders/                      Unity 2019.4.18f1 project
│   └── Assets/WeatheringOverlay.shader
├── DynamicVesselWeathering/          GameData content
│   ├── Config/Weathering.cfg
│   ├── Localization/{en-us,fr-fr}.cfg
│   └── Patches/{AddModule,Materials,BDB}.cfg
├── LICENSE
└── README.md
```

---

## Known limitations

- **Six directions per part.** The effect varies around a part but not along it. Fine detail such as streaks behind individual tiles would need texture-space masks.
- **One material per part.** A part that mixes materials (for example tiles and bare steel) is treated as a single material.
- **Gloss.** The overlay can change color but cannot remove the base material's specular highlight, so the loss of gloss is only approximated.
- **Secondary lights.** Only the main light (the Sun) lights the weathering; spotlights do not yet.
- **Unsupported meshes.** Procedurally generated meshes (stock fairings, procedural parts) and skinned meshes do not receive an overlay yet.
- **Shadowing relies on colliders.** A part without colliders does not cast an aerodynamic shadow.
- **Heating magnitude follows KSP.** The overall amount of heat comes from KSP's thermal model, so gentler-than-real reentries produce proportionally less damage.

---

## Roadmap

- [ ] Engine exhaust soot and plume-driven dust during landing and takeoff
- [ ] Atmospheric grime for low-altitude flight
- [ ] Cleaning and refurbishment (EVA, workshop) and a public API for other mods
- [ ] Vessel condition window (per-part breakdown)
- [ ] Performance / High Quality presets and distance-based LOD
- [ ] Overlay support for procedural fairings
- [ ] Per-sub-mesh materials

---

## Contributing

Bug reports, screenshots, patches for part mods and translations are welcome. To add a language, copy `Localization/en-us.cfg`, rename both the file and its inner node to the KSP language code (for example `de-de`), and translate the values.

When reporting an issue, please include:

- your `KSP.log` (search for lines starting with `[DVW]`);
- the list of installed mods;
- a screenshot of the right-click debug fields of the affected part.

---

## License

Dynamic Vessel Weathering is released under the [MIT License](LICENSE).

---

## References

- Sutton, K. & Graves, R. A. (1971). *A General Stagnation-Point Convective-Heating Equation for Arbitrary Gas Mixtures.* NASA TR R-376.
- Lees, L. (1956). *Laminar Heat Transfer Over Blunt-Nosed Bodies at Hypersonic Flight Speeds.* Jet Propulsion, 26(4).
- Apollo Command Module thermal-control Kapton tape and post-flight appearance: [collectSPACE](https://www.collectspace.com/ubb/Forum14/HTML/000379.html), [NASA Apollo Flight Journal](https://www.nasa.gov/history/afj/ap13fj/30postflight.html).
- *Kerbal Space Program* is a trademark of Take-Two Interactive. This project is not affiliated with or endorsed by Take-Two Interactive or Squad.
