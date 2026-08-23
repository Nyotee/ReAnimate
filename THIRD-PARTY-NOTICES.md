# Credits

ReAnimate ships no third party libraries. It runs on Dalamud and uses what Dalamud
already provides to every plugin: FFXIVClientStructs for the game's structures and
Havok bindings, Lumina for game files and sheets, Newtonsoft.Json for the mod json.

- [Dalamud](https://github.com/goatcorp/Dalamud) by goatcorp
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) by aers and contributors
- [Lumina](https://github.com/NotAdam/Lumina) by NotAdam
- [Penumbra](https://github.com/xivdev/Penumbra) by the xivdev team. Saves and swaps land
  there as regular mods, over its IPC.
- [VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor) by 0ceal0t. Reading its source
  is how driving the game's own Havok runtime from inside the process was figured out.
- [Dancy](https://github.com/lnjanos/Dancy) by kcuY. The animation swap tab is ReAnimate's
  take on the one-button emote rebinding Dancy came up with.
- Pose2Idle, the original standalone tool that showed poses could become idles. The
  Idle2Pose tab name is a nod to it.
- Brio and Ktisis, the posing tools this pairs with.

Animation data is produced by the Havok runtime built into the game client. No Havok SDK
is included or needed.
