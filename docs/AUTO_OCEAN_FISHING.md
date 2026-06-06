# Auto Ocean Fishing — suivi de fork

Fork du plugin Dalamud **GatherBuddyReborn** pour ajouter l'automatisation
complète de l'Ocean Fishing FFXIV, distribué via le `repo.json` de XIVPath.

---

## Vue d'ensemble

- **Fork** : https://github.com/liliekintsugi/GatherBuddyReborn
  (parent : `FFXIV-CombatReborn/GatherBuddyReborn`)
- **Branche de travail** : `feature/auto-ocean-fishing`
- **Distribution** : prerelease `nightly-feature-auto-ocean-fishing` rebuildée
  à chaque push (workflow `.github/workflows/fork-release.yaml`)
- **Repo Dalamud (DIP)** : `repo.json` à la racine, à pointer depuis XIVPath
  via l'URL raw GitHub
- **CLAUDE.md mémoire** : voir `xivpath-project.md` / `xivpath-product-vision.md`
  pour le contexte XIVPath

---

## Pourquoi ce fork

GBR couvre déjà la pêche normale et le spearfishing, sait piloter AutoHook
via IPC, et a toutes les données ocean fishing dans `GatherBuddy.GameData`
(`OceanTimeline`, `OceanRoute`, `OceanSpecies`, `OceanTime`,
`Plugin/OceanUptime.cs`). Ce qui manquait :

1. Un module qui orchestre la boucle de trip (3 segments × normal/spectral).
2. La détection robuste du mode spectral.
3. L'embarquement automatique.
4. Le restock de bait avant départ.
5. Une surface IPC permettant à XIVPath de tout afficher / piloter sans
   réimplémenter `OceanTimeline` côté compagnon.

---

## Stack technique réutilisée

| Brique                          | Source dans GBR                               | Usage          |
| ------------------------------- | --------------------------------------------- | -------------- |
| AutoHook IPC                    | `Plugin/IpcSubscribers.cs` (`AutoHook`)       | Switch preset  |
| vnavmesh IPC                    | `Plugin/IpcSubscribers.cs` (`VNavmesh.Nav/Path`) | Pathfind ferry |
| Preset builder AutoHook         | `AutoHookIntegration/AutoHookPresetBuilder.cs` | Build presets  |
| `WeatherManager` (per-territory) | `SeFunctions/EnhancedCurrentWeather.cs`       | Détection spectral |
| OceanTimeline / OceanRoute      | `GatherBuddy.GameData/`                       | Données routes |
| OceanUptime                     | `Plugin/OceanUptime.cs`                       | Route en cours |
| FishingParser events            | `FishTimer/Parser/FishingParser.cs`           | Détection segment |
| AddonMaster                     | `Automation/AddonMaster.cs`                   | SelectString NPC |
| VendorBuyListManager / Purchase | `Vulcan/Vendors/`                             | Restock bait   |

**Aucune dépendance externe ajoutée.**

---

## CI & distribution

### `.github/workflows/fork-release.yaml`

Trigger : push `main` / `feature/**`, ou `workflow_dispatch`.
Étapes :

1. Calcule `assemblyVersion = 0.0.<run_number>.0` et `tag = nightly-<branch>`.
2. Setup .NET 10 + Rust + télécharge Dalamud `latest.zip`.
3. `dotnet build -c Release Gatherbuddy/Gatherbuddy.csproj` avec
   `AssemblyVersion`/`FileVersion`/`InformationalVersion` injectés.
4. Build `raphael-cli` depuis `KonaeAkira/raphael-rs` (dépendance crafting GBR).
5. Zippe `./build/*` → `GatherBuddyReborn.zip`.
6. Génère `GatherBuddyReborn.manifest.json` à partir du JSON existant
   (`GatherBuddy/GatherBuddyReborn.json`) en y injectant `AssemblyVersion`,
   `DownloadLinkInstall/Update/Testing` et `RepoUrl`.
7. Publie une **prerelease** taguée `nightly-<branch>` avec les deux assets.

