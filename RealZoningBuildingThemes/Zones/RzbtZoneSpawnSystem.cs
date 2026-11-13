// Adapted from Custom Zones' PaletteZoneSpawnSystem.
// Copyright (c) 2026 dvovo. Licensed under the MIT License:
// https://github.com/dvovo/CustomZones/blob/main/LICENSE (copy in LICENSES/MIT-CustomZones.txt)
// Changes for Real Zoning & Building Themes: our zone and allow-list components, level 1 only
// (as the game spawns), no free-run merging (vanilla lot sizes), no growth diagnostics, and
// residential candidates of different densities ranked on the zone's own density demand (each
// still gated by its own), so densities and themes can mix in one zone.

using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using BuildingData = Game.Prefabs.BuildingData;

namespace RealZoningBuildingThemes.Zones
{
    /// <summary>
    /// The city's zone spawner. Replaces the game's ZoneSpawnSystem (disabled in Mod.OnLoad) at
    /// the same pace, so the city grows at the game's own rate:
    ///   - the game's zones: the game's own EvaluateSpawnAreas job, unchanged;
    ///   - our zones: <see cref="EvaluateOurZones"/>, which picks from the zone's allow-list with
    ///     the game's demand and score rules, scoring each building on its own home zone, so one
    ///     zone may mix themes and densities.
    /// Both feed the same three candidate queues, and the game's SpawnBuildingJob spawns the best
    /// candidate of each type, as in the game. If anything throws, the game's spawner is turned
    /// back on and this system turns itself off.
    /// </summary>
    public partial class RzbtZoneSpawnSystem : GameSystemBase
    {
        private EntityQuery m_VacantLotsQuery;
        private EntityQuery m_ProcessQuery;
        private EntityQuery m_BuildingConfigQuery;
        private EntityQuery m_PreferenceQuery;
        private EntityQuery m_BuildingQuery;
        private EntityArchetype m_DefinitionArchetype;

        private Game.Prefabs.ZoneSystem m_ZoneSystem;
        private ZoneSpawnSystem m_ZoneSpawnSystem;
        private ResidentialDemandSystem m_ResidentialDemandSystem;
        private CommercialDemandSystem m_CommercialDemandSystem;
        private IndustrialDemandSystem m_IndustrialDemandSystem;
        private GroundPollutionSystem m_PollutionSystem;
        private AirPollutionSystem m_AirPollutionSystem;
        private NoisePollutionSystem m_NoisePollutionSystem;
        private TerrainSystem m_TerrainSystem;
        private Game.Zones.SearchSystem m_SearchSystem;
        private ResourceSystem m_ResourceSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private EndFrameBarrier m_EndFrameBarrier;

        private readonly System.Diagnostics.Stopwatch m_Timer = new System.Diagnostics.Stopwatch();
        private int m_TimedPasses;
        private double m_TimedMs;
        private double m_MaxMs;

