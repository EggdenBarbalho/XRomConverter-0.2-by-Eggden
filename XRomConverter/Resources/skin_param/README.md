# Corrected XRom skin parameters

This catalog is the corrected reference output supplied for the Project DIVA X
base game and all supported DLCs. Mikudayo (`CharacterType.EXT`) is
intentionally not included.

At runtime, XRomConverter copies a matching corrected file to the generated
mod. When no matching file exists, it retains the normal generated
skin-parameter output as a compatibility fallback.

The project embeds the text files in this directory into
`XRomConverter.dll`. They are not distributed as a separate catalog:
the converter writes each matching parameter automatically while converting
its module.