Conséquence : à chaque commit, le ZIP installable est dispo à
`https://github.com/liliekintsugi/GatherBuddyReborn/releases/download/nightly-feature-auto-ocean-fishing/GatherBuddyReborn.zip`
et un manifest cohérent à
`…/GatherBuddyReborn.manifest.json`.

### `repo.json` (racine fork)

Tableau d'une entrée Dalamud Plugin Repository pointant vers la release ci-dessus.
Pour consommation XIVPath, URL raw :

```
https://raw.githubusercontent.com/liliekintsugi/GatherBuddyReborn/main/repo.json
```

(En attendant le merge `feature/auto-ocean-fishing → main`, utiliser l'URL
de la branche.)

---

## Découpage en PR (toutes sur `feature/auto-ocean-fishing`)

### PR 1 — `SpectralDetector` + debug tab — commit `58f65ae`
**Quoi.** `GatherBuddy/AutoGather/OceanFishing/SpectralDetector.cs` scanne
la sheet `Weather` à l'init, garde un `HashSet<byte>` des `Id` dont le nom
contient « Spectral », et tique chaque frame :
- hors territoire 900 → état idle, signal de sortie si on était spectral
- dans territoire 900 → lit `EnhancedCurrentWeather.GetCurrentWeatherId()`,
  marque `IsSpectralActive` et fire `SpectralChanged(bool)` sur delta.

**Pourquoi.** Le mode spectral est encodé en jeu comme une météo individuelle
(per-territory weather override) sur la zone 900. C'est instantané, en mémoire,
zéro OCR, zéro hook.

**Wiring.** Instancié dans `GatherBuddy.cs` (`SpectralDetector` static),
tick dans `Update()`, dispose au unload. Onglet ImGui debug
`Interface.OceanFishingTab.cs` qui affiche les IDs trackés + état live.

**Caveat.** Le filtre `Name.Contains("Spectral")` doit capturer toutes les
routes (Aldenard + Othard). Si une route a un weather nommé autrement, fallback
à prévoir : extraction depuis `IKDRouteTable` qui référence directement
les rows Weather.

---

### PR 2 — `OceanPresetCache` + `/gbocean` — commit `b9d22f5`
**Quoi.**
- `OceanPresetCache.cs` : `Dictionary<(routeId,order,spectral), presetName>`.
  Sur `GetOrBuild`, résout le `FishingSpot` via `OceanRoute.GetSpot(order, spectral)`,
  liste les `Fish` ayant ce spot, génère un nom `GBR_Ocean_<route>_<order>_<S|N>`
  et délègue à `AutoHookService.ExportPresetToAutoHook(...)`.
- `Apply(...)` appelle `AutoHook.SetPreset?.Invoke(name)` via IPC.
- Commande slash `/gbocean preset [aldenard|othard] [0|1|2] [normal|spectral]`,
  `/gbocean clear`, `/gbocean status`.

**Pourquoi.** Brique de test manuel sans embarquer. Permet de valider que
la chaîne `(spot → fish list → preset AutoHook → IPC apply)` fonctionne
avant d'écrire l'auto-orchestration.

**Caveat.** La sélection des poissons est naïve (tous les poissons mappés
au spot, ordonnés par `ItemId`). Pas de priorisation par points / blue
fish / intuition. Reste pour une PR ultérieure (extension config).

---

### PR 3 — `AutoOceanFishing` trip loop — commit `89fd38e`
**Quoi.** `AutoOceanFishing.cs` : machine à états trip-only.
Au passage territoire ≠ 900 → 900 :
- Résout la `CurrentRoute` via `OceanUptime.NextOceanRoute(PreferredArea, ServerTime)`.
- Démarre `CurrentSegment = 0`, `CurrentSpectral = SpectralDetector.IsSpectralActive`.
- Apply preset initial.