        // Same pace as the game's ZoneSpawnSystem.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        public override int GetUpdateOffset(SystemUpdatePhase phase) => 13;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_VacantLotsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CurvePosition>(),
                    ComponentType.ReadOnly<VacantLot>(),
                },
                None = new[]
                {
                    ComponentType.ReadWrite<Temp>(),
                    ComponentType.ReadWrite<Deleted>(),
                },
            });

            m_DefinitionArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<CreationDefinition>(),
                ComponentType.ReadWrite<ObjectDefinition>(),
                ComponentType.ReadWrite<Updated>(),
                ComponentType.ReadWrite<Deleted>());

            m_ProcessQuery = GetEntityQuery(ComponentType.ReadOnly<IndustrialProcessData>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(),
                ComponentType.ReadOnly<BuildingSpawnGroupData>(),
                ComponentType.ReadOnly<PrefabData>());
            m_BuildingConfigQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_PreferenceQuery = GetEntityQuery(ComponentType.ReadOnly<ZonePreferenceData>());

            m_ZoneSystem = World.GetOrCreateSystemManaged<Game.Prefabs.ZoneSystem>();
            m_ZoneSpawnSystem = World.GetOrCreateSystemManaged<ZoneSpawnSystem>();
            m_ResidentialDemandSystem = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            m_CommercialDemandSystem = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            m_IndustrialDemandSystem = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            m_PollutionSystem = World.GetOrCreateSystemManaged<GroundPollutionSystem>();
            m_AirPollutionSystem = World.GetOrCreateSystemManaged<AirPollutionSystem>();
            m_NoisePollutionSystem = World.GetOrCreateSystemManaged<NoisePollutionSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_SearchSystem = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            RequireForUpdate(m_VacantLotsQuery);
        }

        protected override void OnUpdate()
        {
            try
            {
                m_Timer.Restart();
                UpdateSpawn();
                m_Timer.Stop();
                double ms = m_Timer.Elapsed.TotalMilliseconds;
                m_TimedMs += ms;
                m_MaxMs = System.Math.Max(m_MaxMs, ms);
                if (++m_TimedPasses >= 4096)
                {
                    Mod.log.Info($"Spawn: {m_TimedPasses} passes, main thread {m_TimedMs / m_TimedPasses:0.###} ms on average, {m_MaxMs:0.##} ms at most.");
                    m_TimedPasses = 0;
                    m_TimedMs = 0;
                    m_MaxMs = 0;
                }
            }
            catch (System.Exception e)
            {
                Mod.log.Error(e, "Spawn: failed; handing growth back to the game's own spawner (custom zones stop growing until the mod is fixed).");
                m_ZoneSpawnSystem.Enabled = true;
                Enabled = false;
            }
        }

        private void UpdateSpawn()
        {
            // The game's overall gates: no type grows while its demand bar is empty.
            bool fastSpawn = m_ZoneSpawnSystem.debugFastSpawn;
            int3 residentialDemand = m_ResidentialDemandSystem.buildingDemand;
            bool spawnResidential = fastSpawn || (residentialDemand.x + residentialDemand.y + residentialDemand.z) / 3 > 0;
            bool spawnCommercial = fastSpawn || m_CommercialDemandSystem.buildingDemand > 0;
            bool spawnIndustrial = fastSpawn || (m_IndustrialDemandSystem.industrialBuildingDemand + m_IndustrialDemandSystem.officeBuildingDemand) / 2 > 0;
            bool spawnStorage = fastSpawn || m_IndustrialDemandSystem.storageBuildingDemand > 0;

            var residentialQ = new NativeQueue<ZoneSpawnSystem.SpawnLocation>(Allocator.TempJob);
            var commercialQ = new NativeQueue<ZoneSpawnSystem.SpawnLocation>(Allocator.TempJob);
            var industrialQ = new NativeQueue<ZoneSpawnSystem.SpawnLocation>(Allocator.TempJob);
            var preferences = m_PreferenceQuery.GetSingleton<ZonePreferenceData>();

            var ours = new EvaluateOurZones
            {
                entityHandle = GetEntityTypeHandle(),
                ownerHandle = GetComponentTypeHandle<Owner>(true),
                curvePositionHandle = GetComponentTypeHandle<CurvePosition>(true),
                blockHandle = GetComponentTypeHandle<Block>(true),
                vacantLotHandle = GetBufferTypeHandle<VacantLot>(true),

                zonePrefabs = m_ZoneSystem.GetPrefabs(),
                ourZoneLookup = GetComponentLookup<RzbtZone>(true),
                allowedBuildingLookup = GetBufferLookup<RzbtAllowedBuilding>(true),
                zoneDataLookup = GetComponentLookup<ZoneData>(true),
                zonePropertiesDataLookup = GetComponentLookup<ZonePropertiesData>(true),
                processEstimatesLookup = GetBufferLookup<ProcessEstimate>(true),
                spawnableLookup = GetComponentLookup<SpawnableBuildingData>(true),
                buildingLookup = GetComponentLookup<BuildingData>(true),
                propertyLookup = GetComponentLookup<BuildingPropertyData>(true),
                objGeomLookup = GetComponentLookup<ObjectGeometryData>(true),
                warehouseLookup = GetComponentLookup<WarehouseData>(true),
                resourceAvailabilityLookup = GetBufferLookup<ResourceAvailability>(true),
                blockLookup = GetComponentLookup<Block>(true),
                landValueLookup = GetComponentLookup<LandValue>(true),
                resourceDataLookup = GetComponentLookup<ResourceData>(true),
                lockedLookup = GetComponentLookup<Locked>(true),
                resourcePrefabs = m_ResourceSystem.GetPrefabs(),

                groundPollutionMap = m_PollutionSystem.GetMap(true, out var pollutionJob),
                airPollutionMap = m_AirPollutionSystem.GetMap(true, out var airPollutionJob),
                noisePollutionMap = m_NoisePollutionSystem.GetMap(true, out var noisePollutionJob),
                spawnResidential = spawnResidential,
                spawnCommercial = spawnCommercial,
                spawnIndustrial = spawnIndustrial,
                spawnStorage = spawnStorage,
                zonePreferenceData = preferences,
                industrialProcesses = m_ProcessQuery.ToComponentDataListAsync<IndustrialProcessData>(World.UpdateAllocator.ToAllocator, out var processJob),

                residentialDemands = residentialDemand,
                commercialDemands = m_CommercialDemandSystem.GetBuildingDemands(out var commercialDemandJob),
                industrialDemands = m_IndustrialDemandSystem.GetBuildingDemands(out var industrialDemandJob),
                storageDemands = m_IndustrialDemandSystem.GetStorageBuildingDemands(out var storageDemandJob),

                residentialQ = residentialQ.AsParallelWriter(),
                commercialQ = commercialQ.AsParallelWriter(),
                industrialQ = industrialQ.AsParallelWriter(),

                randomSeed = RandomSeed.Next(),
                minDemand = fastSpawn ? 0 : 1,
            };

            var spawnBuildingJob = new ZoneSpawnSystem.SpawnBuildingJob
            {
                m_BlockData = GetComponentLookup<Block>(true),
                m_BuildingConfigurationData = m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>(),
                m_Cells = GetBufferLookup<Cell>(true),
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter(),
                m_Commercial = commercialQ,
                m_DefinitionArchetype = m_DefinitionArchetype,
                m_Industrial = industrialQ,
                m_LefthandTraffic = m_CityConfigurationSystem.leftHandTraffic,
                m_PrefabAreaGeometryData = GetComponentLookup<AreaGeometryData>(true),
                m_PrefabBuildingData = GetComponentLookup<BuildingData>(true),
                m_PrefabNetGeometryData = GetComponentLookup<NetGeometryData>(true),
                m_PrefabObjectGeometryData = GetComponentLookup<ObjectGeometryData>(true),
                m_PrefabPlaceableObjectData = GetComponentLookup<PlaceableObjectData>(true),
                m_PrefabPlaceholderElements = GetBufferLookup<PlaceholderObjectElement>(true),
                m_PrefabRefData = GetComponentLookup<PrefabRef>(true),
                m_PrefabSpawnableObjectData = GetComponentLookup<SpawnableObjectData>(true),
                m_PrefabSubAreaNodes = GetBufferLookup<Game.Prefabs.SubAreaNode>(true),
                m_PrefabSubAreas = GetBufferLookup<Game.Prefabs.SubArea>(true),
                m_PrefabSubNets = GetBufferLookup<Game.Prefabs.SubNet>(true),
                m_RandomSeed = RandomSeed.Next(),
                m_Residential = residentialQ,
                m_TerrainHeightData = m_TerrainSystem.GetHeightData(false),
                m_TransformData = GetComponentLookup<Game.Objects.Transform>(true),
                m_ValidAreaData = GetComponentLookup<ValidArea>(true),
                m_ZoneSearchTree = m_SearchSystem.GetSearchTree(true, out var searchTreeJob),
            };

            // The game's own evaluation for the game's zones, into the same queues. On our zones
            // it finds nothing, because no building's home zone type matches ours.
            var vanilla = new ZoneSpawnSystem.EvaluateSpawnAreas
            {
                m_BuildingChunks = m_BuildingQuery.ToArchetypeChunkListAsync(World.UpdateAllocator.ToAllocator, out var buildingChunksJob),
                m_ZonePrefabs = m_ZoneSystem.GetPrefabs(),
                m_Preferences = preferences,
                m_SpawnResidential = spawnResidential ? 1 : 0,
                m_SpawnCommercial = spawnCommercial ? 1 : 0,
                m_SpawnIndustrial = spawnIndustrial ? 1 : 0,
                m_SpawnStorage = spawnStorage ? 1 : 0,
                m_MinDemand = fastSpawn ? 0 : 1,
                m_ResidentialDemands = residentialDemand,
                m_CommercialBuildingDemands = ours.commercialDemands,
                m_IndustrialDemands = ours.industrialDemands,
                m_StorageDemands = ours.storageDemands,
                m_RandomSeed = RandomSeed.Next(),
                m_EntityType = GetEntityTypeHandle(),
                m_BlockType = GetComponentTypeHandle<Block>(true),
                m_OwnerType = GetComponentTypeHandle<Owner>(true),
                m_CurvePositionType = GetComponentTypeHandle<CurvePosition>(true),
                m_VacantLotType = GetBufferTypeHandle<VacantLot>(true),
                m_BuildingDataType = GetComponentTypeHandle<BuildingData>(true),
                m_SpawnableBuildingType = GetComponentTypeHandle<SpawnableBuildingData>(true),
                m_BuildingPropertyType = GetComponentTypeHandle<BuildingPropertyData>(true),
                m_ObjectGeometryType = GetComponentTypeHandle<ObjectGeometryData>(true),
                m_BuildingSpawnGroupType = GetSharedComponentTypeHandle<BuildingSpawnGroupData>(),
                m_WarehouseType = GetComponentTypeHandle<WarehouseData>(true),
                m_ZoneData = GetComponentLookup<ZoneData>(true),
                m_ZonePropertiesDatas = GetComponentLookup<ZonePropertiesData>(true),
                m_Availabilities = GetBufferLookup<ResourceAvailability>(true),
                m_LandValues = GetComponentLookup<LandValue>(true),
                m_BlockData = GetComponentLookup<Block>(true),
                m_Processes = ours.industrialProcesses,
                m_ProcessEstimates = GetBufferLookup<ProcessEstimate>(true),
                m_ResourceDatas = GetComponentLookup<ResourceData>(true),
                m_ResourcePrefabs = ours.resourcePrefabs,
                m_GroundPollutionMap = ours.groundPollutionMap,
                m_AirPollutionMap = ours.airPollutionMap,
                m_NoisePollutionMap = ours.noisePollutionMap,
                m_Residential = residentialQ.AsParallelWriter(),
                m_Commercial = commercialQ.AsParallelWriter(),
                m_Industrial = industrialQ.AsParallelWriter(),
            };

            JobHandle inputs = JobHandle.CombineDependencies(
                JobHandle.CombineDependencies(Dependency, pollutionJob, processJob),
                JobHandle.CombineDependencies(airPollutionJob, noisePollutionJob, buildingChunksJob),
                JobHandle.CombineDependencies(commercialDemandJob, industrialDemandJob, storageDemandJob));

            JobHandle vanillaHandle = vanilla.ScheduleParallel(m_VacantLotsQuery, inputs);

            JobHandle evaluateHandle = ours.ScheduleParallel(m_VacantLotsQuery, vanillaHandle);

            // The three queues spawn their best candidates in parallel; keep one of any two
            // winners whose lots come close, so they cannot overlap.
            var reconcile = new KeepOneWhereClose
            {
                blockLookup = GetComponentLookup<Block>(true),
                residentialQ = residentialQ,
                commercialQ = commercialQ,
                industrialQ = industrialQ,
                randomSeed = RandomSeed.Next(),
            };
            JobHandle reconcileHandle = reconcile.Schedule(evaluateHandle);

            JobHandle spawnHandle = spawnBuildingJob.Schedule(3, 1, JobHandle.CombineDependencies(reconcileHandle, searchTreeJob));

            m_ResourceSystem.AddPrefabsReader(evaluateHandle);
            m_PollutionSystem.AddReader(evaluateHandle);
            m_AirPollutionSystem.AddReader(evaluateHandle);
            m_NoisePollutionSystem.AddReader(evaluateHandle);
            m_CommercialDemandSystem.AddReader(evaluateHandle);
            m_IndustrialDemandSystem.AddReader(evaluateHandle);

            residentialQ.Dispose(spawnHandle);
            commercialQ.Dispose(spawnHandle);
            industrialQ.Dispose(spawnHandle);

            m_ZoneSystem.AddPrefabsReader(evaluateHandle);
            m_TerrainSystem.AddCPUHeightReader(spawnHandle);
            m_EndFrameBarrier.AddJobHandleForProducer(spawnHandle);
            m_SearchSystem.AddSearchTreeReader(spawnHandle);

            Dependency = spawnHandle;
        }

        /// <summary>
        /// Runs between evaluation and spawning. The game spawns the best candidate of each
        /// queue (residential, commercial, industrial) at the same time, in parallel, before any
        /// of the new buildings marks its cells as taken. In the game's own zones the three
        /// winners are nearly always far apart, but a zone of ours can allow several types, so
        /// two winners can be the same block, or two blocks meeting at a corner (a corner lot
        /// reaches round the corner), and the buildings would overlap. This keeps the best of
        /// each queue, and of any two whose lots come close keeps one, at random so every type
        /// still gets its turn. A dropped type simply waits for the next spawn pass.
        /// Checks the game's own candidates too, since a corner can join one of its zones to ours.
        /// </summary>
        [BurstCompile]
        public struct KeepOneWhereClose : IJob
        {
            [ReadOnly] public ComponentLookup<Block> blockLookup;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation> residentialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation> commercialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation> industrialQ;
            public RandomSeed randomSeed;

            public void Execute()
            {
                var random = randomSeed.GetRandom(0);
                ZoneSpawnSystem.SpawnLocation residential = TakeBest(residentialQ);
                ZoneSpawnSystem.SpawnLocation commercial = TakeBest(commercialQ);
                ZoneSpawnSystem.SpawnLocation industrial = TakeBest(industrialQ);

                KeepOne(ref residential, ref commercial, ref random);
                KeepOne(ref residential, ref industrial, ref random);
                KeepOne(ref commercial, ref industrial, ref random);

                if (residential.m_Priority != 0f)
                {
                    residentialQ.Enqueue(residential);
                }

                if (commercial.m_Priority != 0f)
                {
                    commercialQ.Enqueue(commercial);
                }

                if (industrial.m_Priority != 0f)
                {
                    industrialQ.Enqueue(industrial);
                }
            }

            /// <summary>Empties the queue and returns its best candidate, as the game's spawn job would pick it.</summary>
            private static ZoneSpawnSystem.SpawnLocation TakeBest(NativeQueue<ZoneSpawnSystem.SpawnLocation> queue)
            {
                var best = default(ZoneSpawnSystem.SpawnLocation);
                while (queue.TryDequeue(out ZoneSpawnSystem.SpawnLocation location))
                {
                    if (location.m_Priority > best.m_Priority)
                    {
                        best = location;
                    }
                }

                return best;
            }

            private void KeepOne(ref ZoneSpawnSystem.SpawnLocation a, ref ZoneSpawnSystem.SpawnLocation b, ref Random random)
            {
                if (a.m_Priority != 0f && b.m_Priority != 0f && LotsClose(a, b))
                {
                    if (random.NextBool())
                    {
                        a.m_Priority = 0f;
                    }
                    else
                    {
                        b.m_Priority = 0f;
                    }
                }
            }

            /// <summary>
            /// True when two lots are on the same block, or their centres are nearer than the sum
            /// of their half diagonals plus one cell (8 m) each for a corner lot. Errs on the side
            /// of caution.
            /// </summary>
            private bool LotsClose(in ZoneSpawnSystem.SpawnLocation a, in ZoneSpawnSystem.SpawnLocation b)
            {
                if (a.m_Entity == b.m_Entity)
                {
                    return true;
                }

                if (!blockLookup.TryGetComponent(a.m_Entity, out Block blockA) || !blockLookup.TryGetComponent(b.m_Entity, out Block blockB))
                {
                    return false;
                }

                float3 centreA = ZoneUtils.GetPosition(blockA, a.m_LotArea.xz, a.m_LotArea.yw);
                float3 centreB = ZoneUtils.GetPosition(blockB, b.m_LotArea.xz, b.m_LotArea.yw);
                float reachA = 4f * math.length((float2)(a.m_LotArea.yw - a.m_LotArea.xz)) + 8f;
                float reachB = 4f * math.length((float2)(b.m_LotArea.yw - b.m_LotArea.xz)) + 8f;
                return math.distance(centreA.xz, centreB.xz) < reachA + reachB;
            }
        }

        /// <summary>
        /// The game's spawn evaluation for the lots of our zones: each allowed level 1 building
        /// that fits a free lot is scored by the rules of its own home zone and offered to the
        /// queue of its type.
        /// </summary>
        [BurstCompile]
        public struct EvaluateOurZones : IJobChunk
        {
            public EntityTypeHandle entityHandle;
            [ReadOnly] public ComponentTypeHandle<Owner> ownerHandle;
            [ReadOnly] public ComponentTypeHandle<CurvePosition> curvePositionHandle;
            [ReadOnly] public ComponentTypeHandle<Block> blockHandle;
            [ReadOnly] public BufferTypeHandle<VacantLot> vacantLotHandle;

            [ReadOnly] public ZonePrefabs zonePrefabs;
            [ReadOnly] public ComponentLookup<RzbtZone> ourZoneLookup;
            [ReadOnly] public BufferLookup<RzbtAllowedBuilding> allowedBuildingLookup;
            [ReadOnly] public ComponentLookup<ZoneData> zoneDataLookup;
            [ReadOnly] public ComponentLookup<ZonePropertiesData> zonePropertiesDataLookup;
            [ReadOnly] public BufferLookup<ProcessEstimate> processEstimatesLookup;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> spawnableLookup;
            [ReadOnly] public ComponentLookup<BuildingData> buildingLookup;
            [ReadOnly] public ComponentLookup<BuildingPropertyData> propertyLookup;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> objGeomLookup;
            [ReadOnly] public ComponentLookup<WarehouseData> warehouseLookup;
            [ReadOnly] public BufferLookup<ResourceAvailability> resourceAvailabilityLookup;
            [ReadOnly] public ComponentLookup<Block> blockLookup;
            [ReadOnly] public ComponentLookup<LandValue> landValueLookup;
            [ReadOnly] public ComponentLookup<ResourceData> resourceDataLookup;
            [ReadOnly] public ComponentLookup<Locked> lockedLookup;
            [ReadOnly] public ResourcePrefabs resourcePrefabs;

            [ReadOnly] public NativeArray<GroundPollution> groundPollutionMap;
            [ReadOnly] public NativeArray<AirPollution> airPollutionMap;
            [ReadOnly] public NativeArray<NoisePollution> noisePollutionMap;
            public bool spawnResidential;
            public bool spawnCommercial;
            public bool spawnIndustrial;
            public bool spawnStorage;
            public ZonePreferenceData zonePreferenceData;
            [ReadOnly] public NativeList<IndustrialProcessData> industrialProcesses;

            public int3 residentialDemands;
            [ReadOnly] public NativeArray<int> commercialDemands;
            [ReadOnly] public NativeArray<int> industrialDemands;
            [ReadOnly] public NativeArray<int> storageDemands;

            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter residentialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter commercialQ;
            public NativeQueue<ZoneSpawnSystem.SpawnLocation>.ParallelWriter industrialQ;

            public RandomSeed randomSeed;
            public int minDemand;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var random = randomSeed.GetRandom(unfilteredChunkIndex);

                // Best lot per type, like the game (one candidate of each type per chunk).
                var bestResidential = default(ZoneSpawnSystem.SpawnLocation);
                var bestCommercial = default(ZoneSpawnSystem.SpawnLocation);
                var bestIndustrial = default(ZoneSpawnSystem.SpawnLocation);

                var entities = chunk.GetNativeArray(entityHandle);
                var vacantLotsAccessor = chunk.GetBufferAccessor(ref vacantLotHandle);
                if (vacantLotsAccessor.Length == 0)
                {
                    return;
                }

                var owners = chunk.GetNativeArray(ref ownerHandle);
                var curvePositions = chunk.GetNativeArray(ref curvePositionHandle);
                var blocks = chunk.GetNativeArray(ref blockHandle);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    var vacantLots = vacantLotsAccessor[i];
                    Owner owner = owners[i];
                    CurvePosition curvePos = curvePositions[i];
                    Block block = blocks[i];

                    for (int j = 0; j < vacantLots.Length; j++)
                    {
                        VacantLot vacantLot = vacantLots[j];
                        Entity zonePrefab = zonePrefabs[vacantLot.m_Type];

                        // Only our zones with a non-empty allow-list.
                        if (!ourZoneLookup.HasComponent(zonePrefab) ||
                            !allowedBuildingLookup.TryGetBuffer(zonePrefab, out var allowed) ||
                            allowed.Length == 0 ||
                            !zoneDataLookup.TryGetComponent(zonePrefab, out var zoneData))
                        {
                            continue;
                        }

                        float curvePosScalar = CalculateCurvePos(curvePos, vacantLot, block);

                        // The demand used to rank residential candidates on this lot: the zone's own
                        // density bar, so a low-density house and a row house compete on fit and
                        // location rather than on which density bar happens to be higher.
                        int rankDemand = -1;
                        if (zoneData.m_AreaType == AreaType.Residential)
                        {
                            zonePropertiesDataLookup.TryGetComponent(zonePrefab, out var zoneProperties);
                            ZoneDensity density = PropertyUtils.GetZoneDensity(zoneData, zoneProperties);
                            rankDemand = density == ZoneDensity.Low ? residentialDemands.x
                                : density == ZoneDensity.Medium ? residentialDemands.y : residentialDemands.z;
                        }

                        // A zone may mix types: each type of building on its list competes in that
                        // type's queue, under that type's demand gate.
                        int types = AllowedTypes(allowed);
                        if ((types & 1) != 0 && spawnResidential)
                        {
                            TryAddLot(allowed, AreaType.Residential, ref bestResidential, ref random, owner.m_Owner, curvePosScalar, entity,
                                vacantLot.m_Area, vacantLot.m_Flags, vacantLot.m_Height, zoneData, true, false, rankDemand);
                        }

                        if ((types & 2) != 0 && spawnCommercial)
                        {
                            TryAddLot(allowed, AreaType.Commercial, ref bestCommercial, ref random, owner.m_Owner, curvePosScalar, entity,
                                vacantLot.m_Area, vacantLot.m_Flags, vacantLot.m_Height, zoneData, true, false, rankDemand);
                        }

                        if ((types & 4) != 0 && (spawnIndustrial || spawnStorage))
                        {
                            TryAddLot(allowed, AreaType.Industrial, ref bestIndustrial, ref random, owner.m_Owner, curvePosScalar, entity,
                                vacantLot.m_Area, vacantLot.m_Flags, vacantLot.m_Height, zoneData, spawnIndustrial, spawnStorage, rankDemand);
                        }
                    }
                }

                if (bestResidential.m_Priority != 0f)
                {
                    residentialQ.Enqueue(bestResidential);
                }

                if (bestCommercial.m_Priority != 0f)
                {
                    commercialQ.Enqueue(bestCommercial);
                }

                if (bestIndustrial.m_Priority != 0f)
                {
                    industrialQ.Enqueue(bestIndustrial);
                }
            }

            private bool IsLocked(Entity prefab) => lockedLookup.HasComponent(prefab) && lockedLookup.IsComponentEnabled(prefab);

            /// <summary>Area types on an allow-list (bit 1 residential, 2 commercial, 4 industrial or office).</summary>
            private int AllowedTypes(DynamicBuffer<RzbtAllowedBuilding> allowed)
            {
                int types = 0;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (spawnableLookup.TryGetComponent(allowed[i].m_Building, out var spawnable) &&
                        zoneDataLookup.TryGetComponent(spawnable.m_ZonePrefab, out var buildingZone))
                    {
                        if (buildingZone.m_AreaType == AreaType.Residential)
                        {
                            types |= 1;
                        }
                        else if (buildingZone.m_AreaType == AreaType.Commercial)
                        {
                            types |= 2;
                        }
                        else if (buildingZone.m_AreaType == AreaType.Industrial)
                        {
                            types |= 4;
                        }
                    }
                }

                return types;
            }

            private float CalculateCurvePos(CurvePosition curvePosition, VacantLot lot, Block block)
            {
                float s = math.saturate((lot.m_Area.x + lot.m_Area.y) * 0.5f / block.m_Size.x);
                return math.lerp(curvePosition.m_CurvePosition.x, curvePosition.m_CurvePosition.y, s);
            }

            private void TryAddLot(DynamicBuffer<RzbtAllowedBuilding> allowed, AreaType type, ref ZoneSpawnSystem.SpawnLocation bestLocation,
                ref Random random, Entity road, float curvePos, Entity entity, int4 area, LotFlags flags, int height,
                ZoneData zoneData, bool normal, bool storage, int rankDemand)
            {
                if (!resourceAvailabilityLookup.HasBuffer(road) || !blockLookup.HasComponent(entity))
                {
                    return;
                }

                var spawnLocation = default(ZoneSpawnSystem.SpawnLocation);
                spawnLocation.m_Entity = entity;
                spawnLocation.m_LotArea = area;
                spawnLocation.m_ZoneType = zoneData.m_ZoneType;
                spawnLocation.m_AreaType = type;
                spawnLocation.m_LotFlags = flags;

                var availabilities = resourceAvailabilityLookup[road];
                float3 position = ZoneUtils.GetPosition(blockLookup[entity], spawnLocation.m_LotArea.xz, spawnLocation.m_LotArea.yw);

                // Ground, noise and air pollution, in the order the game's score expects.
                var pollution = new float3(
                    GroundPollutionSystem.GetPollution(position, groundPollutionMap).m_Pollution,
                    NoisePollutionSystem.GetPollution(position, noisePollutionMap).m_Pollution,
                    AirPollutionSystem.GetPollution(position, airPollutionMap).m_Pollution);
                float landValue = landValueLookup[road].m_LandValue;
                float maxHeight = height - position.y;

                if (SelectBuilding(allowed, type, ref spawnLocation, ref random, availabilities, curvePos, pollution, landValue, maxHeight, normal, storage, rankDemand) &&
                    spawnLocation.m_Priority > bestLocation.m_Priority)
                {
                    bestLocation = spawnLocation;
                }
            }

            /// <summary>Share of the overall best priority a home zone's best design needs to be in the draw.</summary>
            private const float kThemeShare = 0.5f;

            private struct GroupBest
            {
                public Entity m_HomeZone;
                public float m_Priority;
                public int m_Index;
            }

            private bool SelectBuilding(DynamicBuffer<RzbtAllowedBuilding> allowed, AreaType type, ref ZoneSpawnSystem.SpawnLocation location,
                ref Random random, DynamicBuffer<ResourceAvailability> availabilities,
                float curvePos, float3 pollution, float landValue, float maxHeight, bool normal, bool storage, int rankDemand)
            {
                int2 maxLotSize = location.m_LotArea.yw - location.m_LotArea.xz;
                var chosenBuildingData = default(BuildingData);

                // Best candidate per home zone (one theme and density of the game's), and overall.
                var groups = new FixedList4096Bytes<GroupBest>();
                float overallBest = 0f;

                for (int i = 0; i < allowed.Length; i++)
                {
                    Entity building = allowed[i].m_Building;

                    if (!spawnableLookup.TryGetComponent(building, out var spawnableData) ||
                        spawnableData.m_Level != 1 ||
                        !buildingLookup.TryGetComponent(building, out var subjBuildingData) ||
                        !propertyLookup.TryGetComponent(building, out var buildingPropertyData) ||
                        !objGeomLookup.TryGetComponent(building, out var objGeom) ||
                        !zoneDataLookup.TryGetComponent(spawnableData.m_ZonePrefab, out var buildingZone) ||
                        buildingZone.m_AreaType != type)
                    {
                        continue;
                    }

                    // Milestones: a building whose home zone the city has not unlocked does not grow.
                    if (IsLocked(spawnableData.m_ZonePrefab) || IsLocked(building))
                    {
                        continue;
                    }

                    // The lot's corners as our zone sees them. (The game also clears corners the
                    // building's home zone does not support, but in a mixed zone that let designs
                    // from corner-less zones, such as row houses, take corner lots at full score.)
                    LotFlags lotFlags = location.m_LotFlags;
                    var lotAccess = new bool2((lotFlags & LotFlags.CornerLeft) != 0, (lotFlags & LotFlags.CornerRight) != 0);
                    bool supportsNarrow = (buildingZone.m_ZoneFlags & ZoneFlags.SupportNarrow) == 0;

                    // Demand and score come from the building's own home zone (its density bar,
                    // office or industry, process estimates), so densities and themes can share a zone.
                    zonePropertiesDataLookup.TryGetComponent(spawnableData.m_ZonePrefab, out var buildingZoneProperties);
                    if (!processEstimatesLookup.TryGetBuffer(spawnableData.m_ZonePrefab, out var estimates))
                    {
                        continue;
                    }

                    // As the game decides it: an industrial zone without process estimates is office.
                    bool office = buildingZone.m_AreaType == AreaType.Industrial && estimates.Length == 0;

                    int2 lotSize = subjBuildingData.m_LotSize;
                    var buildingAccess = new bool2(
                        (subjBuildingData.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) != 0,
                        (subjBuildingData.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) != 0);

                    if (!math.all(lotSize <= maxLotSize) || objGeom.m_Size.y > maxHeight)
                    {
                        continue;
                    }

                    bool isStorage = warehouseLookup.HasComponent(building);
                    if (isStorage ? !storage : !normal)
                    {
                        continue;
                    }

                    AreaType evalAreaType = buildingZone.m_AreaType;
                    ZoneDensity zoneDensity = PropertyUtils.GetZoneDensity(buildingZone, buildingZoneProperties);
                    int demand = EvaluateDemandAndAvailability(evalAreaType, buildingPropertyData, zoneDensity, isStorage);
                    if (demand < minDemand)
                    {
                        continue; // gate: the building's own density or type must be in demand
                    }

                    if (evalAreaType == AreaType.Residential && rankDemand >= 0)
                    {
                        demand = math.max(rankDemand, minDemand); // rank on the zone's own density bar
                    }

                    int2 spare = math.select(maxLotSize - lotSize, 0, lotSize == maxLotSize - 1);
                    float priority = lotSize.x * lotSize.y * random.NextFloat(1f, 1.05f);
                    priority += spare.x * lotSize.y * random.NextFloat(0.95f, 1f);
                    priority += maxLotSize.x * spare.y * random.NextFloat(0.55f, 0.6f);
                    priority /= maxLotSize.x * maxLotSize.y;
                    priority *= demand + 1;

                    // Corner access as the game weighs it: a lot corner the building does not face
                    // only lowers its score, it does not rule it out.
                    priority *= math.csum(math.select(0.01f, 0.5f, lotAccess == buildingAccess));

                    // A mixed zone offers many more designs per lot than one game zone, so the best
                    // of many mid-block designs would often outscore the few corner designs. A
                    // corner the building does not face costs it a further factor of 5, so corner
                    // designs win corners unless none fits (then a mid-block design still grows).
                    if (math.any(lotAccess & !buildingAccess))
                    {
                        priority *= 0.2f;
                    }

                    float valuePerProperty = landValue;
                    float properties;
                    if (evalAreaType == AreaType.Residential)
                    {
                        properties = buildingPropertyData.m_ResidentialProperties == 1 ? 2f : buildingPropertyData.CountProperties();
                        lotSize.x = math.select(lotSize.x, maxLotSize.x, lotSize.x == maxLotSize.x - 1 && supportsNarrow);
                        valuePerProperty *= lotSize.x * maxLotSize.y;
                    }
                    else
                    {
                        properties = buildingPropertyData.m_SpaceMultiplier;
                    }

                    float score = ZoneEvaluationUtils.GetScore(evalAreaType, office, availabilities, curvePos, ref zonePreferenceData,
                        isStorage, isStorage ? storageDemands : industrialDemands, buildingPropertyData, pollution, valuePerProperty / properties,
                        estimates, industrialProcesses, resourcePrefabs, ref resourceDataLookup);
                    score = math.select(score, math.max(0f, score) + 1f, minDemand == 0);
                    priority *= score;

                    if (priority <= 0f)
                    {
                        continue;
                    }

                    overallBest = math.max(overallBest, priority);
                    int g = 0;
                    while (g < groups.Length && groups[g].m_HomeZone != spawnableData.m_ZonePrefab)
                    {
                        g++;
                    }

                    if (g == groups.Length)
                    {
                        if (groups.Length < groups.Capacity)
                        {
                            groups.Add(new GroupBest { m_HomeZone = spawnableData.m_ZonePrefab, m_Priority = priority, m_Index = i });
                        }
                    }
                    else if (priority > groups[g].m_Priority)
                    {
                        groups[g] = new GroupBest { m_HomeZone = spawnableData.m_ZonePrefab, m_Priority = priority, m_Index = i };
                    }
                }

                if (groups.Length == 0)
                {
                    return false;
                }

                // Variety across themes. The game's ranking strongly favours designs that fill the
                // lot's depth, so in a zone that mixes themes the theme with the deepest designs took
                // almost every lot (EU row houses, 6 cells deep, over 2 to 4 deep US and French ones).
                // Instead, every home zone whose best design scores at least half the overall best
                // gets an equal chance at the lot, and the game's ranking picks the design within it.
                // The lot keeps the overall best priority, so how lots compete is unchanged.
                int eligible = 0;
                for (int g = 0; g < groups.Length; g++)
                {
                    if (groups[g].m_Priority >= overallBest * kThemeShare)
                    {
                        eligible++;
                    }
                }

                int pick = random.NextInt(eligible);
                for (int g = 0; g < groups.Length; g++)
                {
                    if (groups[g].m_Priority < overallBest * kThemeShare)
                    {
                        continue;
                    }

                    if (pick-- == 0)
                    {
                        location.m_Building = allowed[groups[g].m_Index].m_Building;
                        location.m_AreaType = type;
                        chosenBuildingData = buildingLookup[location.m_Building];
                        location.m_Priority = overallBest;
                        break;
                    }
                }

                bool rightAccessOnly = (chosenBuildingData.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) == 0 &&
                                       ((chosenBuildingData.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) != 0 || random.NextBool());
                if (rightAccessOnly)
                {
                    location.m_LotArea.x = location.m_LotArea.y - chosenBuildingData.m_LotSize.x;
                    location.m_LotArea.w = location.m_LotArea.z + chosenBuildingData.m_LotSize.y;
                }
                else
                {
                    location.m_LotArea.yw = location.m_LotArea.xz + chosenBuildingData.m_LotSize;
                }

                return true;
            }

            private int EvaluateDemandAndAvailability(AreaType areaType, BuildingPropertyData propertyData, ZoneDensity zoneDensity, bool storage)
            {
                switch (areaType)
                {
                    case AreaType.Residential:
                        if (zoneDensity == ZoneDensity.Low)
                        {
                            return residentialDemands.x;
                        }

                        return zoneDensity == ZoneDensity.Medium ? residentialDemands.y : residentialDemands.z;

                    case AreaType.Commercial:
                    {
                        int sum = 0;
                        var iterator = ResourceIterator.GetIterator();
                        while (iterator.Next())
                        {
                            if ((propertyData.m_AllowedSold & iterator.resource) != Resource.NoResource)
                            {
                                sum += commercialDemands[EconomyUtils.GetResourceIndex(iterator.resource)];
                            }
                        }

                        return sum;
                    }

                    case AreaType.Industrial:
                    {
                        int sum = 0;
                        var iterator = ResourceIterator.GetIterator();
                        while (iterator.Next())
                        {
                            if (storage)
                            {
                                if ((propertyData.m_AllowedStored & iterator.resource) != Resource.NoResource)
                                {
                                    sum += storageDemands[EconomyUtils.GetResourceIndex(iterator.resource)];
                                }
                            }
                            else if ((propertyData.m_AllowedManufactured & iterator.resource) != Resource.NoResource)
                            {
                                sum += industrialDemands[EconomyUtils.GetResourceIndex(iterator.resource)];
                            }
                        }

                        return sum;
                    }

                    default:
                        return 0;
                }
            }
        }
    }
}
