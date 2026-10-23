# Armor Pack Splitter

A standalone Windows app for extracting selected Skyrim Special Edition armor sets into an installable ZIP containing an ESL-flagged ESP and its assets. The original armor pack is not a master of the exported plugin.

## Download and install

Download **ArmorPackSplitter-1.0.0-win-x64.zip** from the [latest release](https://github.com/Ank164/armor-pack-splitter/releases/latest), extract it into its own folder, and run `ArmorPackSplitter.exe`. Windows x64 is required. The .NET runtime is included.

For MO2, add the extracted EXE to its executable list, set **Start in** to the extracted application folder, and launch it through MO2. Keep the app outside any individual armor mod folder.

## Use

1. Run `ArmorPackSplitter.exe`. Through MO2, choose the **original armor pack ESP** from the plugin dropdown. Outside MO2, use **Browse for plugin**.
2. Choose your Skyrim **Data folder**, containing `Skyrim.esm`, if it was not detected automatically.
3. Pick a set in the dropdown. Review the checked pieces. Set detection uses names and is a suggestion, not an authoritative set definition. Enable **Show every armor piece** to add or remove other pieces; filtering does not change their check states. Changing the set dropdown starts a new selection.
4. Leave **Use current MO2 profile assets** enabled to resolve authored assets from this portable MO2 installation. Generated ParallaxGen, DynDOLOD, Reqtificator, Synthesis, TexGen, xLODGen and NPC Plugin Chooser outputs identified by names or markers are excluded. This option does not change the active profile or enable anything. It supports portable MO2 instances with `mods` and `profiles` beneath `ModOrganizer.ini`; other layouts can use explicit folders or MO2's virtual Data folder.
5. Optional additional asset folders take priority over MO2 and the source pack. Enter one mod root per line, highest priority first. Choose roots that contain `meshes`, `textures`, and similar Data folders, not the `meshes` folder itself.
6. Dependencies are checked automatically after selecting a set or changing its pieces or asset folders. **Check dependencies** also refreshes them manually. Review required masters, warnings, and copied files. Each file now shows its role or referring file and its source provider.
7. Choose a ZIP output folder and a new mod name, then **Export ZIP**. Choose the ZIP filename in the save dialog. Existing files are replaced only after you accept the save dialog's overwrite prompt.
8. Install the ZIP with MO2's **Install a new mod from an archive** button and enable the mod and its ESL-flagged ESP. You can disable the original armor pack. Retain the listed external masters and runtime requirements. Use a new save for testing; exported FormIDs are different from the original items.

To run through MO2, add the EXE as an executable and set its Start in folder to the app directory. You can pass a full plugin path as its first argument, or drag a plugin onto the app window.

## What is exported

- Checked ARMO records, source-plugin dependency closure (including armor addons, custom texture sets, enchantments, and other linked records), and optional recipes producing the exported equipment.
- Shared dependencies are cloned once. All copied records receive new FormIDs, and references between them are remapped.
- Meshes, embedded shader textures, referenced physics XML files, available weight variants and morph files; loose files and supported BSA files are packaged at their Data-relative paths inside the ZIP.
- A manifest records original and exported FormIDs, asset providers, SHA-256 hashes, required masters, and warnings.
- The ESP is reopened after writing and checked for its ESL flag, compact IDs, original master, record counts, and dangling internal references. ZIP entry paths and lengths are checked against the staged export before the finished ZIP is moved into place.

## Limits

- Requires Skyrim Special Edition format. Output is an ESL-flagged ESP, with a maximum of 2,048 new records per ZIP.
- It removes dependency on the opened pack. Other plugins referenced by the selected records remain explicit required masters.
- Matching BodySlide projects are included under CalienteTools/BodySlide, with their source meshes, slider data, shared shape dependencies, and filtered groups. Projects are matched by their actual output mesh paths. Unrelated projects and backup folders are excluded. Install BodySlide and the required body system separately.
- It does not bundle a body system, skeleton, physics engine, or SKSE DLLs. Shared texture files actually referenced by the exported meshes can be copied; the preview lists all files.
- Missing required assets and unverified mesh, XML, or script dependencies require review. **Allow incomplete export (missing files / scripts)** lets you deliberately export available files; the manifest is marked incomplete and the README lists unresolved issues. This does not invent missing files or guarantee that incomplete equipment will work.
- Unsafe paths and unsupported world/quest/NPC record dependencies still block export. Dynamic Papyrus dependencies cannot be safely discovered by following record links.
- Physics XML with malformed comments is scanned with those comments ignored. Chinese and other Unicode text is preserved, and the copied XML stays byte-for-byte unchanged. Actual XML data problems still require review.
- Dark mode is enabled by default; the toggle in the top-right remembers your preference.
- Missing `.tri` morph files are warnings, because an absent optional morph need not prevent the armor from equipping. Available morph files are copied. The tool does not generate missing morphs.
- Meshes are inspected with NiflySharp and copied byte-for-byte. No geometry or texture path rewriting is performed.
- Asset lookup uses explicit folders first, then the current portable MO2 profile's authored loose files in mod priority, then the source and Data folders. Archives are read from explicit folders, base Skyrim archives, source masters, and other active plugins. Archive filenames follow the paired plugin's name; later plugins' archives take priority, while duplicate archive filenames resolve by authored mod priority. This does not simulate every INI-driven archive loading customization; inspect asset providers in the manifest when using unusual archive setups.
- Generated outputs with arbitrary names and no identifying markers may not be detectable. Open original source plugins and supply authored asset folders.
- The source pack's showroom, merchants, containers, distribution lists, and other world content are omitted. The exported recipes or an item spawning tool provide access to the items.

## Validation performed

Tested against `[SunJeong] Loverslab Collection 3.3`: 359 armor records grouped into 66 suggested sets. The Red Moon test exported 12 armor pieces, 31 total records and 109 files using the active MO2 profile, with no reference to the original ESP. This is data and asset validation; an in-game equip test has not been performed.

Regression checks cover shared dependency deduplication, recipe inclusion and remapping, new record ownership, source master removal, unrelated-item exclusion, existing-output refusal, path traversal rejection, and missing required mesh blocking.

Version 0.3.0 checks also cover the exact 2,048-record ESL boundary, overflow refusal, installable ZIP layout and extraction, asset byte preservation, and always-visible error/optional sections. A Babydoll ZIP was exported from the actual pack with 6 armor pieces, 18 records, and 61 assets.

## Build and license

The split-steel chestplate icon was generated with the built-in image-generation tool. Its original transparent artwork, Windows icon, and exact generation prompt are in `Assets/ArmorPackSplitter.png`, `Assets/ArmorPackSplitter.ico`, and `Assets/Icon-Prompt.txt`.

Requires the .NET 10 SDK. Published Windows x64 builds include the runtime.

```powershell
dotnet build -c Release
dotnet run -c Release -- --self-test
./Build-Release.ps1
```

Licensed GPL-3.0-only. Dependencies retain their licenses; see `THIRD-PARTY-NOTICES.md` and `licenses`. Include the matching source and dependency notices when redistributing the executable. Armor assets retain their original authors' permissions.