Trois déclencheurs de re-apply :
- Entrée trip (segment 0).
- `SpectralDetector.SpectralChanged` → re-apply pour le nouveau spectral.
- `FishingParser.BeganFishing(FishingSpot?)` → matche le spot du cast
  contre les 6 combinaisons `(order, spectral)` de la route, ajuste
  `CurrentSegment`/`CurrentSpectral` si différent, re-apply.

Idempotent via `LastAppliedPreset`.

**Pourquoi.** Le segment du trip change toutes les ~7 min réelles. Détection
robuste = écouter le premier cast post-changement, qui identifie le nouveau
spot, donc le nouveau segment. Pas de timing fragile.

**Wiring.** Construit avec `(SpectralDetector, OceanPresetCache, FishRecorder.Parser)`,
tick dans `GatherBuddy.Update()`. Toggle UI + combo zone préférée. Commande
`/gbocean auto on|off`.

**Caveat.** Si le joueur ne pêche jamais (just AFK), on reste sur le segment 0.
Pour PR ultérieure : fallback timer ~7 min si pas de cast.

---

### PR 4 — `EmbarkController` — commit `f29c6a2` (+ fix `f28b9ca`)
**Quoi.** État machine `Idle → Restocking → Pathing → Moving → AtNpc →
Interacting → SelectingRoute → Boarded`.

- `Idle` : si territoire = `FerryTerritoryId` (default 129, Limsa Lower Decks),
  lance `vnavmesh.Nav.Pathfind(player.Position, FerryStandPosition, false)`.
- `Pathing` : attend la fin du `Task<List<Vector3>>`, puis `Path.MoveTo(waypoints, false)`.
- `Moving` : poll `Path.IsRunning()` et distance au stand point ; quand < 3y,
  `Path.Stop()` → `AtNpc`.
- `AtNpc` : cherche dans `Dalamud.Objects` un object avec `DataId == FerrySkipperDataId`
  (default `1027847`) à < 10y du stand point. `TargetSystem->OpenObjectInteraction(...)`.
- `Interacting` : poll `TryGetAddonByName<AddonSelectString>` ; timeout 5s.
- `SelectingRoute` : `AddonMaster.SelectString(addon).Entries[SelectStringBoardIndex].Select()`
  (default index 0).
- Le passage territoire 900 force `Boarded` → handoff `AutoOceanFishing`.

**Pourquoi.** Le menu et l'NPC sont identifiables uniquement par index + dataId,
donc tout est configurable à chaud depuis l'onglet pour absorber les
changements de patch.

**Caveat majeur.** Les défauts (`FerryStandPosition = (-129.7, 18.0, 39.9)`,
`FerrySkipperDataId = 1027847`, `SelectStringBoardIndex = 0`) sont **non vérifiés
en jeu**. Premier test à blanc : aller à Limsa Lower Decks, ouvrir l'onglet,
ajuster les coords / data id si besoin (le data id se lit via `/xltarget`).

**Fix CI.** Le premier build a cassé sur `Dalamud.ClientState.LocalPlayer`
(n'existe pas dans cette version de l'API Dalamud — il faut `Dalamud.Objects.LocalPlayer`)
et un shadowing de variable `territory` dans l'onglet UI. Commit de fix : `f28b9ca`.

---

### PR 5 — `GatherBuddyIpc` v3 — commit `f29c6a2` (groupé avec PR 4)
**Quoi.** Bump `IpcVersion` de 2 à 3. Nouveaux endpoints :

| Endpoint                  | Type            | Sémantique                                   |
| ------------------------- | --------------- | -------------------------------------------- |
| `GetCurrentOceanRouteId`  | `() → byte`     | 0 si pas de trip en cours                    |
| `GetCurrentOceanRouteName`| `() → string`   | vide si pas de trip                          |
| `GetCurrentOceanSegment`  | `() → int`      | -1 si pas de trip, sinon 0/1/2               |
| `IsSpectralActive`        | `() → bool`     |                                              |
| `IsAutoOceanEnabled`      | `() → bool`     |                                              |
| `SetAutoOceanEnabled`     | `(bool) → void` |                                              |
| `GetEmbarkState`          | `() → string`   | Enum `EmbarkState.ToString()`                |

