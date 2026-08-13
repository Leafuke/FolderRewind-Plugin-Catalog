# FolderRewind Official Plugin Catalog

This repository is the source of truth for the FolderRewind official catalog. Source entries live in `source/`; CI validates the referenced `.frplugin`, regenerates `public/catalog.v1.json`, and deploys only that generated index to GitHub Pages.

The catalog records provenance and curation status but is not a process sandbox or publisher-signature system. A release URL must identify one immutable `.frplugin` and include its exact SHA-256.

Local candidate verification:

```powershell
dotnet run --project tools/Catalog.Build -- --source source --output public/catalog.v1.json --package com.folderrewind.minerewind=../../FolderRewind-Plugin-Minecraft/artifacts/MineRewind-1.9.0.frplugin
```
