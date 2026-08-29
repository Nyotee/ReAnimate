# ReAnimate

ReAnimate is a Dalamud plugin that lets you turn your character's current pose into an
idle pose (including combat/weapon poses) with a single click. It retains natural
breathing motion and doesn't feel static.

It doesn't matter if you're in gpose or not or which tool helped you pose, ReAnimate will
just snap it.

It also offers a secondary tool: animation swap with a button. Pick a Penumbra mod from
the list that has animations, pick a new emote and hit "swap". That simple!

## Install

Add this repo to Dalamud (`/xlsettings` > Experimental > Custom Plugin Repositories):

```
https://plogon.nyaughty.com
```

Then install ReAnimate from `/xlplugins`. Penumbra is required, the saves land there as
regular mods you can toggle or delete like any other.

The `repo.json` in this repo exists so plugin indexers can find my plugins (ReAnimate and
Onion). The link above is the actual plugin repository and is always current.

## Use

- `/reanimate` opens the window. Idle2Pose tab: pick race, pose type and slot (they
  default to what you're doing on screen), hit save. Animation Swap tab: pick a mod, pick
  an emote, hit swap.
- `/reanimate save` saves straight into your current idle, no window.
- `/reanimate idle` / `sitting` / `groundsit` / `doze` / `weapon`, optionally followed by a
  slot number, saves into that pose type.

## Credits

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

MIT. See [LICENSE](LICENSE).
