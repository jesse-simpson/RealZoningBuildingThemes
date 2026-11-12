using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// The game's zone test for a grown building's lot (ZoneCheckSystem.ValidateZoneBlocks), with
    /// one change: a cell passes when it is the building's home zone (as in the game) or one of
    /// our zones that allows the building. The lot must also still face a road, as the game requires.
    /// </summary>
    public static class ZoneLotTest
    {
        /// <param name="home">The building's home zone type; ZoneType.None when only our zones count.</param>
        /// <param name="zones">Indexes of our zones that allow the building; may be null.</param>
        public static bool LotIsAllowed(EntityManager em, NativeQuadTree<Entity, Bounds2> tree, Transform transform, int2 lotSize, ZoneType home, List<ushort> zones)
        {
            float2 right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
            float2 forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
            float2 halfRight = right * (lotSize.x * 0.5f - 0.5f);
            float2 halfForward = forward * (lotSize.y * 0.5f - 0.5f);
            float2 extent = math.abs(halfForward) + math.abs(halfRight);

            var validated = new NativeArray<bool>(lotSize.x * lotSize.y, Allocator.Temp, NativeArrayOptions.ClearMemory);
            var iterator = new LotIterator
            {
                m_Bounds = new Bounds2(transform.m_Position.xz - extent, transform.m_Position.xz + extent),
                m_LotSize = lotSize,
                m_StartPosition = transform.m_Position.xz + halfForward + halfRight,
                m_Right = right,
                m_Forward = forward,
                m_Home = home,
                m_HasHome = !home.Equals(ZoneType.None),
                m_Zones = zones,
                m_Validated = validated,
                m_EntityManager = em,
            };
            tree.Iterate(ref iterator);

            bool ok = (iterator.m_Directions & CellFlags.Roadside) != 0;
            for (int i = 0; i < validated.Length; i++)
            {
                ok &= validated[i];
            }

            validated.Dispose();
            return ok;
        }

        private struct LotIterator : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
        {
            public Bounds2 m_Bounds;
            public int2 m_LotSize;
            public float2 m_StartPosition;
            public float2 m_Right;
            public float2 m_Forward;
            public ZoneType m_Home;
            public bool m_HasHome;
            public List<ushort> m_Zones;
            public CellFlags m_Directions;
            public NativeArray<bool> m_Validated;
            public EntityManager m_EntityManager;

            public bool Intersect(Bounds2 bounds)
            {
                return MathUtils.Intersect(bounds, m_Bounds);
            }

            public void Iterate(Bounds2 bounds, Entity blockEntity)
            {
                if (!MathUtils.Intersect(bounds, m_Bounds) ||
                    !m_EntityManager.HasComponent<ValidArea>(blockEntity) ||
                    !m_EntityManager.HasComponent<Block>(blockEntity) ||
                    !m_EntityManager.HasBuffer<Cell>(blockEntity))
                {
                    return;
                }

                ValidArea validArea = m_EntityManager.GetComponentData<ValidArea>(blockEntity);
                if (validArea.m_Area.y <= validArea.m_Area.x)
                {
                    return;
                }

                var target = new Block { m_Direction = m_Forward };
                Block block = m_EntityManager.GetComponentData<Block>(blockEntity);
                DynamicBuffer<Cell> cells = m_EntityManager.GetBuffer<Cell>(blockEntity, true);

                float2 rowStart = m_StartPosition;
                for (int y = 0; y < m_LotSize.y; y++)
                {
                    float2 position = rowStart;
                    for (int x = 0; x < m_LotSize.x; x++)
                    {
                        int2 cellIndex = ZoneUtils.GetCellIndex(block, position);
                        if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
                        {
                            Cell cell = cells[cellIndex.y * block.m_Size.x + cellIndex.x];
                            bool zoneOk = (m_HasHome && cell.m_Zone.Equals(m_Home)) || (m_Zones != null && m_Zones.Contains(cell.m_Zone.m_Index));
                            if ((cell.m_State & CellFlags.Visible) != 0 && zoneOk)
                            {
                                m_Validated[y * m_LotSize.x + x] = true;
                                if ((cell.m_State & (CellFlags.Roadside | CellFlags.RoadLeft | CellFlags.RoadRight | CellFlags.RoadBack)) != 0)
                                {
                                    CellFlags roadDirection = ZoneUtils.GetRoadDirection(target, block, cell.m_State);
                                    int4 mask = math.select(int4.zero, new int4(512, 4, 1024, 2048), new bool4(new int2(x, y) == 0, new int2(x, y) == m_LotSize - 1));
                                    m_Directions |= (CellFlags)((uint)roadDirection & (uint)(ushort)math.csum(mask));
                                }
                            }
                        }

                        position -= m_Right;
                    }

                    rowStart -= m_Forward;
                }
            }
        }
    }
}