**Pourquoi.** XIVPath peut afficher la route en cours dans le compagnon
sans dupliquer `OceanTimeline`. Le `Set*` permet de toggler depuis le compagnon.

---

### PR 6 — `BaitRestock` — commit `8aca524`
**Quoi.** Nouvel état `EmbarkState.Restocking` entre `Idle` et `Pathing`.

- `BaitRestock.Targets` : liste `(ItemId, Name, LowThreshold, RestockTarget)`
  par défaut : Krill (27590), Plump Worm (2603), Ragworm (2587),
  Versatile Lure (29714), Stonefly Nymph (29715).
- `Snapshot()` : parcourt l'inventaire (`InventoryManager.Instance()` →
  `Inventory1..4`) et retourne `(target, have)`.
- `AnyMissing()` : true si un `have < t.LowThreshold`.
- `TryStart()` : si `BuyListId` configuré, appelle
  `GatherBuddy.VendorBuyListManager.Start(buyListId)` et retourne `RestockResult`.
- `EmbarkController.TickIdle` : juste avant Pathfind, si `Restock.Enabled &&
  AnyMissing()`, transition vers `Restocking` en attendant `!Restock.IsRunning`.

**Pourquoi.** Toute la couche vendor (path, dialogue, achat) existe déjà
dans `Vulcan/Vendors/`. PR 6 reste **un orchestrateur** : l'utilisateur
construit lui-même son buy list dans l'UI Vulcan en choisissant ses NPCs
préférés, puis colle le Guid de la liste dans l'onglet Ocean Fishing.

**Caveat.** Pas de persistance config (PR 6 garde tout en mémoire).
Le Guid de buy list doit être recollé à chaque session. À déplacer dans
la Configuration GBR dans une prochaine itération.

---

## Architecture des fichiers ajoutés

```
GatherBuddy/AutoGather/OceanFishing/
├── SpectralDetector.cs      # PR 1
├── OceanPresetCache.cs      # PR 2
├── OceanCommands.cs         # PR 2 (/gbocean handler, partial GatherBuddy)
├── AutoOceanFishing.cs      # PR 3
├── EmbarkController.cs      # PR 4 (+ Restocking state PR 6)
└── BaitRestock.cs           # PR 6

GatherBuddy/Gui/
└── Interface.OceanFishingTab.cs   # tab "Ocean Fishing"

GatherBuddy/Plugin/
└── GatherBuddyIpc.cs        # v3 endpoints PR 5

Modifications:
- GatherBuddy/GatherBuddy.cs              # statics + ctor + Update + Dispose
- GatherBuddy/GatherBuddy.Commands.cs     # /gbocean registration
- GatherBuddy/Gui/Interface.cs            # DrawOceanFishingTab in tab list

Distribution:
- .github/workflows/fork-release.yaml     # nightly CI
- repo.json                               # DIP manifest
```

---

## Lifecycle de runtime

```
GatherBuddy ctor:
  ├─ FishRecorder           (existant)
  ├─ AutoGather             (existant)
  ├─ SpectralDetector       (PR 1)
  ├─ OceanPresetCache       (PR 2)
  ├─ AutoOceanFishing       (PR 3, ref SpectralDetector + OceanPresetCache + FishRecorder.Parser)
  └─ EmbarkController       (PR 4, owns BaitRestock from PR 6)

Update tick (chaque frame):
  ├─ SpectralDetector.Tick()      # mise à jour weather id + event
  ├─ AutoOceanFishing.Tick()      # transitions trip / handoff
  └─ EmbarkController.Tick()      # state machine embark

Dispose:
  └─ ordre inverse, EmbarkController.Dispose() appelle vnavmesh.Path.Stop()
```

---

## Surface utilisateur (onglet `Ocean Fishing`)

1. **SpectralDetector debug** : territory, IDs trackés, weather courante,
   spectral on/off.
