using System;
using Game;
using Game.Common;
using Game.Objects;
using Game.Zones;
using Unity.Entities;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// Step 3 of <see cref="SaveSwapSystem"/>: runs right after the game has copied the city into
    /// the save, and puts back exactly what step 1 changed (our zones' cells and free lots, and the
    /// paused level-ups), so play carries on as if nothing happened. The blocks are not marked as
    /// updated: they end up exactly as they were, so the game has nothing to recalculate.
    /// </summary>
    public partial class SaveRestoreSystem : GameSystemBase
    {
        private SaveSwapSystem m_Swap;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Swap = World.GetOrCreateSystemManaged<SaveSwapSystem>();
        }

        protected override void OnUpdate()
        {
            if (!m_Swap.HasPending)
            {
                return;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int cells = 0;
            int lots = 0;
            int levelUps = 0;
            int missing = 0;
            try
            {
                foreach (SaveSwapSystem.BlockEdits b in m_Swap.PendingBlocks)
                {
                    if (!EntityManager.Exists(b.Block) || !EntityManager.HasBuffer<Cell>(b.Block))
                    {
                        missing++;
                        continue;
                    }

                    DynamicBuffer<Cell> cellBuffer = EntityManager.GetBuffer<Cell>(b.Block);
                    foreach (SaveSwapSystem.Edit e in b.Cells)
                    {
                        if (e.Index < cellBuffer.Length)
                        {
                            Cell cell = cellBuffer[e.Index];
                            cell.m_Zone = e.Original;
                            cellBuffer[e.Index] = cell;
                            cells++;
                        }
                    }

                    if (b.Lots.Count > 0 && EntityManager.HasBuffer<VacantLot>(b.Block))
                    {
                        DynamicBuffer<VacantLot> lotBuffer = EntityManager.GetBuffer<VacantLot>(b.Block);
                        foreach (SaveSwapSystem.Edit e in b.Lots)
                        {
                            if (e.Index < lotBuffer.Length)
                            {
                                VacantLot lot = lotBuffer[e.Index];
                                lot.m_Type = e.Original;
                                lotBuffer[e.Index] = lot;
                                lots++;
                            }
                        }
                    }
                }

                foreach (SaveSwapSystem.PausedLevelUp p in m_Swap.PendingLevelUps)
                {
                    if (EntityManager.Exists(p.Building) && !EntityManager.HasComponent<Deleted>(p.Building) &&
                        !EntityManager.HasComponent<UnderConstruction>(p.Building))
                    {
                        EntityManager.AddComponentData(p.Building, p.Construction);
                        levelUps++;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "SaveSafe: could not put everything back after saving; reload the save to be sure the city is as it was.");
            }
            finally
            {
                m_Swap.ClearPending();
            }

            Mod.log.Info($"SaveSafe: custom zones back in place after saving: {cells:N0} cells, {lots:N0} free lots, {levelUps:N0} level-ups resumed{(missing > 0 ? $" ({missing} not found)" : string.Empty)}; {timer.Elapsed.TotalMilliseconds:0.#} ms.");
        }
    }
}
