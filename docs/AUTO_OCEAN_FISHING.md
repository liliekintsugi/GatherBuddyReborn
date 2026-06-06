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

### PR 7a — `BaitAdvisor` — commit `477d31f`
**Quoi.** Module read-only `AutoGather/OceanFishing/BaitAdvisor.cs`.

- `Scan(desiredPerFish)` : parcourt `AutoGatherListsManager.ActiveItems + FallbackItems`,
  garde les `Fish` non-spearfish, groupe par `Fish.InitialBait`, snapshot inventaire
  via `InventoryManager` (Inventory1..4). Retourne `MissingBait(BaitItemId,
  BaitName, IconId, HaveQty, DesiredQty, FishNames)` triés (manquants d'abord).
- `QueueRestock(bait)` / `QueueAllMissing(baits)` : push dans la buy list
  active via `VendorBuyListManager.TryIncrementTarget(itemId, need)` — la
  couche vendor résout NPC / shop / prix toute seule.

**Pourquoi.** Surface UX pour voir d'un coup d'œil quels appâts manquent
sur **n'importe quelle gather list pêche** (pas que ocean). PR 7a n'auto-pas
les achats — c'est la PR 7b qui automatise.

**Wiring.**
- `GatherBuddy.cs` : nouveau static `AutoGatherLists` (raccourci public sur
  l'instance interne `AutoGatherListsManager`).
- UI : section dépliable « Bait Advisor » sur l'onglet Ocean Fishing avec
  table 5 colonnes (Bait / Have / Desired / # Fish / Action), tooltip listant
  les poissons concernés au survol, bouton « Refresh » et « Queue ALL missing ».

**Caveat.** Le `DesiredQty` est juste `fishCount * DesiredQtyPerFish` (heuristique).
Pas de prise en compte des chains mooch / des baits intermédiaires.

---

### PR 7b — `BaitGuard` — commit `5715d61`
**Quoi.** Service tick périodique `AutoGather/OceanFishing/BaitGuard.cs` qui
appelle `BaitAdvisor.Scan` toutes les `CheckInterval` secondes (default 15s),
queue les baits sous `TriggerBelowFraction * DesiredQty` (default 50%),
et appelle `VendorBuyListManager.Start()` si la pipeline vendor n'est pas
déjà en cours.

**Pourquoi.** Pendant qu'AutoGather pêche, on ne veut pas tomber à sec. Le
guard fait le check en background et déclenche le restock automatiquement
quand un seuil est atteint, sans toucher la machine à états d'AutoGather
(pas de pause explicite — pendant que le shop est ouvert, AutoGather idle
naturellement, puis reprend).

**Wiring.** Instancié dans `GatherBuddy.cs`, tique dans `Update()`. UI section
dépliable avec toggle, `OnlyWhenAutoGatherEnabled` (default ON),
`TriggerBelowFraction` slider, `DesiredQtyPerFish`, `CheckInterval`, et
compteurs (last/total queued).

**Caveat.** Si plusieurs presets fish utilisent le même bait dans la même
liste, on multiplie inutilement le `DesiredQty`. À déduplicater par bait
plutôt que par fish dans une prochaine itération.

---

### PR 7c — Auto-toggle AutoHook on trip — commit `70af294`
**Quoi.** Modifs dans `AutoOceanFishing.cs`.

- `OnEnterTrip` : `_savedPluginState = AutoHook.GetPluginState()`,
  `_savedAutoStart = AutoHook.GetAutoStartFishing()`, puis `SetPluginState(true)`
  + `SetAutoStartFishing(true)`.
- `OnLeaveTrip` : restaure les deux valeurs sauvegardées.
- Toggle UI `ManageAutoHookState` (default ON) pour désactiver le comportement.

**Pourquoi.** Avant PR 7c, AutoOceanFishing switchait les presets mais
n'activait jamais AutoHook lui-même — donc rien ne pêchait si le joueur
n'avait pas pré-activé AutoHook manuellement. Avec PR 7c le pipeline est
end-to-end : embark → preset switch → AutoHook ON → casts → preset re-switch
sur spectral → desembark → AutoHook restauré.

**Caveat.** Si AutoHook est rechargé entre `OnEnterTrip` et `OnLeaveTrip`,
les états sauvegardés deviennent stale (et `RestoreAutoHook` early-out si
`!AutoHook.Enabled`). Acceptable pour v1.

---

### PR 8 — `LevelingMode` — commit `1e26cae` (+ fix `257e0f8`)
**Quoi.** Module `AutoGather/OceanFishing/LevelingMode.cs` qui auto-génère
une `AutoGatherList` ciblant le `FishingSpot` au plus haut niveau dans
`[playerLvl + LevelMin, playerLvl + LevelMax]` (défaut `[-3, +2]`), exclut
spearfishing, préfère les spots avec aetheryte.

- `Retarget()` : sélection du spot + suppression de l'ancienne liste +
  création d'une nouvelle list nommée `GBR Leveling Lv{N} {SpotName}` avec
  tous les `Fish` du spot, `Enabled = true`, push via
  `AutoGatherListsManager.AddList`. Force `GatherBuddy.AutoGather.Enabled = true`.
- `Tick()` : si territoire == 900 OU `EmbarkController.State ≠ Idle/Boarded`,
  désactive la liste générée (`SetActiveItems()` refresh) — handoff au
  pipeline ocean. Sinon retargete périodiquement (default 5 min).
- `Cleanup()` au toggle-off supprime la liste.

**Pourquoi.** Combler les ~24 min réelles entre deux trips ocean. Le joueur
ne reste pas idle au quai : il level sa Fisher pendant qu'il attend.

**Wiring.** Static dans `GatherBuddy.cs`, tick dans `Update()`. UI dépliable :
toggle + sliders LevelMin/LevelMax/RetargetEvery, état live, bouton « Force
retarget now ». Modification de `FishingSpot.cs` (GameData) pour exposer
`GatheringLevel` lu de la sheet Lumina à la construction.

**Fix CI.** Premier build a cassé : `FishingSpot.Name` est `string`, j'avais
écrit `.English` en supposant un `MultiString`. Commit de fix : `257e0f8`.

**Caveat.** Aucune prise en compte des unlocks (Big Fish, achievements,
zones débloquées). Le joueur doit s'assurer que le spot choisi est
accessible. À durcir si jamais on lit la sheet `Achievement` / quest flags.

---

## Architecture des fichiers ajoutés

```
GatherBuddy/AutoGather/OceanFishing/
├── SpectralDetector.cs      # PR 1
├── OceanPresetCache.cs      # PR 2
├── OceanCommands.cs         # PR 2 (/gbocean handler, partial GatherBuddy)
├── AutoOceanFishing.cs      # PR 3 (+ AutoHook toggle PR 7c)
├── EmbarkController.cs      # PR 4 (+ Restocking state PR 6)
├── BaitRestock.cs           # PR 6
├── BaitAdvisor.cs           # PR 7a
├── BaitGuard.cs             # PR 7b
└── LevelingMode.cs          # PR 8

GatherBuddy.GameData/Classes/
└── FishingSpot.cs           # PR 8 — expose GatheringLevel

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
  ├─ AutoGatherLists        (alias public statique sur AutoGatherListsManager — PR 7a)
  ├─ SpectralDetector       (PR 1)
  ├─ OceanPresetCache       (PR 2)
  ├─ AutoOceanFishing       (PR 3 + PR 7c, ref SpectralDetector + OceanPresetCache + FishRecorder.Parser)
  ├─ EmbarkController       (PR 4, owns BaitRestock from PR 6)
  ├─ BaitGuard              (PR 7b)
  └─ LevelingMode           (PR 8)

Update tick (chaque frame):
  ├─ SpectralDetector.Tick()      # mise à jour weather id + event
  ├─ AutoOceanFishing.Tick()      # transitions trip / handoff + AutoHook on/off
  ├─ EmbarkController.Tick()      # state machine embark (Restocking inclus)
  ├─ BaitGuard.Tick()             # restock auto pendant AutoGather
  └─ LevelingMode.Tick()          # auto-fish entre les trips

Dispose:
  └─ ordre inverse, EmbarkController.Dispose() appelle vnavmesh.Path.Stop()
```

---

## Surface utilisateur (onglet `Ocean Fishing`)

1. **SpectralDetector debug** : territory, IDs trackés, weather courante,
   spectral on/off.
2. **AutoOceanFishing** : toggle + zone préférée + route/segment/spectral
   actuels + dernier preset appliqué + `ManageAutoHookState` (PR 7c).
3. **EmbarkController** : toggle + état + last error + champs runtime
   (FerryStandPosition Vector3, FerrySkipperDataId, FerryTerritoryId,
   SelectStringBoardIndex).
4. **BaitRestock** : toggle + Guid buy list + snapshot inventaire (par bait,
   `have / target` avec couleur si < low threshold).
5. **Bait Advisor (PR 7a)** : section dépliable, table missing baits
   (Bait / Have / Desired / # Fish / Action), boutons « Refresh » et
   « Queue ALL missing ».
6. **Bait Guard (PR 7b)** : toggle, `OnlyWhenAutoGatherEnabled`,
   `TriggerBelowFraction`, `DesiredQtyPerFish`, `CheckInterval`, compteurs.
7. **Leveling Mode (PR 8)** : toggle, sliders LevelMin/LevelMax/RetargetEvery,
   spot/list courants, bouton « Force retarget now ».
8. **Routes prochaines** Aldenard + Othard avec bouton « Apply seg 0
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
| 2 | Sélection naïve fish par spot dans `OceanPresetCache` (pas de priorité points/blue/intuition) | Config `OceanPriority` à ajouter |
| 3 | Pas de fallback timer si aucun cast (segment stuck à 0) | Timer 7 min en backup dans `AutoOceanFishing` |
| 4 | Coords ferry / data id NPC **non vérifiés** en jeu | Test live + correction depuis l'UI au premier run |
| 5 | `BuyListId` + autres params non persistés (PR 6/7/8) | Brancher sur `Configuration.cs` |
| 6 | Pas de planification "embark dans X minutes" | Time-gate vs `OceanUptime.NextOceanRoute().StartTime` |
| 7 | Pas de gestion explicite des poissons "blue fish" / intuition | Combinable avec #2 |
| 8 | Pas de retour automatique en ville post-trip | Hook `OnLeaveTrip` pour replanifier le prochain embark |
| 9 | `BaitGuard.DesiredQty` multiplié par fish (pas dedup par bait) | Dédoublonnage dans `BaitAdvisor.Scan` |
| 10 | `LevelingMode` ne tient pas compte des unlocks (Big Fish / achievements / zones) | Lecture sheet `Achievement` / quest flags si besoin |
| 11 | `BaitAdvisor.QueueAllMissing` ne déclenche pas auto `Start()` | Volontaire (PR 7a = read-only) — PR 7b s'en charge déjà |

---

## Prochaines PR candidates

- **PR 9** : Persistance config (`OceanFishingConfig` dans `Configuration`),
  inclut `BuyListId`, `FerryStandPosition`, `FerrySkipperDataId`, `PreferredArea`,
  seuils restock, settings BaitGuard / LevelingMode.
- **PR 10** : Priorité poissons (Points / Blue Fish / Intuition / Custom),
  refactor de `OceanPresetCache.ResolveFishForSpot`.
- **PR 11** : Planification embark (déclencher quand le prochain départ
  est dans `LeadTimeMinutes`).
- **PR 12** : Boucle multi-trip continue (post-arrivée → reset state →
  attendre prochain départ, handoff LevelingMode entre).
- **PR 13** : Détection unlocks pour `LevelingMode` (sheet Achievement).

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
   presets switchent au passage spectral. Avec PR 7c, AutoHook s'active
   automatiquement au passage en territoire 900.
6. Activer **BaitAdvisor** (PR 7a) — clique Refresh, vérifier que les baits
   manquants apparaissent. Test « Add » → vérifier que la buy list Vulcan se
   remplit.
7. Activer **BaitGuard** (PR 7b) — laisser tourner AutoGather sur une fish
   list. Quand un bait passe sous le seuil, le vendor list doit démarrer
   automatiquement.
8. Activer **LevelingMode** (PR 8) — vérifier qu'un spot est sélectionné
   correspondant à ton niveau Fisher, qu'une liste `GBR Leveling Lv...` est
   créée, et que AutoGather la pêche. Quand un trip ocean démarre, la
   liste doit se désactiver automatiquement.
9. **Pipeline complet** : activer Embark + BaitRestock + AutoOceanFishing +
   BaitGuard + LevelingMode + PR 7c. Le joueur ne devrait plus rien toucher
   en jeu : level entre les trips, restock auto, embark auto à l'horaire,
   pêche auto sur le bateau, retour, recommencer.
