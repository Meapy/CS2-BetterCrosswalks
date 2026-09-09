# CrosswalkWidth — UI module

Frontend half of the mod. Adds the crossing-width button to the row of mod buttons at the top right
of the screen, and the small panel that appears beside it while the tool is running.

The C# side (`CrosswalkToolUISystem`) publishes the tool's state as bindings in the
`crosswalkWidth` group and takes the buttons' clicks as triggers. This module renders them.

## First-time setup

`mod.json` and `src/` are already written. The rest of the boilerplate — build config, type
definitions and dependencies — is versioned against your installed game, so copy it from the
toolchain rather than checking copies into the repo.

`CSII_TOOLPATH` points at the *installed* toolchain cache under `LocalLow`, which does not carry
the UI scaffolder. The template lives in the game folder instead:

```powershell
cd C:\Users\dkras\OneDrive\Documents\GitHub\CS2-CrosswalkWidth\ui

$tpl = "F:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain\npx-create-csii-ui-mod\template"
Copy-Item "$tpl\package.json","$tpl\tsconfig.json","$tpl\webpack.config.js" -Destination .
Copy-Item "$tpl\types","$tpl\tools" -Destination . -Recurse

npm install
```

`npm run update` afterwards refreshes those files from the toolchain after a game update.

## Building

```powershell
npm run build     # production bundle
npm run dev       # rebuild on change
```

Webpack reads `CSII_USERDATAPATH` and writes the `.mjs` and `.css` straight into
`%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Mods\CrosswalkWidth`, alongside the managed
DLL. Both halves must be present for the button to appear.

### The deploy folder is shared, and the stock build wipes it

The toolchain's `DeployWIP` target runs `<RemoveDir Directories="$(DeployDir)" />` before copying
the managed output — into the very folder webpack just wrote the UI bundle to. Building the C#
project therefore deletes the frontend, and the failure is silent.

`CrosswalkWidth.csproj` overrides `DeployWIP` to copy without the `RemoveDir`, so the two halves
coexist regardless of build order.

## Verifying

A successful build does not prove the module loaded. Check `UI.log` for:

- `[CrosswalkWidth] Toolbar button registered.`
- no `GameTopLeft append target is not available` warning
- no exception from the module

`CrosswalkWidth.log` on the managed side then shows `crossing tool running` when the button is
pressed, `pointing at junction N` on first hover, and `selected junction N at NNN%` on click.
