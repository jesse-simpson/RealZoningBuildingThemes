using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Step 4 of <see cref="SaveSwapSystem"/>: on load, right after the game has matched the save's
    /// zones and buildings to this session's, puts our zones back on the cells and free lots the
    /// save recorded, and resumes the level-ups that were paused in the saved copy.
    ///
    /// Each cell is only put back if it still holds the game zone the save wrote, so nothing else
    /// is overwritten. Zones and designs are matched by name: the game numbers zones afresh each
    /// session, so the numbers in the save mean nothing now. A zone that no longer exists, or a
    /// design no longer installed, leaves the city as the save had it (game zones, no level-up),
    /// which is safe.
    /// </summary>
    public partial class LoadRestoreSystem : GameSystemBase
    {
        private SaveSwapSystem m_Swap;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_ZonePrefabQuery;
        private EntityQuery m_BuildingPrefabQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Swap = World.GetOrCreateSystemManaged<SaveSwapSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ZonePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<ZoneData>(), ComponentType.ReadOnly<PrefabData>());
            m_BuildingPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<SpawnableBuildingData>(), ComponentType.ReadOnly<PrefabData>());
        }

        protected override void OnUpdate()
        {
            if (m_Swap.LoadedBlocks.Count == 0 && m_Swap.LoadedLevelUps.Count == 0)
            {
                return;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int cells = 0;
            int lots = 0;
            int changed = 0;
            int unknownZone = 0;
            int levelUps = 0;
            int levelUpsSkipped = 0;
            int missingBlocks = 0;
            try
            {
                // This session's zone types by prefab name.
                var zones = new Dictionary<string, ZoneType>();
                var zoneEntities = m_ZonePrefabQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < zoneEntities.Length; i++)
                    {
                        string name = m_PrefabSystem.GetPrefabName(zoneEntities[i]);
                        if (!string.IsNullOrEmpty(name) && !zones.ContainsKey(name))
                        {
                            zones[name] = EntityManager.GetComponentData<ZoneData>(zoneEntities[i]).m_ZoneType;
                        }
                    }
                }
                finally
                {
                    zoneEntities.Dispose();
                }

                IReadOnlyList<string> names = m_Swap.LoadedNames;
                bool TryZone(ushort key, out ZoneType zone)
                {
                    zone = ZoneType.None;
                    if (key >= names.Count)
                    {
                        return false;
                    }

                    // An empty name: the save wrote "no zone" (the zone had no usable base zone).
                    if (string.IsNullOrEmpty(names[key]))
                    {
                        return true;
                    }

                    return zones.TryGetValue(names[key], out zone) && !zone.Equals(ZoneType.None);
                }

                foreach (SaveSwapSystem.LoadedBlock b in m_Swap.LoadedBlocks)
                {
                    if (b.Block == Entity.Null || !EntityManager.Exists(b.Block) || !EntityManager.HasBuffer<Cell>(b.Block))
                    {
                        missingBlocks++;
                        continue;
                    }

                    DynamicBuffer<Cell> cellBuffer = EntityManager.GetBuffer<Cell>(b.Block);
                    foreach (var (index, oursKey, fallbackKey) in b.Cells)
                    {
                        if (index >= cellBuffer.Length)
                        {
                            changed++;
                            continue;
                        }

                        if (!TryZone(oursKey, out ZoneType ours) || ours.Equals(ZoneType.None) || !TryZone(fallbackKey, out ZoneType fallback))
                        {
                            unknownZone++;
                            continue;
                        }

                        Cell cell = cellBuffer[index];
                        if (!cell.m_Zone.Equals(fallback))
                        {
                            changed++;
                            continue;
                        }

                        cell.m_Zone = ours;
                        cellBuffer[index] = cell;
                        cells++;
                    }

                    if (b.Lots.Count > 0 && EntityManager.HasBuffer<VacantLot>(b.Block))
                    {
                        DynamicBuffer<VacantLot> lotBuffer = EntityManager.GetBuffer<VacantLot>(b.Block);
                        foreach (var (index, oursKey, fallbackKey) in b.Lots)
                        {
                            if (index < lotBuffer.Length && TryZone(oursKey, out ZoneType ours) && !ours.Equals(ZoneType.None) && TryZone(fallbackKey, out ZoneType fallback))
                            {
                                VacantLot lot = lotBuffer[index];
                                if (lot.m_Type.Equals(fallback))
                                {
                                    lot.m_Type = ours;
                                    lotBuffer[index] = lot;
                                    lots++;
                                }
                            }
                        }
                    }
                }

                if (m_Swap.LoadedLevelUps.Count > 0)
                {
                    // This session's growable designs by name.
                    var designs = new Dictionary<string, Entity>();
                    var prefabs = m_BuildingPrefabQuery.ToEntityArray(Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < prefabs.Length; i++)
                        {
                            string name = m_PrefabSystem.GetPrefabName(prefabs[i]);
                            if (!string.IsNullOrEmpty(name) && !designs.ContainsKey(name))
                            {
                                designs[name] = prefabs[i];
                            }
                        }
                    }
                    finally
                    {
                        prefabs.Dispose();
                    }

                    foreach (SaveSwapSystem.LoadedLevelUp p in m_Swap.LoadedLevelUps)
                    {
                        // Resume only if the building is still the one that was levelling up, and
                        // nothing else has started a construction on it since.
                        if (p.Building == Entity.Null || !EntityManager.Exists(p.Building) || EntityManager.HasComponent<Deleted>(p.Building) ||
                            EntityManager.HasComponent<UnderConstruction>(p.Building) || !EntityManager.HasComponent<PrefabRef>(p.Building) ||
                            m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(p.Building).m_Prefab) != p.Current ||
                            !designs.TryGetValue(p.Target ?? string.Empty, out Entity target))
                        {
                            levelUpsSkipped++;
                            continue;
                        }

                        EntityManager.AddComponentData(p.Building, new UnderConstruction { m_NewPrefab = target, m_Progress = p.Progress, m_Speed = p.Speed });
                        levelUps++;
                    }
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "SaveSafe: could not put all custom zones back on load; the rest of the city keeps game zones (safe, but they will not grow as custom zones).");
            }
            finally
            {
                m_Swap.ClearLoaded();
            }

            Mod.log.Info($"SaveSafe: custom zones put back on load: {cells:N0} cells, {lots:N0} free lots, {levelUps:N0} level-ups resumed; " +
                $"left as saved: {changed:N0} cells no longer as saved, {unknownZone:N0} cells of zones not found, {missingBlocks:N0} blocks not found, {levelUpsSkipped:N0} level-ups; {timer.Elapsed.TotalMilliseconds:0.#} ms.");
        }
    }
}
