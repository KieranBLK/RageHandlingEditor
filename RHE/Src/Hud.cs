using System.Collections.Generic;
using GTA;
using GTA.Math;
using System.Drawing;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        [System.Flags]
        private enum HandlingVisualizationElements
        {
            Disabled = 0,
            Bounding = 1,
            COM = 2,
            RollCenter = 4,
            Submerged = 8,
            AntiRollBarBias = 16,
            CenterOfGravity = 32,
            WheelBounds = 64,
            SuspensionLoad = 128,
            RudderOffsets = 512,
            PropRadius = 1024,
            Impeller = 2048,
            WaterSamples = 8192,
            KeelSpheres = 16384,
            All = Bounding | COM | RollCenter | Submerged | AntiRollBarBias | CenterOfGravity | WheelBounds | SuspensionLoad | RudderOffsets | PropRadius | Impeller | WaterSamples | KeelSpheres,
        }

        // [Bounding Box] Color of the bounding box corner ticks
        private static readonly Color VISUALIZATION_BOX_COLOR = Color.FromArgb(153, 255, 51);

        // [Wheel Bounds] Colors of the main and extra wheel bound rings
        private static readonly Color VISUALIZATION_WHEEL_BOUND_COLOR = Color.FromArgb(153, 255, 51);
        private static readonly Color VISUALIZATION_EXTRA_WHEEL_BOUND_COLOR = Color.FromArgb(120, 180, 255);

        // [Percent Submerged] Colors of the waterline ring, floating and sinking
        private static readonly Color VISUALIZATION_SUBMERGED_COLOR = Color.FromArgb(0, 81, 255);
        private static readonly Color VISUALIZATION_SINKING_COLOR = Color.FromArgb(255, 60, 60);

        // [Center Of Mass And Center Of Gravity] Colors of the 2 centers' axes
        private static readonly Color VISUALIZATION_COM_COLOR = Color.FromArgb(255, 0, 0);
        private static readonly Color VISUALIZATION_CENTER_OF_GRAVITY_COLOR = Color.FromArgb(255, 255, 255);

        // [Anti-Roll Bars] Color of the anti-roll bar lines
        private static readonly Color VISUALIZATION_ANTI_ROLL_BAR_COLOR = Color.FromArgb(245, 203, 19);

        // [Suspension Load] Colors of the suspension load gauges, their level line, and their bias line
        private static readonly Color VISUALIZATION_SUSPENSION_LOAD_COLOR = Color.FromArgb(200, 210, 230);
        private static readonly Color VISUALIZATION_SUSPENSION_LOAD_LEVEL_COLOR = Color.FromArgb(0, 220, 255);
        private static readonly Color VISUALIZATION_SUSPENSION_LOAD_BIAS_COLOR = Color.FromArgb(255, 0, 0);

        // [Roll Centers] Color of the roll center posts
        private static readonly Color VISUALIZATION_ROLL_COLOR = Color.FromArgb(255, 0, 0);

        // [Water Samples] Colors of the water samples of a boat or seaplane, then of the box a boat's sample multipliers shape
        private static readonly Color VISUALIZATION_WATER_SAMPLE_COLOR = Color.FromArgb(150, 200, 255);
        private static readonly Color VISUALIZATION_WATER_SAMPLE_BOX_COLOR = Color.FromArgb(0, 120, 255);

        // [Rudder Offsets] Colors of the rudder offset lines
        // They reuse the Suspension Load colors, since a boat has no wheels to draw gauges at
        private static readonly Color VISUALIZATION_RUDDER_STUB_COLOR = Color.FromArgb(200, 210, 230);
        private static readonly Color VISUALIZATION_RUDDER_SUBMERGE_COLOR = Color.FromArgb(0, 220, 255);
        private static readonly Color VISUALIZATION_RUDDER_FORCE_COLOR = Color.FromArgb(255, 0, 0);

        // [Propeller Radius] Color of the propeller radius cross
        private static readonly Color VISUALIZATION_PROP_RADIUS_COLOR = Color.FromArgb(255, 140, 0);

        // [Keel Spheres] Color of the keel spheres
        // They reuse the anti-roll bar yellow, since a boat has no anti-roll bars to draw
        private static readonly Color VISUALIZATION_KEEL_SPHERE_COLOR = Color.FromArgb(245, 203, 19);

        // [Impeller] Color of the impeller ring and arrow
        private static readonly Color VISUALIZATION_IMPELLER_COLOR = Color.FromArgb(255, 0, 255);

        // Hub bone per wheel, indexed by VehicleWheelBoneId minus WHEEL_BONE_ID_FIRST
        private static readonly string[] WheelHubBoneNames = { "wheel_lf", "wheel_rf", "wheel_lr", "wheel_rr", "wheel_lm1", "wheel_rm1", "wheel_lm2", "wheel_rm2", "wheel_lm3", "wheel_rm3" };
        // The bones whose average position is a boat's thrust point
        private static readonly string[] BoatPropBoneNames = { "static_prop", "static_prop2" };
        // The sine and cosine of each of the VISUALIZATION_RING_SEGMENTS + 1 points around a ring, so finished rings and spokes read a table every frame
        private static readonly float[] RingSegmentSines = BuildRingSegmentTable(true);
        private static readonly float[] RingSegmentCosines = BuildRingSegmentTable(false);
        // One API argument per color component value, reused by every visualizer line, where the API's line call creates a new argument object per component per line
        private static readonly GTA.Native.InputArgument[] ColorComponentArguments = BuildColorComponentArguments();

        private static float[] BuildRingSegmentTable(bool sine)
        {
            var table = new float[VISUALIZATION_RING_SEGMENTS + 1];
            for (int i = 0; i < table.Length; i++)
            {
                double angle = i * 2.0 * System.Math.PI / VISUALIZATION_RING_SEGMENTS;
                table[i] = (float)(sine ? System.Math.Sin(angle) : System.Math.Cos(angle));
            }
            return table;
        }

        private static GTA.Native.InputArgument[] BuildColorComponentArguments()
        {
            var arguments = new GTA.Native.InputArgument[256];
            for (int i = 0; i < arguments.Length; i++) arguments[i] = i;
            return arguments;
        }

        // A build slot's own build seconds const. A new element needs its own case
        // The default case returns a positive duration, so a slot with no element keeps its build at 0 with no division by zero
        private static float ResolveVisualizationBuildSeconds(int slot)
        {
            switch ((HandlingVisualizationElements)(1 << slot))
            {
                case HandlingVisualizationElements.Bounding: return VISUALIZATION_BOUNDING_BUILD_SECONDS;
                case HandlingVisualizationElements.COM: return VISUALIZATION_COM_BUILD_SECONDS;
                case HandlingVisualizationElements.RollCenter: return VISUALIZATION_ROLL_CENTER_BUILD_SECONDS;
                case HandlingVisualizationElements.Submerged: return VISUALIZATION_SUBMERGED_BUILD_SECONDS;
                case HandlingVisualizationElements.AntiRollBarBias: return VISUALIZATION_ANTI_ROLL_BAR_BUILD_SECONDS;
                case HandlingVisualizationElements.CenterOfGravity: return VISUALIZATION_CENTER_OF_GRAVITY_BUILD_SECONDS;
                case HandlingVisualizationElements.WheelBounds: return VISUALIZATION_WHEEL_BOUND_BUILD_SECONDS;
                case HandlingVisualizationElements.SuspensionLoad: return VISUALIZATION_SUSPENSION_LOAD_BUILD_SECONDS;
                case HandlingVisualizationElements.RudderOffsets: return VISUALIZATION_RUDDER_OFFSETS_BUILD_SECONDS;
                case HandlingVisualizationElements.PropRadius: return VISUALIZATION_PROP_RADIUS_BUILD_SECONDS;
                case HandlingVisualizationElements.Impeller: return VISUALIZATION_IMPELLER_BUILD_SECONDS;
                case HandlingVisualizationElements.WaterSamples: return VISUALIZATION_WATER_SAMPLES_BUILD_SECONDS;
                case HandlingVisualizationElements.KeelSpheres: return VISUALIZATION_KEEL_SPHERES_BUILD_SECONDS;
                default: return VISUALIZATION_UNUSED_SLOT_BUILD_SECONDS;
            }
        }

        private readonly IHandlingVisualizer visualizer;

        private GTA.UI.ContainerElement crosshairHorizontal;
        private GTA.UI.ContainerElement crosshairVertical;

        private void DrawCrosshair()
        {
            Color color = !Entity.IsNullOrNotExisting(hoveredVehicleForSelection) ? Color.LimeGreen : Color.Red;
            float centerX = GTA.UI.Screen.Width / 2.0f;
            float centerY = GTA.UI.Screen.Height / 2.0f;
            if (crosshairHorizontal == null)
            {
                crosshairHorizontal = new GTA.UI.ContainerElement(PointF.Empty, new SizeF(CROSSHAIR_SIZE, CROSSHAIR_THICKNESS), color);
                crosshairVertical = new GTA.UI.ContainerElement(PointF.Empty, new SizeF(CROSSHAIR_THICKNESS, CROSSHAIR_SIZE), color);
            }
            crosshairHorizontal.Position = new PointF(centerX - CROSSHAIR_SIZE / 2.0f, centerY - CROSSHAIR_THICKNESS / 2.0f);
            crosshairHorizontal.Color = color;
            crosshairVertical.Position = new PointF(centerX - CROSSHAIR_THICKNESS / 2.0f, centerY - CROSSHAIR_SIZE / 2.0f);
            crosshairVertical.Color = color;
            crosshairHorizontal.Draw();
            crosshairVertical.Draw();
        }

        private interface IHandlingVisualizer
        {
            void DrawHandlingVisualization(int treeIndex, Vehicle vehicle, HandlingData handling, HandlingVisualizationElements shownElements);
            bool HasBuildProgress(int treeIndex);
            void CarryVisualizationStateToRespawn(int oldHandle, int newHandle);
        }

        // Only a draw moves an element's build or glide, since this class owns that state
        private sealed class HandlingVisualizer : IHandlingVisualizer
        {
            private readonly ILogger logger;
            private readonly IModelNameResolver modelNames;
            private readonly System.Func<HandlingVisualizationElements> readShowHandlingVisualization;
            // The elements the setting shows, read once at the start of each draw so every element of one frame agrees
            private HandlingVisualizationElements showHandlingVisualization;
            // The placement inputs of the vehicle drawing now, read once when its draw starts, since each is a native call or a memory read
            // Every point and gauge of one draw shares them: The vehicle's matrix, the camera's position, and how far the vehicle moved in the last frame
            private static int drawVehicleHandle;
            private static Matrix drawMatrix;
            private static Vector3 drawCameraPosition;
            private static Vector3 drawFrameMotion;
            // A wheel's main bound is its lowest-index child and any extra bound follows it. RefreshWheelBoundShapes rereads each type and box per tick
            private struct WheelBoundInfo { public int Wheel; public int Child; public int BoundType; public Vector3 BoxMin; public Vector3 BoxMax; public bool IsExtra; }
            // A water sample's offset is in its bound component's frame
            private struct WaterSampleInfo { public Vector3 Offset; public float Size; public int Component; public WaterSampleKind Kind; }
            // A pontoon sample also carries the keel flag, so Pontoon wins over Keel when a sample's flags are read
            private enum WaterSampleKind { Ordinary, Keel, Pontoon }
            // A sample's center and axes in the vehicle's frame, resolved before any sample draws so each can start by its height. Drawn is False for a skipped sample
            private struct WaterSampleFrame { public bool Drawn; public Vector3 CenterLocal; public Vector3 RightLocal; public Vector3 ForwardLocal; public Vector3 UpLocal; }

            // A tree owns its cache, so 2 trees drawing different vehicles in one tick never rebuild each other's
            private class VisualizationCache
            {
                public int CachedHandle;
                public int CachedModel;
                public Vector3 BoundsMin;
                public Vector3 BoundsMax;
                public VehicleWheel[] Wheels = new VehicleWheel[0];
                // One per VehicleWheelBoneId, from WheelHubBoneNames, and a bone the model lacks reads IsValid False
                public EntityBone[] HubBones = new EntityBone[10];
                // One per BoatPropBoneNames entry, and a bone the model lacks reads IsValid False
                public EntityBone[] PropBones = new EntityBone[2];
                public WheelBoundInfo[] WheelBounds = new WheelBoundInfo[0];
                // Ticks left before RefreshWheelBoundShapes reads the shapes again
                public int TicksUntilShapeRefresh;
                // The samples the vehicle's model loaded with
                public WaterSampleInfo[] WaterSamples = new WaterSampleInfo[0];
                // False until EnsureWaterSamples reads the cached vehicle's samples, so only a vehicle that draws Water Samples reads them
                public bool WaterSamplesRead;
            }

            // Models whose unmatched wheel bound already logged its INFO
            private readonly HashSet<int> wheelBoundNotFoundLoggedModels = new HashSet<int>();
            // Models whose water samples could not be read already logged their INFO
            private readonly HashSet<int> waterSamplesNotFoundLoggedModels = new HashSet<int>();
            // Seaplane models whose readable samples held no pontoon sample already logged their INFO
            private readonly HashSet<int> pontoonSamplesNotFoundLoggedModels = new HashSet<int>();
            // The 3 keel spheres rebuilt each draw from the live handling values, reused so no draw allocates
            private readonly WaterSampleInfo[] liveKeelSpheres = new WaterSampleInfo[3];
            // Scratch for one tick's suspension load pass, indexed like the cache's Wheels
            private float[] suspensionLoadRatioTargets = new float[0];
            private float[] suspensionSpringRatioTargets = new float[0];
            private float[] suspensionLoadWeightShares = new float[0];
            private Vector3[] suspensionLoadHubLocals = new Vector3[0];
            private int[] suspensionLoadAxleOfWheel = new int[0];
            // Scratch for ResolveWheelSides: The side signs (-1 left, 1 right) a wheel's gauge and roll post draw on
            private readonly float[] wheelSides = new float[2];
            // Scratch for DrawWaterSamples, grown to the largest sample count seen: Each sample's frame, and the distinct sample heights lowest first
            private WaterSampleFrame[] waterSampleFrames = new WaterSampleFrame[0];
            private float[] waterSampleLevels = new float[0];

            private class VisualizationAnimationState
            {
                public readonly VisualizationCache Cache = new VisualizationCache();
                // One build per element, indexed by the element's bit position: 0 is fully hidden, 1 fully built
                public float[] Builds = new float[VISUALIZATION_ELEMENT_SLOT_COUNT];
                public int VehicleHandle;
                // Share of the bounds height the waterline sits at, and how far the ring has blended from submerged to sinking
                public float WaterlineFraction;
                public float SinkBlend;
                public float RollCenterHeightFront;
                public float RollCenterHeightRear;
                public float AntiRollBarFrontShare;
                public Vector3 CenterOfMass;
                public Vector3 CenterOfGravity;
                // The boat handling values the rudder, prop, and impeller elements draw, all 0 on a vehicle with no boat handling
                public float RudderOffsetSubmerge;
                public float RudderOffsetForce;
                public float RudderOffsetForceZMultiplier;
                public float PropRadius;
                public float ImpellerOffset;
                public float ImpellerForceMultiplier;
                // The boat handling values the water sample box draws, all 0 on a vehicle with no boat handling
                public float BoxFrontMultiplier;
                public float BoxRearMultiplier;
                public float BoxSideMultiplier;
                public float SampleTop;
                public float SampleBottomTestCorrection;
                // The boat elements whose values were read and glided last frame, so an element that just turned on snaps to its live values instead of gliding from stale ones
                public HandlingVisualizationElements BoatElementsGlided;
                // Indexed like Cache.Wheels, each wheel's suspension load against an even 1
                public float[] SuspensionLoadRatios = new float[0];
                public float[] SuspensionSpringRatios = new float[0];
                // True while the load ratios come from the center of gravity's weight split, False while they fall back to the spring force share
                public bool SuspensionLoadFromWeight;
                // The camera distance to the vehicle's center, corrected for the camera's one frame lag and glided with the other values. 0 until measured
                public float GaugeCameraDistance;
                // True while the vehicle was not visible, so the first drawn frame after it snaps every glided value instead of gliding from where it stopped
                public bool GlidesStale;
            }

            // One per editor tree, indexed by EditorContext.Index
            private readonly VisualizationAnimationState[] treeStates;

            public HandlingVisualizer(ILogger visualizerLogger, IModelNameResolver nameResolver, System.Func<HandlingVisualizationElements> shownElementsReader, int treeCount)
            {
                logger = visualizerLogger;
                modelNames = nameResolver;
                readShowHandlingVisualization = shownElementsReader;
                treeStates = new VisualizationAnimationState[treeCount];
                for (int i = 0; i < treeCount; i++) treeStates[i] = new VisualizationAnimationState();
            }

            public void DrawHandlingVisualization(int treeIndex, Vehicle vehicle, HandlingData handling, HandlingVisualizationElements shownElements)
            {
                showHandlingVisualization = readShowHandlingVisualization();
                DrawHandlingVisualization(vehicle, handling, treeStates[treeIndex], shownElements);
            }

            public bool HasBuildProgress(int treeIndex) { return HasAnyBuild(treeStates[treeIndex]); }

            // A respawned vehicle keeps its tree's element builds and glided values, so a respawn neither restarts the builds nor snaps the glides
            // The tree's cache is not carried. It rebuilds once for the respawned vehicle on the tree's next draw. Called before the old vehicle is deleted
            public void CarryVisualizationStateToRespawn(int oldHandle, int newHandle)
            {
                for (int i = 0; i < treeStates.Length; i++)
                {
                    if (treeStates[i].VehicleHandle == oldHandle) treeStates[i].VehicleHandle = newHandle;
                }
            }

            // modelHash is checked alongside the handle because a freed slot's counter can wrap back to the same integer
            private void EnsureVisualizationCache(VisualizationCache cache, Vehicle vehicle)
            {
                int modelHash = vehicle.Model.Hash;
                if (vehicle.Handle == cache.CachedHandle && modelHash == cache.CachedModel) return;
                cache.CachedHandle = vehicle.Handle;
                cache.CachedModel = modelHash;
                vehicle.Model.GetDimensions(out cache.BoundsMin, out cache.BoundsMax);
                cache.Wheels = vehicle.Wheels.GetAllWheels();
                for (int i = 0; i < WheelHubBoneNames.Length; i++) cache.HubBones[i] = vehicle.Bones[WheelHubBoneNames[i]];
                for (int i = 0; i < BoatPropBoneNames.Length; i++) cache.PropBones[i] = vehicle.Bones[BoatPropBoneNames[i]];
                cache.WheelBounds = new WheelBoundInfo[0];
                cache.WaterSamplesRead = false;
                MatchWheelBounds(cache, vehicle, modelHash);
                cache.TicksUntilShapeRefresh = VISUALIZATION_WHEEL_BOUND_SHAPE_REFRESH_TICKS;
            }

            // The vehicle's composite collision bound, read fresh each call, since the game can swap a vehicle's bound for its own copy (a bike's cloned bounds)
            private static bool ResolveCompositeBound(Vehicle vehicle, out System.IntPtr composite)
            {
                composite = System.IntPtr.Zero;
                if (!RawMemory.RawOffsetsSupported) return false;
                System.IntPtr instance, archetype;
                if (!RawMemory.ReadRawIntPtr(vehicle.MemoryAddress, EntityLayout.PhysicsInstance, out instance) || instance == System.IntPtr.Zero) return false;
                if (!RawMemory.ReadRawIntPtr(instance, PhysicsInstanceLayout.Archetype, out archetype) || archetype == System.IntPtr.Zero) return false;
                if (!RawMemory.ReadRawIntPtr(archetype, ArchetypeLayout.Bound, out composite) || composite == System.IntPtr.Zero) return false;
                int typeBits;
                return RawMemory.ReadRawInt32(composite, BoundLayout.Type, out typeBits) && (typeBits & 0xFF) == BoundLayout.TypeComposite;
            }

            // The game builds the samples once when the model loads, so they hold until Unload Vehicle reloads the model
            // A failed read leaves no samples and logs the step that failed once per model
            // A successful read on seaplane handling that holds no pontoon sample logs once per model, which names a model without pontoon bounds or a wrong pontoon bit
            private void EnsureWaterSamples(VisualizationCache cache, Vehicle vehicle, bool hasSeaPlaneHandling)
            {
                if (cache.WaterSamplesRead) return;
                cache.WaterSamplesRead = true;
                cache.WaterSamples = new WaterSampleInfo[0];
                if (!RawMemory.RawOffsetsSupported) return;
                System.IntPtr modelInfo = System.IntPtr.Zero, buoyancyInfo = System.IntPtr.Zero, samples = System.IntPtr.Zero;
                short count = 0;
                string failedStep = null;
                if (!RawMemory.ReadRawIntPtr(vehicle.MemoryAddress, EntityLayout.ModelInfo, out modelInfo) || modelInfo == System.IntPtr.Zero) failedStep = "Model info";
                else if (!RawMemory.ReadRawIntPtr(modelInfo, ModelInfoLayout.BuoyancyInfo, out buoyancyInfo) || buoyancyInfo == System.IntPtr.Zero) failedStep = "Buoyancy info";
                else if (!RawMemory.ReadRawInt16(buoyancyInfo, BuoyancyInfoLayout.Count, out count) || count < 1 || count > WaterSampleLayout.CountMax) failedStep = "Sample count";
                else if (!RawMemory.ReadRawIntPtr(buoyancyInfo, BuoyancyInfoLayout.Samples, out samples) || samples == System.IntPtr.Zero) failedStep = "Sample array";
                WaterSampleInfo[] read = new WaterSampleInfo[failedStep == null ? count : 0];
                for (int i = 0; i < read.Length; i++)
                {
                    int sampleOffset = i * WaterSampleLayout.Stride;
                    Vector3 offset;
                    float size = 0.0f;
                    short component = 0, flags = 0;
                    if (!RawMemory.ReadRawVector3(samples, sampleOffset, out offset) || !RawMemory.ReadRawFloat(samples, sampleOffset + WaterSampleLayout.Size, out size)
                        || !RawMemory.ReadRawInt16(samples, sampleOffset + WaterSampleLayout.Component, out component) || !RawMemory.ReadRawInt16(samples, sampleOffset + WaterSampleLayout.Flags, out flags))
                    {
                        failedStep = "Sample " + i;
                        break;
                    }
                    WaterSampleKind kind = (flags & WaterSampleLayout.PontoonBit) != 0 ? WaterSampleKind.Pontoon : (flags & WaterSampleLayout.KeelBit) != 0 ? WaterSampleKind.Keel : WaterSampleKind.Ordinary;
                    read[i] = new WaterSampleInfo { Offset = offset, Size = size, Component = (ushort)component, Kind = kind };
                }
                int modelHash = vehicle.Model.Hash;
                if (failedStep == null)
                {
                    cache.WaterSamples = read;
                    if (hasSeaPlaneHandling && !HasPontoonSample(read) && pontoonSamplesNotFoundLoggedModels.Add(modelHash)) logger.Log(Dbg.Level.Info, Dbg.PontoonSamplesNotFound(modelNames.ResolveModelName(vehicle), modelHash, read.Length));
                    return;
                }
                if (waterSamplesNotFoundLoggedModels.Add(modelHash)) logger.Log(Dbg.Level.Info, Dbg.WaterSamplesNotFound(modelNames.ResolveModelName(vehicle), modelHash, failedStep));
            }

            private static bool HasPontoonSample(WaterSampleInfo[] samples)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i].Kind == WaterSampleKind.Pontoon) return true;
                }
                return false;
            }

            // A composite child's current matrix, in the vehicle's own frame, read as one span
            private static bool ReadCompositeMatrix(System.IntPtr composite, int child, out Vector3 right, out Vector3 forward, out Vector3 up, out Vector3 position)
            {
                right = forward = up = position = Vector3.Zero;
                System.IntPtr matrices;
                if (!RawMemory.ReadRawIntPtr(composite, CompositeBoundLayout.CurrentMatrices, out matrices) || matrices == System.IntPtr.Zero) return false;
                return RawMemory.ReadRawMatrixColumns(matrices, child * CompositeBoundLayout.MatrixStride, CompositeBoundLayout.MatrixColumnStride, out right, out forward, out up, out position);
            }

            // Pass 1: A disc or cylinder child belongs to the nearest hub that is within the child's own reach, measured across the vehicle's X and Y
            // A wheel's lowest-index child there is its main bound
            // Pass 2: Each remaining child goes to the nearest hub with a main bound within VISUALIZATION_WHEEL_BOUND_EXTRA_PUSH_RADII of that bound's radius
            // A thin extra is pushed out farther than its own reach. A wheel whose collision is a mesh (a helicopter skid's suspension bound) has none and is not drawn
            private void MatchWheelBounds(VisualizationCache cache, Vehicle vehicle, int modelHash)
            {
                System.IntPtr composite;
                if (!ResolveCompositeBound(vehicle, out composite)) return;
                short childCount;
                System.IntPtr childBounds;
                if (!RawMemory.ReadRawInt16(composite, CompositeBoundLayout.ChildCount, out childCount) || !RawMemory.ReadRawIntPtr(composite, CompositeBoundLayout.ChildBounds, out childBounds) || childBounds == System.IntPtr.Zero) return;
                if (childCount <= 0 || childCount > COMPOSITE_CHILD_COUNT_MAX) return;
                int wheelCount = cache.Wheels.Length;
                Vector3[] hubLocals = new Vector3[wheelCount];
                bool[] hubFound = new bool[wheelCount];
                bool[] wheelHasMain = new bool[wheelCount];
                for (int i = 0; i < wheelCount; i++)
                {
                    EntityBone hub = ResolveWheelHub(cache, cache.Wheels[i]);
                    hubFound[i] = hub != null;
                    if (hub != null) hubLocals[i] = vehicle.GetPositionOffset(hub.Position);
                }
                List<WheelBoundInfo> matched = new List<WheelBoundInfo>();
                List<WheelBoundInfo> unmatched = new List<WheelBoundInfo>();
                List<Vector3> unmatchedPositions = new List<Vector3>();
                for (int child = 0; child < childCount; child++)
                {
                    System.IntPtr childBound;
                    int typeBits;
                    Vector3 boxMin, boxMax, right, forward, up, position;
                    if (!RawMemory.ReadRawIntPtr(childBounds, child * System.IntPtr.Size, out childBound) || childBound == System.IntPtr.Zero) continue;
                    if (!RawMemory.ReadRawInt32(childBound, BoundLayout.Type, out typeBits)) continue;
                    int boundType = typeBits & 0xFF;
                    if (boundType != BoundLayout.TypeDisc && boundType != BoundLayout.TypeCylinder) continue;
                    if (!RawMemory.ReadRawVector3(childBound, BoundLayout.BoxMin, out boxMin) || !RawMemory.ReadRawVector3(childBound, BoundLayout.BoxMax, out boxMax)) continue;
                    if (!ReadCompositeMatrix(composite, child, out right, out forward, out up, out position)) continue;
                    Vector3 span = boxMax - boxMin;
                    float reach = System.Math.Max(span.X, System.Math.Max(span.Y, span.Z)) * 0.5f;
                    int nearestWheel = -1;
                    float nearestDistanceSquared = float.MaxValue;
                    for (int i = 0; i < wheelCount; i++)
                    {
                        if (!hubFound[i]) continue;
                        float distanceX = position.X - hubLocals[i].X;
                        float distanceY = position.Y - hubLocals[i].Y;
                        float distanceSquared = distanceX * distanceX + distanceY * distanceY;
                        if (distanceSquared > reach * reach || distanceSquared >= nearestDistanceSquared) continue;
                        nearestWheel = i;
                        nearestDistanceSquared = distanceSquared;
                    }
                    if (nearestWheel < 0)
                    {
                        unmatched.Add(new WheelBoundInfo { Wheel = -1, Child = child, BoundType = boundType, BoxMin = boxMin, BoxMax = boxMax, IsExtra = true });
                        unmatchedPositions.Add(position);
                        continue;
                    }
                    matched.Add(new WheelBoundInfo { Wheel = nearestWheel, Child = child, BoundType = boundType, BoxMin = boxMin, BoxMax = boxMax, IsExtra = wheelHasMain[nearestWheel] });
                    wheelHasMain[nearestWheel] = true;
                }
                // The radius of each wheel's main bound sets how far its pushed out extras can sit from the hub
                float[] mainRadius = new float[wheelCount];
                for (int i = 0; i < matched.Count; i++)
                {
                    if (matched[i].IsExtra) continue;
                    float radius, halfWidth;
                    ResolveWheelBoundSize(matched[i], out radius, out halfWidth);
                    mainRadius[matched[i].Wheel] = radius;
                }
                for (int i = 0; i < unmatched.Count; i++)
                {
                    int nearestWheel = -1;
                    float nearestDistanceSquared = float.MaxValue;
                    for (int wheel = 0; wheel < wheelCount; wheel++)
                    {
                        if (!wheelHasMain[wheel]) continue;
                        float distanceX = unmatchedPositions[i].X - hubLocals[wheel].X;
                        float distanceY = unmatchedPositions[i].Y - hubLocals[wheel].Y;
                        float distanceSquared = distanceX * distanceX + distanceY * distanceY;
                        float pushedReach = VISUALIZATION_WHEEL_BOUND_EXTRA_PUSH_RADII * mainRadius[wheel];
                        if (distanceSquared > pushedReach * pushedReach || distanceSquared >= nearestDistanceSquared) continue;
                        nearestWheel = wheel;
                        nearestDistanceSquared = distanceSquared;
                    }
                    if (nearestWheel < 0) continue;
                    WheelBoundInfo extra = unmatched[i];
                    extra.Wheel = nearestWheel;
                    matched.Add(extra);
                }
                cache.WheelBounds = matched.ToArray();
                for (int i = 0; i < wheelCount; i++)
                {
                    if (!hubFound[i] || wheelHasMain[i]) continue;
                    // A mesh collision wheel has no disc or cylinder bound, so this is expected for helicopter skids
                    if (wheelBoundNotFoundLoggedModels.Add(modelHash)) logger.Log(Dbg.Level.Info, Dbg.WheelBoundNotFound(modelNames.ResolveModelName(vehicle), modelHash, cache.Wheels[i].BoneId.ToString()));
                }
            }

            // A wheel bound's radius and half width from its own-frame box: A disc lies flat across its X axis, a cylinder runs along its Y axis
            private static void ResolveWheelBoundSize(WheelBoundInfo bound, out float radius, out float halfWidth)
            {
                Vector3 span = bound.BoxMax - bound.BoxMin;
                if (bound.BoundType == BoundLayout.TypeCylinder)
                {
                    radius = span.X * 0.5f;
                    halfWidth = span.Y * 0.5f;
                }
                else
                {
                    radius = span.Z * 0.5f;
                    halfWidth = span.X * 0.5f;
                }
            }

            // The game changes a wheel bound's type and box while the vehicle exists (HF_ALT_EXT_WHEEL_BOUNDS_SHRINK thins an extra bound when it is set)
            // A read that fails, or a child that is no longer a disc or cylinder, keeps the last good values
            private static void RefreshWheelBoundShape(System.IntPtr childBounds, short childCount, ref WheelBoundInfo bound)
            {
                if (bound.Child >= childCount) return;
                System.IntPtr childBound;
                int typeBits;
                Vector3 boxMin, boxMax;
                if (!RawMemory.ReadRawIntPtr(childBounds, bound.Child * System.IntPtr.Size, out childBound) || childBound == System.IntPtr.Zero) return;
                if (!RawMemory.ReadRawInt32(childBound, BoundLayout.Type, out typeBits)) return;
                int boundType = typeBits & 0xFF;
                if (boundType != BoundLayout.TypeDisc && boundType != BoundLayout.TypeCylinder) return;
                if (!RawMemory.ReadRawVector3(childBound, BoundLayout.BoxMin, out boxMin) || !RawMemory.ReadRawVector3(childBound, BoundLayout.BoxMax, out boxMax)) return;
                bound.BoundType = boundType;
                bound.BoxMin = boxMin;
                bound.BoxMax = boxMax;
            }

            // A refresh every VISUALIZATION_WHEEL_BOUND_SHAPE_REFRESH_TICKS shows a changed shape within a fraction of a second, at a fraction of a per-frame read's cost
            private void RefreshWheelBoundShapes(VisualizationCache cache, Vehicle vehicle)
            {
                if (cache.WheelBounds.Length == 0 || --cache.TicksUntilShapeRefresh > 0) return;
                cache.TicksUntilShapeRefresh = VISUALIZATION_WHEEL_BOUND_SHAPE_REFRESH_TICKS;
                System.IntPtr composite;
                if (!ResolveCompositeBound(vehicle, out composite)) return;
                short childCount;
                System.IntPtr childBounds;
                if (!RawMemory.ReadRawInt16(composite, CompositeBoundLayout.ChildCount, out childCount) || !RawMemory.ReadRawIntPtr(composite, CompositeBoundLayout.ChildBounds, out childBounds) || childBounds == System.IntPtr.Zero) return;
                for (int i = 0; i < cache.WheelBounds.Length; i++) RefreshWheelBoundShape(childBounds, childCount, ref cache.WheelBounds[i]);
            }

            // The hub bone of a wheel, or null when the wheel's id is outside the table or the model lacks the bone
            private EntityBone ResolveWheelHub(VisualizationCache cache, VehicleWheel wheel)
            {
                int index = (int)wheel.BoneId - WHEEL_BONE_ID_FIRST;
                if (index < 0 || index >= cache.HubBones.Length) return null;
                EntityBone hub = cache.HubBones[index];
                return hub != null && hub.IsValid ? hub : null;
            }

            // Smoothstep, so a build starts and ends gently
            private static float EaseBuildProgress(float progress) { return progress * progress * (3.0f - 2.0f * progress); }
            // How far one step of a build has progressed: 0 until build reaches start, 1 from end on, linear between
            private static float BuildPhase(float build, float start, float end) { return System.Math.Max(0.0f, System.Math.Min(1.0f, (build - start) / (end - start))); }
            // -1 for a point left of the centerline, 1 otherwise
            private static float OutboardSide(Vector3 hubLocal) { return hubLocal.X < 0.0f ? -1.0f : 1.0f; }
            // The vehicle-local X of the bounding box side on side (-1 left, 1 right)
            private static float ResolveSidePlaneX(float side, Vector3 min, Vector3 max) { return side < 0.0f ? min.X : max.X; }

            // A wheel near the centerline draws its gauge and roll post on both sides, since its live X would otherwise flip it between sides with every suspension jitter
            private int ResolveWheelSides(Vector3 hubLocal)
            {
                if (System.Math.Abs(hubLocal.X) < VISUALIZATION_CENTERED_WHEEL_EPSILON)
                {
                    wheelSides[0] = -1.0f;
                    wheelSides[1] = 1.0f;
                    return 2;
                }
                wheelSides[0] = OutboardSide(hubLocal);
                return 1;
            }

            private static float Glide(float current, float target, float blend) { return current + (target - current) * blend; }
            private static Vector3 Glide(Vector3 current, Vector3 target, float blend) { return current + (target - current) * blend; }

            private static Color BlendColor(Color from, Color to, float blend)
            {
                return Color.FromArgb((int)(from.R + (to.R - from.R) * blend), (int)(from.G + (to.G - from.G) * blend), (int)(from.B + (to.B - from.B) * blend));
            }

            // Every visualizer line goes through here
            // It calls the native with the reused color arguments, since World.DrawLine creates an argument object for each of its 10 values on every line
            private static void DrawLine(Vector3 from, Vector3 to, Color color)
            {
                GTA.Native.Function.Call(GTA.Native.Hash.DRAW_LINE, from.X, from.Y, from.Z, to.X, to.Y, to.Z, ColorComponentArguments[color.R], ColorComponentArguments[color.G], ColorComponentArguments[color.B], ColorComponentArguments[color.A]);
            }

            private static void DrawGrowingLine(Vector3 anchor, Vector3 end, float progress, Color color)
            {
                if (progress <= 0.0f) return;
                DrawLine(anchor, anchor + (end - anchor) * progress, color);
            }

            // A finished line draws whole, since 2 halves meeting end to end leave a dark dot where they join
            private static void DrawLineFromMiddle(Vector3 from, Vector3 to, float progress, Color color)
            {
                if (progress >= 1.0f)
                {
                    DrawLine(from, to, color);
                    return;
                }
                Vector3 middle = (from + to) * 0.5f;
                DrawGrowingLine(middle, from, progress, color);
                DrawGrowingLine(middle, to, progress, color);
            }

            // A finished line draws whole, like DrawLineFromMiddle
            private static void DrawLineFromEnds(Vector3 from, Vector3 to, float progress, Color color)
            {
                if (progress >= 1.0f)
                {
                    DrawLine(from, to, color);
                    return;
                }
                Vector3 middle = (from + to) * 0.5f;
                DrawGrowingLine(from, middle, progress, color);
                DrawGrowingLine(to, middle, progress, color);
            }

            // The bounding sphere test alone passes a vehicle behind a building, and the occlusion test alone passes one outside the view
            // Neither catches a vehicle the game does not render
            private static bool IsVehicleVisible(Vehicle vehicle) { return vehicle.IsOnScreen && vehicle.IsRendered && !vehicle.IsOccluded; }

            // The build slot of an element is its flag's bit position
            public static int BuildSlot(HandlingVisualizationElements element)
            {
                int slot = 0;
                for (int value = (int)element; value > 1; value >>= 1) slot++;
                return slot;
            }

            private static bool HasAnyBuild(VisualizationAnimationState state)
            {
                for (int slot = 0; slot < state.Builds.Length; slot++) if (state.Builds[slot] > 0.0f) return true;
                return false;
            }

            // The eased build of one element, 0 while it is hidden and 1 once it has built in
            private static float ElementBuild(VisualizationAnimationState state, HandlingVisualizationElements element) { return EaseBuildProgress(state.Builds[BuildSlot(element)]); }

            // Moves each element's build toward 1 while the element is shown and toward 0 otherwise, or straight onto that target when snap is set
            // Each slot steps by the frame time over its own build duration, shortened toward VISUALIZATION_FAST_BUILD_MIN_SECONDS by speedBlend (0 to 1)
            private void AdvanceBuilds(VisualizationAnimationState state, HandlingVisualizationElements shownElements, float deltaTime, bool snap, float speedBlend)
            {
                for (int slot = 0; slot < state.Builds.Length; slot++)
                {
                    bool shown = ((int)shownElements & (1 << slot)) != 0;
                    if (snap) state.Builds[slot] = shown ? 1.0f : 0.0f;
                    else
                    {
                        float duration = ResolveVisualizationBuildSeconds(slot);
                        if (duration > VISUALIZATION_FAST_BUILD_MIN_SECONDS) duration = Glide(duration, VISUALIZATION_FAST_BUILD_MIN_SECONDS, speedBlend);
                        float step = deltaTime / duration;
                        state.Builds[slot] = System.Math.Max(0.0f, System.Math.Min(1.0f, state.Builds[slot] + (shown ? step : -step)));
                    }
                }
            }

            // Called every tick its tree has a visible menu, and after the last one closes until the elements finish building back out
            // shownElements picks which elements build in and which build out
            private void DrawHandlingVisualization(Vehicle vehicle, HandlingData handling, VisualizationAnimationState state, HandlingVisualizationElements shownElements)
            {
                // A different vehicle builds in from nothing, and a respawned one was carried over (CarryVisualizationStateToRespawn) and keeps its builds
                if (vehicle.Handle != state.VehicleHandle)
                {
                    state.VehicleHandle = vehicle.Handle;
                    System.Array.Clear(state.Builds, 0, state.Builds.Length);
                }
                // A vehicle that is not visible skips every glide and draw. Each build takes the value it would have reached had drawing continued
                // Its glides snap on the first visible frame, so the visualizer is fully drawn the moment the vehicle returns to view
                if (!IsVehicleVisible(vehicle))
                {
                    AdvanceBuilds(state, shownElements, 0.0f, true, 0.0f);
                    state.GlidesStale = true;
                    return;
                }
                float deltaTime = Game.LastFrameTime;
                // A draw starting with no element built and the first visible frame after a hidden one snap every value, so nothing glides from stale positions
                bool snapGlides = !HasAnyBuild(state) || state.GlidesStale;
                state.GlidesStale = false;
                float speedBlend = BuildPhase(vehicle.Speed * MS_TO_MPH, VISUALIZATION_FAST_BUILD_START_MPH, VISUALIZATION_FAST_BUILD_FULL_MPH);
                AdvanceBuilds(state, shownElements, deltaTime, false, speedBlend);
                if (!HasAnyBuild(state)) return;
                VisualizationCache cache = state.Cache;
                EnsureVisualizationCache(cache, vehicle);
                drawVehicleHandle = vehicle.Handle;
                drawMatrix = vehicle.Matrix;
                drawCameraPosition = GameplayCamera.Position;
                drawFrameMotion = vehicle.Velocity * deltaTime;
                if (state.Builds[BuildSlot(HandlingVisualizationElements.WheelBounds)] > 0.0f) RefreshWheelBoundShapes(cache, vehicle);
                UpdateVisualizationGlides(vehicle, handling, state, snapGlides ? 1.0f : 1.0f - (float)System.Math.Exp(-deltaTime / VISUALIZATION_GLIDE_TIME_CONSTANT));
                Vector3 min = cache.BoundsMin;
                Vector3 max = cache.BoundsMax;
                float boundingBuild = ElementBuild(state, HandlingVisualizationElements.Bounding);
                float centerOfMassBuild = ElementBuild(state, HandlingVisualizationElements.COM);
                float centerOfGravityBuild = ElementBuild(state, HandlingVisualizationElements.CenterOfGravity);
                if (boundingBuild > 0.0f) DrawBoundingBrackets(vehicle, min, max, boundingBuild);
                // While the center of gravity matches the center of mass only its axes draw, and it draws last so it sits over the center of mass axes when they diverge
                // A door event resets the live center of gravity
                bool centersMatch = Vector3.Distance(handling.CenterOfMassOffset, vehicle.CenterOfGravityOffset) <= VISUALIZATION_COM_COG_MATCH_EPSILON;
                bool drawCenterOfMassAxes = centerOfMassBuild > 0.0f && !(centerOfGravityBuild > 0.0f && centersMatch);
                if (drawCenterOfMassAxes) DrawAxes(vehicle, state.CenterOfMass, min, max, centerOfMassBuild, VISUALIZATION_COM_COLOR);
                if (centerOfGravityBuild > 0.0f) DrawAxes(vehicle, state.CenterOfGravity, min, max, centerOfGravityBuild, VISUALIZATION_CENTER_OF_GRAVITY_COLOR);
                float build = ElementBuild(state, HandlingVisualizationElements.RollCenter);
                if (build > 0.0f) DrawRollCenterPosts(vehicle, state, min, max, build);
                build = ElementBuild(state, HandlingVisualizationElements.AntiRollBarBias);
                if (build > 0.0f) DrawAntiRollBars(vehicle, handling, state, min, max, build);
                build = ElementBuild(state, HandlingVisualizationElements.Submerged);
                if (build > 0.0f) DrawWaterline(vehicle, state, min, max, boundingBuild > 0.0f, build);
                build = ElementBuild(state, HandlingVisualizationElements.WheelBounds);
                if (build > 0.0f) DrawWheelBounds(cache, vehicle, build);
                build = ElementBuild(state, HandlingVisualizationElements.SuspensionLoad);
                if (build > 0.0f) DrawSuspensionLoadGauges(vehicle, state, min, max, build);
                bool hasSeaPlaneHandling = handling.SeaPlaneHandlingData != null;
                // Water Samples covers the sample box and samples of a boat and the pontoon samples of a seaplane, all on one build
                float waterSamplesBuild = ElementBuild(state, HandlingVisualizationElements.WaterSamples);
                // Resolved at most once per frame, by the first sample element that draws
                System.IntPtr composite = System.IntPtr.Zero;
                bool compositeResolved = false;
                if (waterSamplesBuild > 0.0f && hasSeaPlaneHandling && ResolveCompositeBound(vehicle, out composite))
                {
                    compositeResolved = true;
                    EnsureWaterSamples(cache, vehicle, hasSeaPlaneHandling);
                    DrawWaterSamples(vehicle, composite, cache.WaterSamples, WaterSampleKind.Pontoon, waterSamplesBuild, VISUALIZATION_WATER_SAMPLE_COLOR);
                }
                // A vehicle with no boat handling has no thrust point, propeller, impeller, boat water samples, or keel spheres to show
                if (handling.BoatHandlingData == null) return;
                build = ElementBuild(state, HandlingVisualizationElements.RudderOffsets);
                if (build > 0.0f) DrawRudderOffsets(vehicle, state, min, build);
                build = ElementBuild(state, HandlingVisualizationElements.PropRadius);
                if (build > 0.0f) DrawPropRadiusCross(vehicle, state, min, build);
                build = ElementBuild(state, HandlingVisualizationElements.Impeller);
                if (build > 0.0f) DrawImpeller(vehicle, handling, state, min, max, build);
                if (waterSamplesBuild > 0.0f) DrawWaterSampleBox(vehicle, state, min, max, waterSamplesBuild);
                float keelSpheresBuild = ElementBuild(state, HandlingVisualizationElements.KeelSpheres);
                if (waterSamplesBuild <= 0.0f && keelSpheresBuild <= 0.0f) return;
                if (!compositeResolved && !ResolveCompositeBound(vehicle, out composite)) return;
                if (waterSamplesBuild > 0.0f)
                {
                    EnsureWaterSamples(cache, vehicle, hasSeaPlaneHandling);
                    DrawWaterSamples(vehicle, composite, cache.WaterSamples, WaterSampleKind.Ordinary, waterSamplesBuild, VISUALIZATION_WATER_SAMPLE_COLOR);
                }
                if (keelSpheresBuild > 0.0f && ResolveLiveKeelSpheres(handling.BoatHandlingData, state, min, max)) DrawWaterSamples(vehicle, composite, liveKeelSpheres, WaterSampleKind.Keel, keelSpheresBuild, VISUALIZATION_KEEL_SPHERE_COLOR);
            }

            // Places the keel spheres the way the game does at model load, but from the live values so they follow edits
            // The 3 spheres sit on the centerline at the front extent times BoxFrontMultiplier, the origin, and the rear extent times BoxRearMultiplier
            // False when KeelSphereSize is 0 or less, since the game then adds none
            private bool ResolveLiveKeelSpheres(BoatHandlingData boat, VisualizationAnimationState state, Vector3 min, Vector3 max)
            {
                float size = boat.KeelSphereSize;
                if (size <= 0.0f) return false;
                liveKeelSpheres[0] = new WaterSampleInfo { Offset = new Vector3(0.0f, max.Y * state.BoxFrontMultiplier, 0.0f), Size = size, Kind = WaterSampleKind.Keel };
                liveKeelSpheres[1] = new WaterSampleInfo { Offset = Vector3.Zero, Size = size, Kind = WaterSampleKind.Keel };
                liveKeelSpheres[2] = new WaterSampleInfo { Offset = new Vector3(0.0f, min.Y * state.BoxRearMultiplier, 0.0f), Size = size, Kind = WaterSampleKind.Keel };
                return true;
            }

            // A right and up pair facing the gameplay camera, which the suspension load gauges draw in, so they stay readable from any angle
            // When the camera looks almost straight down or up, the vehicle's right side stands in for the across direction
            private static void ResolveCameraFacingBasis(Vehicle vehicle, out Vector3 across, out Vector3 upward)
            {
                Vector3 cameraDirection = GameplayCamera.Direction;
                across = Vector3.Cross(cameraDirection, Vector3.WorldUp);
                across = across.Length() < VISUALIZATION_GAUGE_FACING_EPSILON ? vehicle.RightVector : across.Normalized;
                upward = Vector3.Cross(across, cameraDirection).Normalized;
            }

            // A blend of 1 snaps every glided value to its live target
            private void UpdateVisualizationGlides(Vehicle vehicle, HandlingData handling, VisualizationAnimationState state, float blend)
            {
                float percentSubmerged = handling.PercentSubmerged;
                bool sinking = percentSubmerged > 100.0f;
                state.WaterlineFraction = Glide(state.WaterlineFraction, sinking ? 1.0f : System.Math.Max(0.0f, percentSubmerged / 100.0f), blend);
                state.SinkBlend = Glide(state.SinkBlend, sinking ? 1.0f : 0.0f, blend);
                state.RollCenterHeightFront = Glide(state.RollCenterHeightFront, handling.RollCenterHeightFront, blend);
                state.RollCenterHeightRear = Glide(state.RollCenterHeightRear, handling.RollCenterHeightRear, blend);
                state.AntiRollBarFrontShare = Glide(state.AntiRollBarFrontShare, handling.AntiRollBarBiasFront / BIAS_FRONT_SCALE, blend);
                state.CenterOfMass = Glide(state.CenterOfMass, handling.CenterOfMassOffset, blend);
                state.CenterOfGravity = Glide(state.CenterOfGravity, vehicle.CenterOfGravityOffset, blend);
                BoatHandlingData boat = handling.BoatHandlingData;
                // Only the values an enabled boat element draws are read. An element that was off snaps on its first frame, since nothing kept its values current
                HandlingVisualizationElements boatElements = boat != null ? showHandlingVisualization & VISUALIZATION_BOAT_ELEMENTS : HandlingVisualizationElements.Disabled;
                HandlingVisualizationElements boatJustEnabled = boatElements & ~state.BoatElementsGlided;
                state.BoatElementsGlided = boatElements;
                float groupBlend;
                if (ResolveBoatGroupBlend(VISUALIZATION_THRUST_POINT_ELEMENTS, boatElements, boatJustEnabled, blend, out groupBlend))
                {
                    state.RudderOffsetSubmerge = Glide(state.RudderOffsetSubmerge, boat.RudderOffsetSubmerge, groupBlend);
                    state.RudderOffsetForceZMultiplier = Glide(state.RudderOffsetForceZMultiplier, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.RudderOffsetForceZMultiplier), groupBlend);
                }
                if (ResolveBoatGroupBlend(HandlingVisualizationElements.RudderOffsets, boatElements, boatJustEnabled, blend, out groupBlend)) state.RudderOffsetForce = Glide(state.RudderOffsetForce, boat.RudderOffsetForce, groupBlend);
                if (ResolveBoatGroupBlend(HandlingVisualizationElements.PropRadius, boatElements, boatJustEnabled, blend, out groupBlend)) state.PropRadius = Glide(state.PropRadius, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.PropRadius), groupBlend);
                if (ResolveBoatGroupBlend(HandlingVisualizationElements.Impeller, boatElements, boatJustEnabled, blend, out groupBlend))
                {
                    state.ImpellerOffset = Glide(state.ImpellerOffset, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.ImpellerOffset), groupBlend);
                    state.ImpellerForceMultiplier = Glide(state.ImpellerForceMultiplier, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.ImpellerForceMultiplier), groupBlend);
                }
                if (ResolveBoatGroupBlend(VISUALIZATION_WATER_BOX_ELEMENTS, boatElements, boatJustEnabled, blend, out groupBlend))
                {
                    state.BoxFrontMultiplier = Glide(state.BoxFrontMultiplier, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.BoxFrontMultiplier), groupBlend);
                    state.BoxRearMultiplier = Glide(state.BoxRearMultiplier, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.BoxRearMultiplier), groupBlend);
                }
                if (ResolveBoatGroupBlend(HandlingVisualizationElements.WaterSamples, boatElements, boatJustEnabled, blend, out groupBlend))
                {
                    state.BoxSideMultiplier = Glide(state.BoxSideMultiplier, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.BoxSideMultiplier), groupBlend);
                    state.SampleTop = Glide(state.SampleTop, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.SampleTop), groupBlend);
                    state.SampleBottomTestCorrection = Glide(state.SampleBottomTestCorrection, RawMemory.ReadRawFloat(boat.MemoryAddress, BoatLayout.SampleBottomTestCorrection), groupBlend);
                }
                if ((showHandlingVisualization & HandlingVisualizationElements.SuspensionLoad) != 0)
                {
                    VisualizationCache cache = state.Cache;
                    float measuredCenterDistance = MeasureCameraDistance(vehicle, ToWorld(vehicle, (cache.BoundsMin + cache.BoundsMax) * 0.5f));
                    state.GaugeCameraDistance = state.GaugeCameraDistance <= 0.0f ? measuredCenterDistance : Glide(state.GaugeCameraDistance, measuredCenterDistance, blend);
                    int wheelCount = cache.Wheels.Length;
                    float ratioBlend = blend;
                    // A different wheel count belongs to a different model, so its ratios snap instead of gliding from the old model's
                    if (state.SuspensionLoadRatios.Length != wheelCount)
                    {
                        state.SuspensionLoadRatios = new float[wheelCount];
                        state.SuspensionSpringRatios = new float[wheelCount];
                        ratioBlend = 1.0f;
                    }
                    state.SuspensionLoadFromWeight = ResolveSuspensionLoadRatios(cache, vehicle);
                    for (int i = 0; i < wheelCount; i++)
                    {
                        state.SuspensionLoadRatios[i] = Glide(state.SuspensionLoadRatios[i], suspensionLoadRatioTargets[i], ratioBlend);
                        state.SuspensionSpringRatios[i] = Glide(state.SuspensionSpringRatios[i], suspensionSpringRatioTargets[i], ratioBlend);
                    }
                }
                else
                {
                    // With the element off nothing keeps these current, so they are cleared, and the next time it turns on they snap
                    state.GaugeCameraDistance = 0.0f;
                    if (state.SuspensionLoadRatios.Length != 0)
                    {
                        state.SuspensionLoadRatios = new float[0];
                        state.SuspensionSpringRatios = new float[0];
                    }
                }
            }

            private static bool ResolveBoatGroupBlend(HandlingVisualizationElements group, HandlingVisualizationElements enabled, HandlingVisualizationElements justEnabled, float blend, out float groupBlend)
            {
                groupBlend = (justEnabled & group) != 0 ? 1.0f : blend;
                return (enabled & group) != 0;
            }

            // A wheel's gauge value is its share against the average across the wheels with a spring, so an even load reads 1
            // The share is of the weight the center of gravity puts on the wheel once the weight split resolves, else of the resting spring force (StaticForce)
            // A wheel with no spring force reads 0. suspensionSpringRatioTargets gets the spring force share on the same scale, which the bias line reaches to
            // Returns True when the weight split resolved, so the gauges can show the spring force fallback in the bias color
            private bool ResolveSuspensionLoadRatios(VisualizationCache cache, Vehicle vehicle)
            {
                int wheelCount = cache.Wheels.Length;
                if (suspensionLoadRatioTargets.Length != wheelCount)
                {
                    suspensionLoadRatioTargets = new float[wheelCount];
                    suspensionSpringRatioTargets = new float[wheelCount];
                    suspensionLoadWeightShares = new float[wheelCount];
                    suspensionLoadHubLocals = new Vector3[wheelCount];
                    suspensionLoadAxleOfWheel = new int[wheelCount];
                }
                float totalForce = 0.0f;
                for (int i = 0; i < wheelCount; i++) totalForce += cache.Wheels[i].StaticForce;
                bool useWeight = totalForce > 0.0f && ResolveWheelWeightShares(cache, vehicle);
                float sum = 0.0f;
                int springWheels = 0;
                for (int i = 0; i < wheelCount; i++)
                {
                    float springShare = totalForce > 0.0f ? cache.Wheels[i].StaticForce / totalForce : 0.0f;
                    float value = springShare <= 0.0f ? 0.0f : (useWeight ? suspensionLoadWeightShares[i] : springShare);
                    suspensionLoadRatioTargets[i] = value;
                    suspensionSpringRatioTargets[i] = springShare;
                    if (springShare > 0.0f)
                    {
                        sum += value;
                        springWheels++;
                    }
                }
                float average = springWheels > 0 ? sum / springWheels : 0.0f;
                float springAverage = springWheels > 0 ? 1.0f / springWheels : 0.0f;
                for (int i = 0; i < wheelCount; i++)
                {
                    suspensionLoadRatioTargets[i] = average > 0.0f ? suspensionLoadRatioTargets[i] / average : 0.0f;
                    suspensionSpringRatioTargets[i] = springAverage > 0.0f ? suspensionSpringRatioTargets[i] / springAverage : 0.0f;
                }
                return useWeight;
            }

            // Splits the weight by the lever rule in the model frame, like the game's own weight balance
            // The front axle carries (cog.Y - rear.Y) / (front.Y - rear.Y) of the weight, and an axle's 2 wheels split their part the same way across X
            // False unless every hub resolves and the wheels sit on exactly 2 axles of at most 2 wheels each (axles grouped by VISUALIZATION_AXLE_GROUP_EPSILON)
            private bool ResolveWheelWeightShares(VisualizationCache cache, Vehicle vehicle)
            {
                int wheelCount = cache.Wheels.Length;
                float firstAxleY = 0.0f, secondAxleY = 0.0f;
                int axleCount = 0;
                for (int i = 0; i < wheelCount; i++)
                {
                    EntityBone hub = ResolveWheelHub(cache, cache.Wheels[i]);
                    if (hub == null) return false;
                    Vector3 hubLocal = vehicle.GetPositionOffset(hub.Position);
                    suspensionLoadHubLocals[i] = hubLocal;
                    if (axleCount > 0 && System.Math.Abs(hubLocal.Y - firstAxleY) <= VISUALIZATION_AXLE_GROUP_EPSILON) suspensionLoadAxleOfWheel[i] = 0;
                    else if (axleCount > 1 && System.Math.Abs(hubLocal.Y - secondAxleY) <= VISUALIZATION_AXLE_GROUP_EPSILON) suspensionLoadAxleOfWheel[i] = 1;
                    else if (axleCount == 0)
                    {
                        firstAxleY = hubLocal.Y;
                        axleCount = 1;
                        suspensionLoadAxleOfWheel[i] = 0;
                    }
                    else if (axleCount == 1)
                    {
                        secondAxleY = hubLocal.Y;
                        axleCount = 2;
                        suspensionLoadAxleOfWheel[i] = 1;
                    }
                    else return false;
                }
                if (axleCount != 2) return false;
                Vector3 centerOfGravity = vehicle.CenterOfGravityOffset;
                int frontAxle = firstAxleY > secondAxleY ? 0 : 1;
                float frontY = frontAxle == 0 ? firstAxleY : secondAxleY;
                float rearY = frontAxle == 0 ? secondAxleY : firstAxleY;
                float frontShare = System.Math.Max(0.0f, System.Math.Min(1.0f, (centerOfGravity.Y - rearY) / (frontY - rearY)));
                for (int axle = 0; axle < 2; axle++)
                {
                    float axleShare = axle == frontAxle ? frontShare : 1.0f - frontShare;
                    int leftWheel = -1, rightWheel = -1;
                    for (int i = 0; i < wheelCount; i++)
                    {
                        if (suspensionLoadAxleOfWheel[i] != axle) continue;
                        if (leftWheel < 0) leftWheel = i;
                        else if (rightWheel < 0) rightWheel = i;
                        else return false;
                    }
                    if (rightWheel < 0)
                    {
                        suspensionLoadWeightShares[leftWheel] = axleShare;
                        continue;
                    }
                    if (suspensionLoadHubLocals[rightWheel].X < suspensionLoadHubLocals[leftWheel].X)
                    {
                        int swap = leftWheel;
                        leftWheel = rightWheel;
                        rightWheel = swap;
                    }
                    float track = suspensionLoadHubLocals[rightWheel].X - suspensionLoadHubLocals[leftWheel].X;
                    float rightPart = track > 0.0f ? System.Math.Max(0.0f, System.Math.Min(1.0f, (centerOfGravity.X - suspensionLoadHubLocals[leftWheel].X) / track)) : 0.5f;
                    suspensionLoadWeightShares[rightWheel] = axleShare * rightPart;
                    suspensionLoadWeightShares[leftWheel] = axleShare * (1.0f - rightPart);
                }
                return true;
            }

            private static void DrawBoundingBrackets(Vehicle vehicle, Vector3 min, Vector3 max, float build)
            {
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 cornerLocal = new Vector3((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z);
                    Vector3 towardOpposite = new Vector3(((corner & 1) == 0 ? max.X : min.X) - cornerLocal.X, ((corner & 2) == 0 ? max.Y : min.Y) - cornerLocal.Y, ((corner & 4) == 0 ? max.Z : min.Z) - cornerLocal.Z) * VISUALIZATION_BRACKET_FRACTION;
                    Vector3 cornerWorld = ToWorld(vehicle, cornerLocal);
                    DrawGrowingLine(cornerWorld, ToWorld(vehicle, cornerLocal + new Vector3(towardOpposite.X, 0.0f, 0.0f)), build, VISUALIZATION_BOX_COLOR);
                    DrawGrowingLine(cornerWorld, ToWorld(vehicle, cornerLocal + new Vector3(0.0f, towardOpposite.Y, 0.0f)), build, VISUALIZATION_BOX_COLOR);
                    DrawGrowingLine(cornerWorld, ToWorld(vehicle, cornerLocal + new Vector3(0.0f, 0.0f, towardOpposite.Z)), build, VISUALIZATION_BOX_COLOR);
                }
            }

            // Each axis reaches VISUALIZATION_AXIS_OVERHANG past the bounds on both sides so it shows outside the body
            // The center of mass axes show the edited CenterOfMassOffset, but the vehicle only uses it after Unload Vehicle
            // The game bakes the offset into the model's absolute center of mass when the model loads
            private static void DrawAxes(Vehicle vehicle, Vector3 centerLocal, Vector3 min, Vector3 max, float build, Color color)
            {
                float axisProgress = BuildPhase(build, 0.0f, VISUALIZATION_AXIS_BUILD_SHARE);
                float tickProgress = BuildPhase(build, VISUALIZATION_AXIS_BUILD_SHARE, 1.0f);
                Vector3 tickAlongZ = new Vector3(0.0f, 0.0f, VISUALIZATION_AXIS_TICK_HALF_LENGTH);
                Vector3 tickAlongX = new Vector3(VISUALIZATION_AXIS_TICK_HALF_LENGTH, 0.0f, 0.0f);
                DrawAxisHalf(vehicle, centerLocal, new Vector3(min.X - VISUALIZATION_AXIS_OVERHANG, centerLocal.Y, centerLocal.Z), tickAlongZ, axisProgress, tickProgress, color);
                DrawAxisHalf(vehicle, centerLocal, new Vector3(max.X + VISUALIZATION_AXIS_OVERHANG, centerLocal.Y, centerLocal.Z), tickAlongZ, axisProgress, tickProgress, color);
                DrawAxisHalf(vehicle, centerLocal, new Vector3(centerLocal.X, min.Y - VISUALIZATION_AXIS_OVERHANG, centerLocal.Z), tickAlongZ, axisProgress, tickProgress, color);
                DrawAxisHalf(vehicle, centerLocal, new Vector3(centerLocal.X, max.Y + VISUALIZATION_AXIS_OVERHANG, centerLocal.Z), tickAlongZ, axisProgress, tickProgress, color);
                DrawAxisHalf(vehicle, centerLocal, new Vector3(centerLocal.X, centerLocal.Y, min.Z - VISUALIZATION_AXIS_OVERHANG), tickAlongX, axisProgress, tickProgress, color);
                DrawAxisHalf(vehicle, centerLocal, new Vector3(centerLocal.X, centerLocal.Y, max.Z + VISUALIZATION_AXIS_OVERHANG), tickAlongX, axisProgress, tickProgress, color);
            }

            private static void DrawAxisHalf(Vehicle vehicle, Vector3 centerLocal, Vector3 endLocal, Vector3 tickHalfLocal, float axisProgress, float tickProgress, Color color)
            {
                Vector3 center = ToWorld(vehicle, centerLocal);
                Vector3 end = ToWorld(vehicle, endLocal);
                DrawGrowingLine(center, end, axisProgress, color);
                DrawGrowingLine(end, ToWorld(vehicle, endLocal + tickHalfLocal), tickProgress, color);
                DrawGrowingLine(end, ToWorld(vehicle, endLocal - tickHalfLocal), tickProgress, color);
            }

            // The game measures an axle's roll center height up from the tire contact points, so a post rises by RollCenterHeight from the contact point's height
            // The posts are built in the vehicle's own frame so they roll with the body, and the contact point only sets their base height
            // A centered wheel (a bike's) gets a post on each side, and those 2 posts are never joined
            private void DrawRollCenterPosts(Vehicle vehicle, VisualizationAnimationState state, Vector3 min, Vector3 max, float build)
            {
                Vector3 leftFrontTop = Vector3.Zero, rightFrontTop = Vector3.Zero, leftRearTop = Vector3.Zero, rightRearTop = Vector3.Zero;
                bool hasLeftFront = false, hasRightFront = false, hasLeftRear = false, hasRightRear = false;
                float postProgress = BuildPhase(build, 0.0f, VISUALIZATION_ROLL_POST_BUILD_SHARE);
                float joinProgress = BuildPhase(build, VISUALIZATION_ROLL_POST_BUILD_SHARE, 1.0f);
                VisualizationCache cache = state.Cache;
                for (int i = 0; i < cache.Wheels.Length; i++)
                {
                    VehicleWheel wheel = cache.Wheels[i];
                    VehicleWheelBoneId boneId = wheel.BoneId;
                    bool isFront = boneId == VehicleWheelBoneId.WheelLeftFront || boneId == VehicleWheelBoneId.WheelRightFront;
                    if (!isFront && boneId != VehicleWheelBoneId.WheelLeftRear && boneId != VehicleWheelBoneId.WheelRightRear) continue;
                    EntityBone hub = ResolveWheelHub(cache, wheel);
                    if (hub == null) continue;
                    Vector3 hubLocal = vehicle.GetPositionOffset(hub.Position);
                    // The game's last hit position is stale while the wheel is in the air, so the post then stands on the bounds floor
                    float baseZ = wheel.IsTouchingSurface ? vehicle.GetPositionOffset(wheel.LastContactPosition).Z : min.Z;
                    float height = isFront ? state.RollCenterHeightFront : state.RollCenterHeightRear;
                    Vector3 rise = new Vector3(0.0f, 0.0f, height * postProgress);
                    Vector3 top = Vector3.Zero;
                    int sideCount = ResolveWheelSides(hubLocal);
                    for (int side = 0; side < sideCount; side++)
                    {
                        Vector3 baseLocal = new Vector3(ResolveSidePlaneX(wheelSides[side], min, max) + wheelSides[side] * VISUALIZATION_ROLL_POST_SIDE_OFFSET, hubLocal.Y, baseZ);
                        top = ToWorld(vehicle, baseLocal + rise);
                        DrawLine(ToWorld(vehicle, baseLocal), top, VISUALIZATION_ROLL_COLOR);
                    }
                    if (boneId == VehicleWheelBoneId.WheelLeftFront)
                    {
                        leftFrontTop = top;
                        hasLeftFront = true;
                    }
                    else if (boneId == VehicleWheelBoneId.WheelRightFront)
                    {
                        rightFrontTop = top;
                        hasRightFront = true;
                    }
                    else if (boneId == VehicleWheelBoneId.WheelLeftRear)
                    {
                        leftRearTop = top;
                        hasLeftRear = true;
                    }
                    else
                    {
                        rightRearTop = top;
                        hasRightRear = true;
                    }
                }
                if (hasLeftFront && hasRightFront) DrawLineFromEnds(leftFrontTop, rightFrontTop, joinProgress, VISUALIZATION_ROLL_COLOR);
                if (hasLeftRear && hasRightRear) DrawLineFromEnds(leftRearTop, rightRearTop, joinProgress, VISUALIZATION_ROLL_COLOR);
            }

            // Each axle's bar carries an arm at each end whose length shows that axle's share of AntiRollBarForce (the share times half the wheelbase)
            // Nothing draws with no anti-roll force or without all 4 corner hubs (boats, planes, bikes)
            private void DrawAntiRollBars(Vehicle vehicle, HandlingData handling, VisualizationAnimationState state, Vector3 min, Vector3 max, float build)
            {
                if (handling.AntiRollBarForce <= 0.0f) return;
                EntityBone leftFront = state.Cache.HubBones[(int)VehicleWheelBoneId.WheelLeftFront - WHEEL_BONE_ID_FIRST];
                EntityBone rightFront = state.Cache.HubBones[(int)VehicleWheelBoneId.WheelRightFront - WHEEL_BONE_ID_FIRST];
                EntityBone leftRear = state.Cache.HubBones[(int)VehicleWheelBoneId.WheelLeftRear - WHEEL_BONE_ID_FIRST];
                EntityBone rightRear = state.Cache.HubBones[(int)VehicleWheelBoneId.WheelRightRear - WHEEL_BONE_ID_FIRST];
                if (!leftFront.IsValid || !rightFront.IsValid || !leftRear.IsValid || !rightRear.IsValid) return;
                Vector3 leftFrontLocal = vehicle.GetPositionOffset(leftFront.Position);
                Vector3 rightFrontLocal = vehicle.GetPositionOffset(rightFront.Position);
                Vector3 leftRearLocal = vehicle.GetPositionOffset(leftRear.Position);
                Vector3 rightRearLocal = vehicle.GetPositionOffset(rightRear.Position);
                float halfWheelbase = ((leftFrontLocal.Y + rightFrontLocal.Y) - (leftRearLocal.Y + rightRearLocal.Y)) * 0.25f;
                float frontShare = state.AntiRollBarFrontShare;
                float barProgress = BuildPhase(build, 0.0f, VISUALIZATION_ANTI_ROLL_BAR_BUILD_SHARE);
                float armProgress = BuildPhase(build, VISUALIZATION_ANTI_ROLL_BAR_BUILD_SHARE, 1.0f);
                DrawAntiRollBarAxle(vehicle, leftFrontLocal, rightFrontLocal, min, max, -frontShare * halfWheelbase, barProgress, armProgress, VISUALIZATION_ANTI_ROLL_BAR_COLOR);
                DrawAntiRollBarAxle(vehicle, leftRearLocal, rightRearLocal, min, max, (1.0f - frontShare) * halfWheelbase, barProgress, armProgress, VISUALIZATION_ANTI_ROLL_BAR_COLOR);
            }

            // The arms start at the bar's ends, so they run along the box sides
            // armOffsetY is the vehicle-local Y length of each arm, and its sign points the arms at the other axle
            private static void DrawAntiRollBarAxle(Vehicle vehicle, Vector3 leftHubLocal, Vector3 rightHubLocal, Vector3 min, Vector3 max, float armOffsetY, float barProgress, float armProgress, Color color)
            {
                Vector3 leftEndLocal = new Vector3(ResolveSidePlaneX(OutboardSide(leftHubLocal), min, max), leftHubLocal.Y, leftHubLocal.Z);
                Vector3 rightEndLocal = new Vector3(ResolveSidePlaneX(OutboardSide(rightHubLocal), min, max), rightHubLocal.Y, rightHubLocal.Z);
                Vector3 leftEnd = ToWorld(vehicle, leftEndLocal);
                Vector3 rightEnd = ToWorld(vehicle, rightEndLocal);
                Vector3 arm = new Vector3(0.0f, armOffsetY, 0.0f);
                DrawLineFromMiddle(leftEnd, rightEnd, barProgress, color);
                DrawGrowingLine(leftEnd, ToWorld(vehicle, leftEndLocal + arm), armProgress, color);
                DrawGrowingLine(rightEnd, ToWorld(vehicle, rightEndLocal + arm), armProgress, color);
            }

            // A ring around the bounds at the height PercentSubmerged marks. Above 100 the vehicle sinks, so the ring glides to the top and blends to the sinking color
            // While sinking with the bounding brackets shown, each edge narrows into the gap between its 2 brackets
            // Each edge stops short of each bracket by VISUALIZATION_SINK_GAP_FRACTION of the gap
            private static void DrawWaterline(Vehicle vehicle, VisualizationAnimationState state, Vector3 min, Vector3 max, bool showBounding, float build)
            {
                float waterZ = min.Z + (max.Z - min.Z) * state.WaterlineFraction;
                Color color = BlendColor(VISUALIZATION_SUBMERGED_COLOR, VISUALIZATION_SINKING_COLOR, state.SinkBlend);
                float edgeStart = showBounding ? state.SinkBlend * (VISUALIZATION_BRACKET_FRACTION + (1.0f - 2.0f * VISUALIZATION_BRACKET_FRACTION) * VISUALIZATION_SINK_GAP_FRACTION) : 0.0f;
                Vector3 rearLeft = new Vector3(min.X, min.Y, waterZ);
                Vector3 rearRight = new Vector3(max.X, min.Y, waterZ);
                Vector3 frontRight = new Vector3(max.X, max.Y, waterZ);
                Vector3 frontLeft = new Vector3(min.X, max.Y, waterZ);
                if (state.SinkBlend > VISUALIZATION_WATERLINE_SINKING_BLEND)
                {
                    DrawWaterlineEdge(vehicle, rearLeft, rearRight, edgeStart, build, color);
                    DrawWaterlineEdge(vehicle, rearRight, frontRight, edgeStart, build, color);
                    DrawWaterlineEdge(vehicle, frontRight, frontLeft, edgeStart, build, color);
                    DrawWaterlineEdge(vehicle, frontLeft, rearLeft, edgeStart, build, color);
                    return;
                }
                DrawWaterlineInOrder(vehicle, rearLeft, rearRight, frontRight, frontLeft, edgeStart, build, color);
            }

            private static void DrawWaterlineEdge(Vehicle vehicle, Vector3 fromLocal, Vector3 toLocal, float edgeStart, float build, Color color)
            {
                DrawLineFromMiddle(WaterlineEdgePoint(vehicle, fromLocal, toLocal, edgeStart), WaterlineEdgePoint(vehicle, toLocal, fromLocal, edgeStart), build, color);
            }

            private static Vector3 WaterlineEdgePoint(Vehicle vehicle, Vector3 fromLocal, Vector3 toLocal, float edgeStart)
            {
                return ToWorld(vehicle, fromLocal + (toLocal - fromLocal) * edgeStart);
            }

            // Every edge keeps the trim DrawWaterlineEdge applies
            private static void DrawWaterlineInOrder(Vehicle vehicle, Vector3 rearLeft, Vector3 rearRight, Vector3 frontRight, Vector3 frontLeft, float edgeStart, float build, Color color)
            {
                float frontProgress = BuildPhase(build, 0.0f, VISUALIZATION_WATERLINE_STEP_SHARE);
                float sideProgress = BuildPhase(build, VISUALIZATION_WATERLINE_STEP_SHARE, 2.0f * VISUALIZATION_WATERLINE_STEP_SHARE);
                float rearProgress = BuildPhase(build, 2.0f * VISUALIZATION_WATERLINE_STEP_SHARE, 1.0f);
                DrawLineFromMiddle(WaterlineEdgePoint(vehicle, frontLeft, frontRight, edgeStart), WaterlineEdgePoint(vehicle, frontRight, frontLeft, edgeStart), frontProgress, color);
                DrawGrowingLine(WaterlineEdgePoint(vehicle, frontRight, rearRight, edgeStart), WaterlineEdgePoint(vehicle, rearRight, frontRight, edgeStart), sideProgress, color);
                DrawGrowingLine(WaterlineEdgePoint(vehicle, frontLeft, rearLeft, edgeStart), WaterlineEdgePoint(vehicle, rearLeft, frontLeft, edgeStart), sideProgress, color);
                DrawLineFromEnds(WaterlineEdgePoint(vehicle, rearLeft, rearRight, edgeStart), WaterlineEdgePoint(vehicle, rearRight, rearLeft, edgeStart), rearProgress, color);
            }

            // The wireframe shows exactly where the game collides, since it uses the game's own matrix and each bound's current type and box, both read each tick
            // The game builds that matrix from the wheel's fixed axle at the wheel bone's rest position with the suspension fully extended, with no tilt
            // It steers the matrix only for a few handling flag combinations
            private void DrawWheelBounds(VisualizationCache cache, Vehicle vehicle, float build)
            {
                System.IntPtr composite;
                if (!ResolveCompositeBound(vehicle, out composite)) return;
                float step = VISUALIZATION_WHEEL_BOUND_STEP_SHARE;
                float spokeProgress = BuildPhase(build, 0.0f, step);
                float ringProgress = BuildPhase(build, step, 2.0f * step);
                float treadProgress = BuildPhase(build, 2.0f * step, 1.0f);
                for (int boundIndex = 0; boundIndex < cache.WheelBounds.Length; boundIndex++)
                {
                    WheelBoundInfo bound = cache.WheelBounds[boundIndex];
                    Vector3 rightLocal, forwardLocal, upLocal, positionLocal;
                    if (!ReadCompositeMatrix(composite, bound.Child, out rightLocal, out forwardLocal, out upLocal, out positionLocal)) continue;
                    bool isCylinder = bound.BoundType == BoundLayout.TypeCylinder;
                    Color color = bound.IsExtra ? VISUALIZATION_EXTRA_WHEEL_BOUND_COLOR : VISUALIZATION_WHEEL_BOUND_COLOR;
                    // A disc lies flat across its own X axis and a cylinder runs along its own Y axis, and the other 2 columns span each face
                    Vector3 axleLocal = isCylinder ? forwardLocal : rightLocal;
                    if (axleLocal.Length() < VISUALIZATION_BOUND_AXIS_EPSILON) continue;
                    float radius, halfWidth;
                    ResolveWheelBoundSize(bound, out radius, out halfWidth);
                    Vector3 boxCenter = (bound.BoxMin + bound.BoxMax) * 0.5f;
                    Vector3 centerLocal = positionLocal + rightLocal * boxCenter.X + forwardLocal * boxCenter.Y + upLocal * boxCenter.Z;
                    // The outer face is the one on the wheel's own side of the centerline
                    Vector3 outboardAxleLocal = axleLocal.X * OutboardSide(centerLocal) < 0.0f ? -axleLocal : axleLocal;
                    Vector3 center = ToWorld(vehicle, centerLocal);
                    Vector3 outerCenter = ToWorld(vehicle, centerLocal + outboardAxleLocal * halfWidth);
                    Vector3 innerCenter = ToWorld(vehicle, centerLocal - outboardAxleLocal * halfWidth);
                    Vector3 faceForward = (ToWorld(vehicle, centerLocal + (isCylinder ? rightLocal : forwardLocal)) - center) * radius;
                    Vector3 faceUp = (ToWorld(vehicle, centerLocal + upLocal) - center) * radius;
                    int links = VISUALIZATION_RING_SEGMENTS / VISUALIZATION_RING_LINES_PER_LINK;
                    for (int link = 0; link < links; link++)
                    {
                        int segment = link * VISUALIZATION_RING_LINES_PER_LINK;
                        Vector3 outerRim = RingSegmentPoint(outerCenter, faceForward, faceUp, segment);
                        Vector3 innerRim = RingSegmentPoint(innerCenter, faceForward, faceUp, segment);
                        DrawGrowingLine(innerCenter, innerRim, spokeProgress, color);
                        DrawGrowingLine(outerCenter, outerRim, spokeProgress, color);
                        DrawLineFromEnds(innerRim, outerRim, treadProgress, color);
                    }
                    // The swapped axes start the sweep at the ring's front and back points, so each half grows up and down from its middle
                    DrawRingSweep(innerCenter, faceUp, faceForward, ringProgress, color);
                    DrawRingSweep(outerCenter, faceUp, faceForward, ringProgress, color);
                }
            }

            // The lengths of forward and up set the circle's radius. The 4 arcs grow from the point up reaches (angle 0) and the point opposite it
            private static void DrawRingSweep(Vector3 center, Vector3 forward, Vector3 up, float progress, Color color)
            {
                if (progress <= 0.0f) return;
                if (progress >= 1.0f)
                {
                    DrawFullRing(center, forward, up, color);
                    return;
                }
                int quarterSegments = VISUALIZATION_RING_SEGMENTS / 4;
                float quarterTurn = (float)System.Math.PI * 0.5f;
                float step = quarterTurn / quarterSegments;
                float sweep = quarterTurn * progress;
                for (int i = 0; i < quarterSegments; i++)
                {
                    float start = i * step;
                    if (start >= sweep) break;
                    float end = System.Math.Min(sweep, start + step);
                    DrawLine(RingPoint(center, forward, up, start), RingPoint(center, forward, up, end), color);
                    DrawLine(RingPoint(center, forward, up, -start), RingPoint(center, forward, up, -end), color);
                    DrawLine(RingPoint(center, forward, up, 2.0f * quarterTurn - start), RingPoint(center, forward, up, 2.0f * quarterTurn - end), color);
                    DrawLine(RingPoint(center, forward, up, 2.0f * quarterTurn + start), RingPoint(center, forward, up, 2.0f * quarterTurn + end), color);
                }
            }

            // The lengths of side and up set the circle's radius. The 2 arcs start at the point opposite up and climb both sides to meet where up points
            private static void DrawRingRisingFromBottom(Vector3 center, Vector3 side, Vector3 up, float progress, Color color)
            {
                if (progress <= 0.0f) return;
                if (progress >= 1.0f)
                {
                    DrawFullRing(center, side, up, color);
                    return;
                }
                int halfSegments = VISUALIZATION_RING_SEGMENTS / 2;
                float halfTurn = (float)System.Math.PI;
                float step = halfTurn / halfSegments;
                float sweep = halfTurn * progress;
                for (int i = 0; i < halfSegments; i++)
                {
                    float start = i * step;
                    if (start >= sweep) break;
                    float end = System.Math.Min(sweep, start + step);
                    DrawLine(RingPoint(center, side, up, halfTurn - start), RingPoint(center, side, up, halfTurn - end), color);
                    DrawLine(RingPoint(center, side, up, halfTurn + start), RingPoint(center, side, up, halfTurn + end), color);
                }
            }

            // The lengths of forward and side set the circle's radius. The 4 points where forward and side reach split the ring into quarters
            // Each quarter grows from both of its ends at once and closes at its middle, so the ring draws inward from the 4 points
            // A finished ring draws as one loop, like DrawLineFromMiddle, since 2 arcs ending on the same point leave a gap where they join
            private static void DrawRingInwardFromQuarters(Vector3 center, Vector3 forward, Vector3 side, float progress, Color color)
            {
                if (progress <= 0.0f) return;
                float quarterTurn = (float)System.Math.PI * 0.5f;
                if (progress >= 1.0f)
                {
                    DrawFullRing(center, forward, side, color);
                    return;
                }
                int eighthSegments = VISUALIZATION_RING_SEGMENTS / 8;
                float eighthTurn = quarterTurn * 0.5f;
                float step = eighthTurn / eighthSegments;
                float sweep = eighthTurn * progress;
                for (int quarter = 0; quarter < 4; quarter++)
                {
                    float quarterStart = quarter * quarterTurn;
                    float quarterEnd = quarterStart + quarterTurn;
                    for (int i = 0; i < eighthSegments; i++)
                    {
                        float start = i * step;
                        if (start >= sweep) break;
                        float end = System.Math.Min(sweep, start + step);
                        DrawLine(RingPoint(center, forward, side, quarterStart + start), RingPoint(center, forward, side, quarterStart + end), color);
                        DrawLine(RingPoint(center, forward, side, quarterEnd - start), RingPoint(center, forward, side, quarterEnd - end), color);
                    }
                }
            }

            // A point on a wheel bound face. Angle 0 is the top, and a positive angle turns toward forward
            private static Vector3 RingPoint(Vector3 center, Vector3 forward, Vector3 up, float angle)
            {
                return center + forward * (float)System.Math.Sin(angle) + up * (float)System.Math.Cos(angle);
            }

            // The ring point at a whole segment, read from the table. Segment 0 is the top, as with RingPoint's angle 0
            private static Vector3 RingSegmentPoint(Vector3 center, Vector3 forward, Vector3 up, int segment)
            {
                return center + forward * RingSegmentSines[segment] + up * RingSegmentCosines[segment];
            }

            // A finished ring draws the same VISUALIZATION_RING_SEGMENTS lines its build ends on, as one loop that places each point once
            private static void DrawFullRing(Vector3 center, Vector3 forward, Vector3 up, Color color)
            {
                Vector3 previous = RingSegmentPoint(center, forward, up, 0);
                for (int segment = 1; segment <= VISUALIZATION_RING_SEGMENTS; segment++)
                {
                    Vector3 next = RingSegmentPoint(center, forward, up, segment);
                    DrawLine(previous, next, color);
                    previous = next;
                }
            }

            // Measures to where the point stood one frame ago, since the script reads the camera as of the previous frame but the vehicle as of this one
            // Otherwise a moving vehicle reads farther by its speed times the frame time, which grows the gauges with speed and flickers them with every frame time change
            // A vehicle other than the one drawing now reads its own camera position and motion
            private static float MeasureCameraDistance(Vehicle vehicle, Vector3 worldPoint)
            {
                return vehicle.Handle == drawVehicleHandle ? Vector3.Distance(drawCameraPosition, worldPoint - drawFrameMotion) : Vector3.Distance(GameplayCamera.Position, worldPoint - vehicle.Velocity * Game.LastFrameTime);
            }

            // The vehicle's matrix places the point the same way the game's offset native does, a rotation plus the position with no scale, with no native call per point
            // The matrix is row-major, with the rotation in its first 3 rows and the position in its 4th
            private static Vector3 ToWorld(Vehicle vehicle, Vector3 local)
            {
                Matrix matrix = vehicle.Handle == drawVehicleHandle ? drawMatrix : vehicle.Matrix;
                return new Vector3(local.X * matrix.M11 + local.Y * matrix.M21 + local.Z * matrix.M31 + matrix.M41, local.X * matrix.M12 + local.Y * matrix.M22 + local.Z * matrix.M32 + matrix.M42, local.X * matrix.M13 + local.Y * matrix.M23 + local.Z * matrix.M33 + matrix.M43);
            }

            // A gauge's height is VISUALIZATION_GAUGE_SIZE_FRACTION of the larger of the bounding box diagonal and its own camera distance, and its scale is at least 1
            // A large vehicle gets a larger gauge, and a gauge farther from the camera keeps the same size on screen as a nearer one
            private static float ResolveGaugeScale(float diagonal, float cameraDistance)
            {
                float cappedDistance = System.Math.Min(cameraDistance, VISUALIZATION_GAUGE_CAMERA_DISTANCE_MAX_MULTIPLE * diagonal);
                return System.Math.Max(1.0f, VISUALIZATION_GAUGE_SIZE_FRACTION * System.Math.Max(diagonal, cappedDistance) / VISUALIZATION_GAUGE_HEIGHT);
            }

            // The level line shows the wheel's suspension load ratio: The middle for an even load, the top for double or more
            // A red bias line runs from the level line to the wheel's spring force share on the same scale, up where the spring is stiffer than its weight needs
            // When the weight split cannot resolve, the level line itself shows the spring force share, so it draws red and the bias line is left out
            // The gauge never reads the tire's contact point, so it holds still when the body is pushed
            private void DrawSuspensionLoadGauges(Vehicle vehicle, VisualizationAnimationState state, Vector3 min, Vector3 max, float build)
            {
                VisualizationCache cache = state.Cache;
                int wheelCount = cache.Wheels.Length;
                if (wheelCount == 0 || state.SuspensionLoadRatios.Length != wheelCount) return;
                Vector3 across, upward;
                ResolveCameraFacingBasis(vehicle, out across, out upward);
                Vector3 boxCenter = ToWorld(vehicle, (min + max) * 0.5f);
                float diagonal = (max - min).Length();
                float centerDistance = MeasureCameraDistance(vehicle, boxCenter);
                float smoothedCenterDistance = state.GaugeCameraDistance;
                float bottomProgress = BuildPhase(build, 0.0f, VISUALIZATION_GAUGE_STEP_SHARE);
                float poleProgress = BuildPhase(build, VISUALIZATION_GAUGE_STEP_SHARE, 2.0f * VISUALIZATION_GAUGE_STEP_SHARE);
                float topProgress = BuildPhase(build, 2.0f * VISUALIZATION_GAUGE_STEP_SHARE, 3.0f * VISUALIZATION_GAUGE_STEP_SHARE);
                float levelDrawProgress = BuildPhase(build, 3.0f * VISUALIZATION_GAUGE_STEP_SHARE, 4.0f * VISUALIZATION_GAUGE_STEP_SHARE);
                float levelSlideProgress = BuildPhase(build, 4.0f * VISUALIZATION_GAUGE_STEP_SHARE, 1.0f);
                for (int i = 0; i < wheelCount; i++)
                {
                    VehicleWheel wheel = cache.Wheels[i];
                    float ratio = state.SuspensionLoadRatios[i];
                    EntityBone hub = ResolveWheelHub(cache, wheel);
                    // A wheel with no spring force carries no load to show
                    if (wheel.StaticForce <= 0.0f || hub == null) continue;
                    Vector3 hubLocal = vehicle.GetPositionOffset(hub.Position);
                    // This gauge's distance is the smoothed center distance, scaled by how much nearer or farther this hub is than the center right now
                    float hubDistance = MeasureCameraDistance(vehicle, hub.Position);
                    float scale = ResolveGaugeScale(diagonal, centerDistance > 0.0f ? smoothedCenterDistance * hubDistance / centerDistance : smoothedCenterDistance);
                    float gaugeHeight = VISUALIZATION_GAUGE_HEIGHT * scale;
                    float sideOffset = VISUALIZATION_SUSPENSION_LOAD_SIDE_OFFSET * scale;
                    Vector3 halfWidth = across * (VISUALIZATION_GAUGE_WIDTH * 0.5f * scale);
                    Vector3 height = upward * gaugeHeight;
                    float levelShare = System.Math.Max(0.0f, System.Math.Min(1.0f, ratio * 0.5f));
                    float springShare = System.Math.Max(0.0f, System.Math.Min(1.0f, state.SuspensionSpringRatios[i] * 0.5f));
                    // Both lines start at the gauge's middle and slide to their shares together, so the level line reveals the spring line as it moves
                    Vector3 middle = height * 0.5f;
                    Vector3 level = Glide(middle, upward * (gaugeHeight * levelShare), levelSlideProgress);
                    Vector3 springLevel = Glide(middle, upward * (gaugeHeight * springShare), levelSlideProgress);
                    // The bias line starts a small gap past the level line, toward the spring end, and stays hidden until it is longer than that gap
                    Vector3 biasSpan = springLevel - level;
                    float biasLength = biasSpan.Length();
                    float biasGap = gaugeHeight * VISUALIZATION_SUSPENSION_LOAD_BIAS_GAP_SHARE;
                    bool drawBiasLine = state.SuspensionLoadFromWeight && System.Math.Abs(springShare - levelShare) >= VISUALIZATION_SUSPENSION_LOAD_BIAS_MIN_SHARE;
                    int sideCount = ResolveWheelSides(hubLocal);
                    for (int side = 0; side < sideCount; side++)
                    {
                        Vector3 gaugeCenter = ToWorld(vehicle, new Vector3(ResolveSidePlaneX(wheelSides[side], min, max) + wheelSides[side] * sideOffset, hubLocal.Y, hubLocal.Z));
                        Vector3 bottomLeft = gaugeCenter - height * 0.5f - halfWidth;
                        Vector3 bottomRight = gaugeCenter - height * 0.5f + halfWidth;
                        DrawLineFromMiddle(bottomLeft, bottomRight, bottomProgress, VISUALIZATION_SUSPENSION_LOAD_COLOR);
                        DrawGrowingLine(bottomLeft, bottomLeft + height, poleProgress, VISUALIZATION_SUSPENSION_LOAD_COLOR);
                        DrawGrowingLine(bottomRight, bottomRight + height, poleProgress, VISUALIZATION_SUSPENSION_LOAD_COLOR);
                        DrawLineFromEnds(bottomLeft + height, bottomRight + height, topProgress, VISUALIZATION_SUSPENSION_LOAD_COLOR);
                        DrawLineFromEnds(bottomLeft + level, bottomRight + level, levelDrawProgress, state.SuspensionLoadFromWeight ? VISUALIZATION_SUSPENSION_LOAD_LEVEL_COLOR : VISUALIZATION_SUSPENSION_LOAD_BIAS_COLOR);
                        if (drawBiasLine && biasLength > biasGap) DrawLine(bottomLeft + halfWidth + level + biasSpan * (biasGap / biasLength), bottomLeft + halfWidth + springLevel, VISUALIZATION_SUSPENSION_LOAD_BIAS_COLOR);
                    }
                }
            }

            // A boat's thrust point in its own frame, where the game applies thrust and rudder force before their offsets
            // It is the average of the prop bones, or with none, the bounds' rear center at the bounds' min Z times RudderOffsetForceZMultiplier
            private Vector3 ResolveThrustPointLocal(Vehicle vehicle, VisualizationAnimationState state, Vector3 min)
            {
                Vector3 sum = Vector3.Zero;
                int count = 0;
                for (int i = 0; i < state.Cache.PropBones.Length; i++)
                {
                    EntityBone bone = state.Cache.PropBones[i];
                    if (bone == null || !bone.IsValid) continue;
                    sum += vehicle.GetPositionOffset(bone.Position);
                    count++;
                }
                return count > 0 ? sum * (1.0f / count) : new Vector3(0.0f, min.Y, min.Z * state.RudderOffsetForceZMultiplier);
            }

            // The cyan line from the stub's left end is the path RudderOffsetSubmerge moves the propeller's water test point along
            // The red line from its right end is the path RudderOffsetForce moves the force point along
            // The white stub keeps the 2 lines apart when both offsets share a sign
            private void DrawRudderOffsets(Vehicle vehicle, VisualizationAnimationState state, Vector3 min, float build)
            {
                float stubProgress = BuildPhase(build, 0.0f, VISUALIZATION_RUDDER_STUB_BUILD_SHARE);
                float lineProgress = BuildPhase(build, VISUALIZATION_RUDDER_STUB_BUILD_SHARE, 1.0f);
                Vector3 thrustLocal = ResolveThrustPointLocal(vehicle, state, min);
                Vector3 stubHalf = new Vector3(VISUALIZATION_RUDDER_STUB_HALF_LENGTH, 0.0f, 0.0f);
                Vector3 leftLocal = thrustLocal - stubHalf;
                Vector3 rightLocal = thrustLocal + stubHalf;
                Vector3 left = ToWorld(vehicle, leftLocal);
                Vector3 right = ToWorld(vehicle, rightLocal);
                DrawLineFromMiddle(left, right, stubProgress, VISUALIZATION_RUDDER_STUB_COLOR);
                DrawGrowingLine(left, ToWorld(vehicle, leftLocal + new Vector3(0.0f, 0.0f, state.RudderOffsetSubmerge)), lineProgress, VISUALIZATION_RUDDER_SUBMERGE_COLOR);
                DrawGrowingLine(right, ToWorld(vehicle, rightLocal + new Vector3(0.0f, 0.0f, state.RudderOffsetForce)), lineProgress, VISUALIZATION_RUDDER_FORCE_COLOR);
            }

            // The cross is centered on the propeller's water test point (the thrust point moved up by RudderOffsetSubmerge), with arms PropRadius long
            // The game counts the propeller as in the water once the water reaches the bottom tip
            private void DrawPropRadiusCross(Vehicle vehicle, VisualizationAnimationState state, Vector3 min, float build)
            {
                if (state.PropRadius <= 0.0f) return;
                Vector3 centerLocal = ResolveThrustPointLocal(vehicle, state, min) + new Vector3(0.0f, 0.0f, state.RudderOffsetSubmerge);
                Vector3 center = ToWorld(vehicle, centerLocal);
                Vector3 armUp = new Vector3(0.0f, 0.0f, state.PropRadius);
                Vector3 armRight = new Vector3(state.PropRadius, 0.0f, 0.0f);
                DrawGrowingLine(center, ToWorld(vehicle, centerLocal + armUp), build, VISUALIZATION_PROP_RADIUS_COLOR);
                DrawGrowingLine(center, ToWorld(vehicle, centerLocal - armUp), build, VISUALIZATION_PROP_RADIUS_COLOR);
                DrawGrowingLine(center, ToWorld(vehicle, centerLocal + armRight), build, VISUALIZATION_PROP_RADIUS_COLOR);
                DrawGrowingLine(center, ToWorld(vehicle, centerLocal - armRight), build, VISUALIZATION_PROP_RADIUS_COLOR);
            }

            // The impeller pulls only for handling that drives the rear wheels, which is the game's jet drive rule
            // That is a rear drive bias above 0, or car handling whose MaxDriveBiasTransfer is above -1
            private static bool DrivesRearWheels(HandlingData handling)
            {
                if (RawMemory.ReadRawFloat(handling.MemoryAddress, HandlingDataLayout.DriveBiasRear) > 0.0f) return true;
                CarHandlingData car = handling.CarHandlingData;
                return car != null && RawMemory.ReadRawFloat(car.MemoryAddress, CarLayout.MaxDriveBiasTransfer) > -1.0f;
            }

            // The impeller point is on the centerline at ImpellerOffset times the bounds' rear extent, at the model origin's height
            // The arrow's length follows ImpellerForceMultiplier, capped at the bounds height
            // Drawn only while the game would apply the pull: ImpellerOffset above 0 on handling that drives the rear wheels
            private void DrawImpeller(Vehicle vehicle, HandlingData handling, VisualizationAnimationState state, Vector3 min, Vector3 max, float build)
            {
                if (state.ImpellerOffset <= 0.0f || !DrivesRearWheels(handling)) return;
                float ringProgress = BuildPhase(build, 0.0f, VISUALIZATION_IMPELLER_RING_BUILD_SHARE);
                float arrowProgress = BuildPhase(build, VISUALIZATION_IMPELLER_RING_BUILD_SHARE, 1.0f);
                Vector3 pointLocal = new Vector3(0.0f, min.Y * state.ImpellerOffset, 0.0f);
                Vector3 point = ToWorld(vehicle, pointLocal);
                Vector3 ringForward = ToWorld(vehicle, pointLocal + new Vector3(0.0f, GAME_IMPELLER_RADIUS, 0.0f)) - point;
                Vector3 ringSide = ToWorld(vehicle, pointLocal + new Vector3(GAME_IMPELLER_RADIUS, 0.0f, 0.0f)) - point;
                DrawRingSweep(point, ringForward, ringSide, ringProgress, VISUALIZATION_IMPELLER_COLOR);
                float arrowLength = System.Math.Min(System.Math.Max(0.0f, state.ImpellerForceMultiplier) * VISUALIZATION_IMPELLER_ARROW_METERS_PER_UNIT, max.Z - min.Z);
                if (arrowLength <= 0.0f) return;
                Vector3 tipLocal = pointLocal - new Vector3(0.0f, 0.0f, arrowLength);
                Vector3 tip = ToWorld(vehicle, tipLocal);
                DrawGrowingLine(point, tip, arrowProgress, VISUALIZATION_IMPELLER_COLOR);
                DrawGrowingLine(tip, ToWorld(vehicle, tipLocal + new Vector3(VISUALIZATION_IMPELLER_ARROW_HEAD_LENGTH, 0.0f, VISUALIZATION_IMPELLER_ARROW_HEAD_LENGTH)), arrowProgress, VISUALIZATION_IMPELLER_COLOR);
                DrawGrowingLine(tip, ToWorld(vehicle, tipLocal + new Vector3(-VISUALIZATION_IMPELLER_ARROW_HEAD_LENGTH, 0.0f, VISUALIZATION_IMPELLER_ARROW_HEAD_LENGTH)), arrowProgress, VISUALIZATION_IMPELLER_COLOR);
            }

            // The volume the game lays its 5 by 5 water sample probe grid in, centered on the model origin
            // It spans the bounds' width times BoxSideMultiplier and the bounds' length (front extent times BoxFrontMultiplier, rear times BoxRearMultiplier)
            // Its height runs from the probe bottom (SampleBottomTestCorrection of the way up the bounds) to SampleTop
            // It glides with the edited values, while the water samples keep the layout the model loaded with until Unload Vehicle. It builds bottom-up like the samples
            private static void DrawWaterSampleBox(Vehicle vehicle, VisualizationAnimationState state, Vector3 min, Vector3 max, float build)
            {
                float halfWidth = 0.5f * (max.X - min.X) * state.BoxSideMultiplier;
                float halfLength = 0.5f * (max.Y * state.BoxFrontMultiplier - min.Y * state.BoxRearMultiplier);
                float bottom = min.Z + state.SampleBottomTestCorrection * (max.Z - min.Z);
                Vector3 centerLocal = new Vector3(0.0f, 0.0f, 0.5f * (bottom + state.SampleTop));
                Vector3 center = ToWorld(vehicle, centerLocal);
                Vector3 halfRight = ToWorld(vehicle, centerLocal + new Vector3(halfWidth, 0.0f, 0.0f)) - center;
                Vector3 halfForward = ToWorld(vehicle, centerLocal + new Vector3(0.0f, halfLength, 0.0f)) - center;
                Vector3 halfUp = ToWorld(vehicle, centerLocal + new Vector3(0.0f, 0.0f, 0.5f * (state.SampleTop - bottom))) - center;
                DrawWireBoxBottomUp(center, halfRight, halfForward, halfUp, build, VISUALIZATION_WATER_SAMPLE_BOX_COLOR);
            }

            // Only samples of the given kind draw, each moved through its bound component's current matrix, then the vehicle's, where the game tests it
            // An ordinary or pontoon sample with a size draws as a box reaching its size along that matrix's axes, built bottom-up, lowest samples first
            // A keel sample draws as a sphere of 3 rings of its size: 2 upright rings climbing from the bottom, then a flat ring drawing inward from where they cross it
            // A sample whose component the composite no longer holds is skipped, as the game skips it
            private void DrawWaterSamples(Vehicle vehicle, System.IntPtr composite, WaterSampleInfo[] samples, WaterSampleKind kind, float build, Color color)
            {
                short childCount;
                if (!RawMemory.ReadRawInt16(composite, CompositeBoundLayout.ChildCount, out childCount)) return;
                if (waterSampleFrames.Length < samples.Length) waterSampleFrames = new WaterSampleFrame[samples.Length];
                if (waterSampleLevels.Length < samples.Length) waterSampleLevels = new float[samples.Length];
                int matrixComponent = -1;
                int levelCount = 0;
                Vector3 rightLocal = Vector3.Zero, forwardLocal = Vector3.Zero, upLocal = Vector3.Zero, positionLocal = Vector3.Zero;
                for (int i = 0; i < samples.Length; i++)
                {
                    waterSampleFrames[i].Drawn = false;
                    WaterSampleInfo sample = samples[i];
                    if (sample.Kind != kind || sample.Size <= 0.0f || sample.Component >= childCount) continue;
                    // Consecutive samples usually share a component, so its matrix is read once for the run
                    if (sample.Component != matrixComponent)
                    {
                        if (!ReadCompositeMatrix(composite, sample.Component, out rightLocal, out forwardLocal, out upLocal, out positionLocal)) continue;
                        matrixComponent = sample.Component;
                    }
                    Vector3 centerLocal = positionLocal + rightLocal * sample.Offset.X + forwardLocal * sample.Offset.Y + upLocal * sample.Offset.Z;
                    waterSampleFrames[i] = new WaterSampleFrame { Drawn = true, CenterLocal = centerLocal, RightLocal = rightLocal, ForwardLocal = forwardLocal, UpLocal = upLocal };
                    waterSampleLevels[levelCount++] = centerLocal.Z;
                }
                levelCount = SortDistinctLevels(waterSampleLevels, levelCount);
                for (int i = 0; i < samples.Length; i++)
                {
                    WaterSampleFrame frame = waterSampleFrames[i];
                    if (!frame.Drawn) continue;
                    float size = samples[i].Size;
                    Vector3 center = ToWorld(vehicle, frame.CenterLocal);
                    Vector3 right = (ToWorld(vehicle, frame.CenterLocal + frame.RightLocal) - center) * size;
                    Vector3 forward = (ToWorld(vehicle, frame.CenterLocal + frame.ForwardLocal) - center) * size;
                    Vector3 up = (ToWorld(vehicle, frame.CenterLocal + frame.UpLocal) - center) * size;
                    if (kind != WaterSampleKind.Keel)
                    {
                        DrawWireBoxBottomUp(center, right, forward, up, ResolveWaterSampleBuild(build, frame.CenterLocal.Z, levelCount), color);
                        continue;
                    }
                    DrawRingRisingFromBottom(center, forward, up, build, color);
                    DrawRingRisingFromBottom(center, right, up, build, color);
                    DrawRingInwardFromQuarters(center, forward, right, BuildPhase(build, VISUALIZATION_KEEL_FLAT_RING_START_SHARE, 1.0f), color);
                }
            }

            // Sorts the first count heights and keeps one of each run closer than VISUALIZATION_WATER_SAMPLE_LEVEL_EPSILON, returning how many remain
            private static int SortDistinctLevels(float[] levels, int count)
            {
                System.Array.Sort(levels, 0, count);
                int distinct = 0;
                for (int i = 0; i < count; i++)
                {
                    if (distinct > 0 && levels[i] - levels[distinct - 1] < VISUALIZATION_WATER_SAMPLE_LEVEL_EPSILON) continue;
                    levels[distinct++] = levels[i];
                }
                return distinct;
            }

            // A sample's own build, a VISUALIZATION_WATER_SAMPLE_BUILD_WINDOW share of the element's build that starts later the higher its level ranks
            private float ResolveWaterSampleBuild(float build, float height, int levelCount)
            {
                int rank = 0;
                while (rank < levelCount - 1 && waterSampleLevels[rank] < height - VISUALIZATION_WATER_SAMPLE_LEVEL_EPSILON) rank++;
                float start = levelCount > 1 ? rank * (1.0f - VISUALIZATION_WATER_SAMPLE_BUILD_WINDOW) / (levelCount - 1) : 0.0f;
                return BuildPhase(build, start, start + VISUALIZATION_WATER_SAMPLE_BUILD_WINDOW);
            }

            // A third each: The bottom square's edges grow from their middles, the 4 vertical edges grow upward, then the top square's edges grow in from their ends
            // halfRight, halfForward, and halfUp reach from the center to the faces, so the box spans twice each
            private static void DrawWireBoxBottomUp(Vector3 center, Vector3 halfRight, Vector3 halfForward, Vector3 halfUp, float progress, Color color)
            {
                float bottomProgress = BuildPhase(progress, 0.0f, VISUALIZATION_WIRE_BOX_STEP_SHARE);
                float edgeProgress = BuildPhase(progress, VISUALIZATION_WIRE_BOX_STEP_SHARE, 2.0f * VISUALIZATION_WIRE_BOX_STEP_SHARE);
                float topProgress = BuildPhase(progress, 2.0f * VISUALIZATION_WIRE_BOX_STEP_SHARE, 1.0f);
                Vector3 bottom = center - halfUp;
                Vector3 top = center + halfUp;
                for (int side = -1; side <= 1; side += 2)
                {
                    DrawLineFromMiddle(bottom - halfRight + halfForward * side, bottom + halfRight + halfForward * side, bottomProgress, color);
                    DrawLineFromMiddle(bottom - halfForward + halfRight * side, bottom + halfForward + halfRight * side, bottomProgress, color);
                    DrawLineFromEnds(top - halfRight + halfForward * side, top + halfRight + halfForward * side, topProgress, color);
                    DrawLineFromEnds(top - halfForward + halfRight * side, top + halfForward + halfRight * side, topProgress, color);
                }
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3 bottomCorner = bottom + halfRight * ((corner & 1) == 0 ? -1.0f : 1.0f) + halfForward * ((corner & 2) == 0 ? -1.0f : 1.0f);
                    DrawGrowingLine(bottomCorner, bottomCorner + halfUp * 2.0f, edgeProgress, color);
                }
            }
        }
    }
}