2. **AutoOceanFishing** : toggle + zone préférée + route/segment/spectral
   actuels + dernier preset appliqué.
3. **EmbarkController** : toggle + état + last error + champs runtime
   (FerryStandPosition Vector3, FerrySkipperDataId, FerryTerritoryId,
   SelectStringBoardIndex).
4. **BaitRestock** : toggle + Guid buy list + snapshot inventaire (par bait,
   `have / target` avec couleur si < low threshold).
5. **Routes prochaines** Aldenard + Othard avec bouton « Apply seg 0
   (auto spectral) » et « Clear cache ».

---

## Commandes slash

| Commande                                     | Effet                                          |
| -------------------------------------------- | ---------------------------------------------- |
| `/gbocean`                                   | Affiche l'aide                                 |
| `/gbocean status`                            | État live (territory, weather, spectral, presets cachés, auto, route, segment) |
| `/gbocean preset [aldenard\|othard] [0\|1\|2] [normal\|spectral]` | Apply preset pour ce segment   |
| `/gbocean clear`                             | Vide le cache de presets                       |
| `/gbocean auto on\|off`                      | Toggle AutoOceanFishing                        |

---

## Caveats consolidés / TODO connus

| # | Sujet | Action prévue |
|---|-------|---------------|
| 1 | Mapping spectral via `Name.Contains("Spectral")` | Fallback `IKDRouteTable` si une route échappe |
| 2 | Sélection naïve fish par spot (pas de priorité points/blue/intuition) | Config `OceanPriority` à ajouter |
| 3 | Pas de fallback timer si aucun cast (segment stuck à 0) | Timer 7 min en backup dans `AutoOceanFishing` |
| 4 | Coords ferry / data id NPC **non vérifiés** en jeu | Test live + correction depuis l'UI au premier run |
| 5 | `BuyListId` + autres params non persistés | Brancher sur `Configuration.cs` |
| 6 | Pas de planification "embark dans X minutes" | Time-gate vs `OceanUptime.NextOceanRoute().StartTime` |
| 7 | Pas de gestion explicite des poissons "blue fish" / intuition | Combinable avec #2 |
| 8 | Pas de retour automatique en ville post-trip | Hook `OnLeaveTrip` pour replanifier le prochain embark |

---

## Prochaines PR candidates

- **PR 7** : Persistance config (`OceanFishingConfig` dans `Configuration`),
  inclut `BuyListId`, `FerryStandPosition`, `FerrySkipperDataId`, `PreferredArea`,
  seuils restock.
- **PR 8** : Sélection prioritaire des poissons (Points / Blue Fish /
  Intuition / Custom), refactor de `OceanPresetCache.ResolveFishForSpot`.
- **PR 9** : Planification embark (déclencher quand le prochain départ
  est dans `LeadTimeMinutes`).
- **PR 10** : Boucle multi-trip continue (post-arrivée → reset state →
  attendre prochain départ).

---

## Comment reprendre

```bash
git clone https://github.com/liliekintsugi/GatherBuddyReborn
cd GatherBuddyReborn
git checkout feature/auto-ocean-fishing
# Toutes les modifs ocean fishing : git log --oneline feature/auto-ocean-fishing ^main
```

Pour pousser une nouvelle PR sur la branche : push direct → CI build dispatché
sur push (vois `.github/workflows/fork-release.yaml`). Le tag nightly est
remplacé à chaque build (releases rolling).

Pour tester en jeu :
1. Activer le DIP `https://raw.githubusercontent.com/liliekintsugi/GatherBuddyReborn/feature/auto-ocean-fishing/repo.json` dans Dalamud.
2. Installer `GatherBuddyReborn` (fork).
3. Ouvrir onglet « Ocean Fishing ».
4. Vérifier que SpectralDetector liste des IDs > 0 (sinon, log warning).
5. Embarquer manuellement, activer AutoOceanFishing, vérifier que les
   presets switchent au passage spectral.
6. Plus tard : activer Embark + BaitRestock pour tester le pipeline complet.
