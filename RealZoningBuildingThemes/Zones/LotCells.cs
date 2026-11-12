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
    /// Reads the zone cells under a building's lot, the way the game's zone check and level-up
    /// read them: one cell per lot square, visible cells inside the block's valid area only.
    /// Gives each square's zone type index (0 where no cell was found) and the lowest height
    /// limit over the lot (bridges, power lines), as the level-up uses it.
    /// </summary>
    public struct LotCells : System.IDisposable
    {
        /// <summary>Zone type index per lot square, row by row; 0 where no visible cell was found.</summary>
        public NativeArray<ushort> Zones;

        /// <summary>The lowest cell height limit over the lot minus the building's elevation; infinity when none.</summary>
        public float MaxHeight;

        public static LotCells Read(EntityManager em, NativeQuadTree<Entity, Bounds2> tree, Transform transform, int2 lotSize)
        {
            float2 right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
            float2 forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
            float2 halfRight = right * (lotSize.x * 0.5f - 0.5f);
            float2 halfForward = forward * (lotSize.y * 0.5f - 0.5f);
            float2 extent = math.abs(halfForward) + math.abs(halfRight);

            var iterator = new Iterator
            {
                m_Bounds = new Bounds2(transform.m_Position.xz - extent, transform.m_Position.xz + extent),
                m_LotSize = lotSize,
                m_StartPosition = transform.m_Position.xz + halfForward + halfRight,
                m_Right = right,
                m_Forward = forward,
                m_Zones = new NativeArray<ushort>(lotSize.x * lotSize.y, Allocator.Temp, NativeArrayOptions.ClearMemory),
                m_MaxHeight = int.MaxValue,
                m_EntityManager = em,
            };
            tree.Iterate(ref iterator);

            return new LotCells
            {
                Zones = iterator.m_Zones,
                MaxHeight = iterator.m_MaxHeight == int.MaxValue ? float.PositiveInfinity : iterator.m_MaxHeight - transform.m_Position.y,
            };
        }

        public void Dispose()
        {
            if (Zones.IsCreated)
            {
                Zones.Dispose();
            }
        }

        private struct Iterator : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
        {
            public Bounds2 m_Bounds;
            public int2 m_LotSize;
            public float2 m_StartPosition;
            public float2 m_Right;
            public float2 m_Forward;
            public NativeArray<ushort> m_Zones;
            public int m_MaxHeight;
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
                            if ((cell.m_State & CellFlags.Visible) != 0)
                            {
                                m_MaxHeight = math.min(m_MaxHeight, cell.m_Height);
                                if (cell.m_Zone.m_Index != 0)
                                {
                                    m_Zones[y * m_LotSize.x + x] = cell.m_Zone.m_Index;
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
