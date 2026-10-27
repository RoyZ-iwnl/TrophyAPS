# Trophy APS

Trophy hard-kill active protection system for the M1 and M1IP Abrams in
**Gunner, HEAT, PC!**

Radar-tracked, ellipsoidal detection volume, two launchers with four rounds each.

## Install

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.6.1 into the GHPC folder.
2. Put `VanillaAssetUtil.dll` in `Bin\Mods\`.
3. Put `TrophyAPS.dll` in `Bin\Mods\`.
4. Put the model bundle at `Bin\Mods\TrophyAPS\trophy`.

```
Bin\
└── Mods\
    ├── VanillaAssetUtil.dll
    ├── TrophyAPS.dll
    └── TrophyAPS\
        └── trophy
```

`trophy` has **no file extension**. If your archiver renamed it to `trophy.bundle`
or `trophy.unity3d`, the model will not load.

The launcher muzzle flash is borrowed from the vanilla M2 Bradley, so
VanillaAssetUtil must be present even though the Bradley itself is not used.

## Config

Generated on first launch at `UserData\MelonPreferences.cfg`, category `TrophyAPS`.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | `true` | — | Master switch |
| `Detection Radius` | `30` | 5–30 | Horizontal semi-axis in meters |
| `Detection Height` | `12` | 5–30 | Vertical semi-axis in meters |
| `Rounds per Launcher` | `4` | 1–8 | Rounds per launcher |
| `Friendly Fire Filter` | `true` | — | Let friendly rounds pass |
| `Show Range Visual` | `false` | — | Debug outline per quadrant |

`Detection Radius` sets how far out a threat is engaged, all 360°.
`Detection Height` sets how high something still counts as incoming: a round
crossing 20 m overhead is ignored, a diving top-attack round is still caught.

## Build

Open `TrophyAPS.sln` in Visual Studio, build in **Release**.
Requires the .NET Framework 4.7.2 Developer Pack and the `GHPC` path in
`TrophyAPS.csproj` pointing at your install.

## Package

```powershell
.\package.ps1 -BundlePath "D:\Steam\steamapps\common\Gunner HEAT PC\Bin\Mods\TrophyAPS\trophy"
```

Outputs `dist\TrophyAPS-v<version>.zip` containing `TrophyAPS.dll` and
`TrophyAPS\trophy`.
