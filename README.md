# PassiveSkillTreeNotes

An ExileCore plugin for Path of Exile 1 that displays color-formatted Path of Building notes alongside [PassiveSkillTreePlanter](https://github.com/exApiTools/PassiveSkillTreePlanter).

## Features

- Opens only while the character passive tree is visible.
- Follows the tree currently loaded by PassiveSkillTreePlanter.
- Fetches notes directly from `pobb.in`.
- Supports multiple PoB sources inside the same planter build.
- Matches a loaded tree to its PoB by decoded passive-node IDs.
- Renders PoB `^xRRGGBB` colors and `^7` resets.
- Provides a movable, resizable, scrollable, and collapsible notes window.
- Stores source mappings and fetched notes in its own config without modifying PassiveSkillTreePlanter builds.

## Usage

1. Create or select a build in PassiveSkillTreePlanter.
2. Import one or more trees from a `pobb.in` URL.
3. Open the in-game character passive tree.
4. Expand **PoB source** in the notes window and fetch the URL once if it was not detected automatically.
5. Use the planter's tree load buttons. The displayed notes switch to the PoB that owns the loaded tree.

The companion cache is stored under:

```text
config/PassiveSkillTreeNotes/build-notes.json
```
