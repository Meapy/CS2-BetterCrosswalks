# Better Crosswalks — UI module

Frontend half of the mod. Adds the crossing button to the row of mod buttons at the top **left** of
the screen, and the small panel that appears beside it while the tool is running.

It is not optional. The toolbar button is the only way to open the tool, so a build with a stale or
missing UI bundle has no tool at all — and it loads without complaining.

The C# side (`CrosswalkToolUISystem`) publishes the tool's state as bindings in the
`crosswalkWidth` group and takes the buttons' clicks as triggers. This module renders them.

## First-time setup

`mod.json` and `src/` are already written. The rest of the boilerplate — build config, type
definitions and dependencies — is versioned against your installed game, so copy it from the
toolchain rather than checking copies into the repo.

`CSII_TOOLPATH` points at the *installed* toolchain cache under `LocalLow`, which does not carry
the UI scaffolder. The template lives in the game folder instead:

```powershell
cd <your clone>\ui

# Wherever Cities: Skylines II is installed — the toolchain template ships inside the game folder.
$game = "C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II"
$tpl = "$game\Cities2_Data\Content\Game\.ModdingToolchain\npx-create-csii-ui-mod\template"
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
pressed, `pointing at junction N` on first hover, and `selected junction N with M crossings` on
click.

## Labels that contain a value

Build them as one template literal, never as JSX text with `{expressions}` in it:

```tsx
{`Same for all ${crossingCount} crossings here`}     // right
Same for all {crossingCount} crossings here          // wrong
```

The game lays its interface out with flexbox, and the second form arrives as three text nodes, which
become three flex items. It shipped as `Same for all 7crossings here`, and a heading written the same
way broke across three lines as `Back to global (` / `150` / `%)`.
