using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The handling struct, relative to its own MemoryAddress
        private static class HandlingDataLayout
        {
            // Derived fields the API does not expose as named properties
            // Each must stay in sync with its paired primary field whenever that is written, or the vehicle's physics desync from the displayed value
            public const int DriveBiasRear               = 0x4C;
            public const int SubmergedRatio              = 0x44;
            public const int SteeringLockRatio           = 0x84;
            public const int TractionCurveMaxRatio       = 0x8C;
            public const int TractionCurveRatio          = 0x94;
            public const int TractionCurveLateralRatio   = 0x9C;
            public const int TractionSpringDeltaMaxRatio = 0xA4;
            public const int SuspensionBiasRear          = 0xD8;
            public const int AntiRollBarBiasRear         = 0xE4;
            public const int BrakeBiasRear               = 0x78;
            public const int TractionBiasRear            = 0xB4;
            // The game's stand-in for an infinite traction inverse when its divisor is 0 or negative
            public const float LargeFloat = 1.0e8f;
            // Fields the API reads from the wrong offset, which raw offset access bypasses
            public const int RocketBoostCapacity = 0x11C;
            public const int BoostMaxSpeed       = 0x120;
            // A field with no API wrapper at all. It is wired in the Weapon submenu since it only matters for a vehicle with weapon subdata
            public const int WeaponDamageScaledToVehHealthMult = 0x168;
            // The handling's own name hash, stored right after its vtable pointer
            public const int HandlingName = 0x08;
            // Flag bitmasks, with the same layout as the corresponding API-backed flag properties
            // They remain available independently of the version-gated raw-field table
            public const int ModelFlags    = 0x124;
            public const int HandlingFlags = 0x128;
            public const int DamageFlags   = 0x12C;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class BikeLayout
        {
            public const int FullAnimAngleInverse              = 0x20;
            public const int GroundSideFrictionMultiplier      = 0x48;
            public const int WheelGroundSideFrictionMultiplier = 0x4C;
            public const int OnStandSteerAngle                 = 0x54;
            public const int JumpForce                         = 0x58;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class FlyingLayout
        {
            public const int InitialThrust                     = 0x14;
            public const int InitialThrustFallOff              = 0x18;
            public const int InitialYawMultiplier              = 0x28;
            public const int InitialRollMultiplier             = 0x34;
            public const int InitialPitchMultiplier            = 0x40;
            public const int GearDoorFrontOpen                 = 0x80;
            public const int GearDoorRearOpen                  = 0x84;
            public const int GearDoorRearOpen2                 = 0x88;
            public const int GearDoorRearMOpen                 = 0x8C;
            public const int TurbulenceMagnitudeMax            = 0x90;
            public const int TurbulenceForceMultiplier         = 0x94;
            public const int TurbulenceRollTorqueMultiplier    = 0x98;
            public const int TurbulencePitchTorqueMultiplier   = 0x9C;
            public const int BodyDamageControlEffectMultiplier = 0xA0;
            public const int InputSensitivityForDifficulty     = 0xA4;
            public const int OnGroundYawBoostSpeedPeak         = 0xA8;
            public const int OnGroundYawBoostSpeedCap          = 0xAC;
            public const int EngineOffGlideMultiplier          = 0xB0;
            public const int AfterburnerEffectRadius           = 0xB4;
            public const int AfterburnerEffectDistance         = 0xB8;
            public const int AfterburnerEffectForceMultiplier  = 0xBC;
            public const int SubmergeLevelToPullHeliUnderwater = 0xC0;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class SpecialFlightLayout
        {
            public const int LiftCoefficient              = 0x50;
            public const int CriticalLiftAngle            = 0x54;
            public const int InitialLiftAngle             = 0x58;
            public const int MaxLiftAngle                 = 0x5C;
            public const int DragCoefficient              = 0x60;
            public const int BrakingDrag                  = 0x64;
            public const int MaxLiftVelocity              = 0x68;
            public const int MinLiftVelocity              = 0x6C;
            public const int MaxTorqueVelocity            = 0x74;
            public const int MinTorqueVelocity            = 0x78;
            public const int SelfLevelingPitchTorqueScale = 0x80;
            public const int MaxPitchTorque               = 0x88;
            public const int MaxSteeringRollTorque        = 0x8C;
            public const int SteeringTorqueScale          = 0x94;
            public const int MaxThrust                    = 0x98;
            public const int StabilityAssist              = 0xA4;
            // Both are version-gated separately (GAME_VERSION_1493_0, RawMemory.SpecialFlightFlagsSupported)
            // Neither offset exists at all below that build
            public const int Mode  = 0xB0;
            public const int Flags = 0xB8;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class BoatLayout
        {
            public const int BoxFrontMultiplier                = 0x08;
            public const int BoxRearMultiplier                 = 0x0C;
            public const int BoxSideMultiplier                 = 0x10;
            public const int SampleTop                         = 0x14;
            public const int SampleBottom                      = 0x18;
            public const int SampleBottomTestCorrection        = 0x1C;
            public const int AquaplaneForce                    = 0x20;
            public const int RudderOffsetForceZMultiplier      = 0x3C;
            public const int WaveAudioMultiplier               = 0x40;
            public const int MoveResistance                    = 0x50;
            public const int MoveResistanceY                   = 0x54;
            public const int MoveResistanceZ                   = 0x58;
            public const int LookLRCamHeight                   = 0x70;
            public const int PropRadius                        = 0x7C;
            public const int LowLodAngOffset                   = 0x80;
            public const int LowLodDraughtOffset               = 0x84;
            public const int ImpellerOffset                    = 0x88;
            public const int ImpellerForceMultiplier           = 0x8C;
            public const int DinghySphereBuoyConstant          = 0x90;
            public const int ProwRaiseMultiplier               = 0x94;
            public const int DeepWaterSampleBuoyancyMultiplier = 0x98;
            public const int TransmissionMultiplier            = 0x9C;
            public const int TractionMultiplier                = 0xA0;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class SeaPlaneLayout
        {
            public const int LeftPontoonComponentId          = 0x08;
            public const int RightPontoonComponentId         = 0x0C;
            public const int PontoonBuoyConstant             = 0x10;
            public const int PontoonSampleSizeFront          = 0x14;
            public const int PontoonSampleSizeMiddle         = 0x18;
            public const int PontoonSampleSizeRear           = 0x1C;
            public const int PontoonLengthFractionForSamples = 0x20;
            public const int KeelSphereSize                  = 0x30;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class SubmarineLayout
        {
            public const int TurnResistance   = 0x10;
            public const int TurnResistanceY  = 0x14;
            public const int TurnResistanceZ  = 0x18;
            public const int MoveResistanceXY = 0x20;
            public const int MoveResistanceZ  = 0x24;
            public const int RollMultiplier   = 0x38;
            public const int RollStabilize    = 0x3C;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class TrailerLayout
        {
            public const int AttachRaiseZ = 0x24;
        }

        // SubHandling fields with no API property, relative to the SubHandling object's own MemoryAddress
        private static class CarLayout
        {
            public const int BackEndPopUpCarImpulseMultiplier      = 0x08;
            public const int BackEndPopUpBuildingImpulseMultiplier = 0x0C;
            public const int BackEndPopUpMaxDeltaSpeed             = 0x10;
            public const int MaxDriveBiasTransfer                  = 0x2C;
            public const int JumpForceScale                        = 0x30;
            public const int IncreasedRammingForceScale            = 0x34;
            // The AdvancedFlags bitmask, with the same layout as the API-backed flag properties
            public const int AdvancedFlags = 0x3C;
            // The atArray<CAdvancedData> of AdvancedData entries
            public const int AdvancedDataArray = 0x40;
        }

        // The AdvancedData atArray's count and capacity, relative to the array, and one element's members
        // Element stride is 8-byte aligned past Value (vtable ptr + Slot + Index + Value, rounded up), confirmed entry for entry against 2 real vehicles' handling.meta
        private static class AdvancedDataLayout
        {
            public const int ArrayCount    = 0x08;
            public const int ArrayCapacity = 0x0A;
            public const int ElementSlot   = 0x08;
            public const int ElementIndex  = 0x0C;
            public const int ElementValue  = 0x10;
            public const int ElementStride = 0x18;
        }

        // Weapon SubHandling: Per-turret-slot arrays and whole-category scalars at their offsets in the game's own struct
        // Verified against the API's own offsets for the fields it does expose
        private static class WeaponLayout
        {
            public const int TurretCamPitchMin        = 0xE0;
            public const int TurretCamPitchMax        = 0x110;
            public const int BulletVelocityForGravity = 0x140;
            public const int TurretPitchForwardMin    = 0x170;
            public const int UvAnimationMult          = 0x320;
            public const int MiscGadgetVar            = 0x324;
            public const int WheelImpactOffset        = 0x328;
            // TurretPitchLimitData: One entry per turret slot, each an 8-byte vtable pointer followed by the 6 TurretPitchLimitFields floats
            public const int TurretPitchLimitData        = 0x1A0;
            public const int TurretPitchLimitEntryStride = 0x20;
            public const int TurretPitchLimitFirstField  = 0x08;
        }

        // An entity's physics instance and its model info pointer
        private static class EntityLayout
        {
            public const int PhysicsInstance = 0x30;
            public const int ModelInfo       = 0x20;
        }

        // A physics instance's archetype, the first link to the collision bounds, and its cache entry, the first link to the collider type
        private static class PhysicsInstanceLayout
        {
            public const int Archetype  = 0x10;
            public const int CacheEntry = 0x68;
        }

        // An archetype's bound
        private static class ArchetypeLayout
        {
            public const int Bound = 0x20;
        }

        // Every bound's type byte and its own-frame box, whose max and min are 3 floats each
        private static class BoundLayout
        {
            public const int Type   = 0x10;
            public const int BoxMax = 0x20;
            public const int BoxMin = 0x30;
            // Type values: A composite holds the vehicle's parts, a disc lies flat across its own X axis, and a cylinder runs along its own Y axis
            public const int TypeComposite = 10;
            public const int TypeDisc      = 12;
            public const int TypeCylinder  = 13;
        }

        // A composite bound's child pointer array, its current child matrices (4 columns of 16 bytes in the order right, forward, up, position), and its child count
        private static class CompositeBoundLayout
        {
            public const int ChildBounds        = 0x70;
            public const int CurrentMatrices    = 0x78;
            public const int ChildCount         = 0xA0;
            public const int MatrixStride       = 0x40;
            public const int MatrixColumnStride = 0x10;
        }

        // A physics instance cache entry's articulated collider
        // Read from each edition's own switch into articulated mode, which loads the cache entry and the collider and tests the type before changing it
        private static class CacheEntryLayout
        {
            public const int Collider = 0x1A0;
        }

        // A collider's type, which sits 8 bytes later on Enhanced. 0 is rigid, and 1 and 2 are articulated, the state an unlatched door joint puts the vehicle in
        private static class ColliderLayout
        {
            public const int Type         = 0x8;
            public const int TypeEnhanced = 0x10;
            public const int TypeRigid    = 0;
            public const int TypeMax      = 2;
        }

        // Vehicle fields, relative to the vehicle's own MemoryAddress
        private static class VehicleLayout
        {
            // A plane's engine warm-up timer in seconds
            // Thrust scales with it up to its 5 second maximum, and a new plane starts it at 0
            // Found in game by a scan of a warmed-up plane against a freshly respawned one
            public const int EngineWarmUpTimer = 0x2004;
            // A glider vehicle's glider state (0 not gliding, 1 deploying wings, 2 gliding, 3 retracting wings) and its wing deployed ratio (-0.15 retracted to 0 deployed)
            // Both are read from the game's own glider update, at the same offsets on both editions
            public const int GliderState       = 0x340;
            public const int WingDeployedRatio = 0x344;
        }

        // The model info's buoyancy info pointer, found in game, and the indexes of 2 flags in its vehicles.meta flag bitset
        // The bitset's word size in flags and in bytes
        private static class ModelInfoLayout
        {
            public const int BuoyancyInfo                  = 0x78;
            public const int FlagHasRearMountedTurretIndex = 111;
            public const int FlagHasGliderIndex            = 137;
            public const int FlagsPerWord                  = 32;
            public const int FlagWordBytes                 = 4;
        }

        // The buoyancy info's water sample array pointer and sample count
        private static class BuoyancyInfoLayout
        {
            public const int Samples = 0x00;
            public const int Count   = 0x20;
        }

        // One water sample: A 16-byte offset vector, its size, its buoyancy multiplier, its bound component, and its flag bits
        // Bit 0 marks a keel sample and bit 1 a seaplane pontoon sample, which the game also marks keel. The pontoon bit's place comes from the source's field order
        private static class WaterSampleLayout
        {
            public const int Stride     = 0x20;
            public const int Size       = 0x10;
            public const int Component  = 0x18;
            public const int Flags      = 0x1A;
            public const int KeelBit    = 1;
            public const int PontoonBit = 2;
            // The most samples the game builds for one model, and a count above it is treated as an unreadable read
            public const int CountMax = 45;
        }

        // Memory state and page protection values from the Windows VirtualQuery API. A raw access proceeds only on a committed page whose protection allows it
        private static class PageProtection
        {
            public const uint MemCommit        = 0x1000;
            public const uint NoAccess         = 0x01;
            public const uint Guard            = 0x100;
            public const uint ReadOnly         = 0x02;
            public const uint ReadWrite        = 0x04;
            public const uint WriteCopy        = 0x08;
            public const uint ExecuteRead      = 0x20;
            public const uint ExecuteReadWrite = 0x40;
            public const uint ExecuteWriteCopy = 0x80;
        }

        private List<NativeItem> rawOffsetItems = new List<NativeItem>();

        // Wraps an item at its wire site so the build check can gray out exactly the fields backed by a raw offset, leaving every API-backed field editable
        private T RawOffsetItem<T>(T item) where T : NativeItem
        {
            rawOffsetItems.Add(item);
            return item;
        }

        // Each tree disables only the items its own build added, past start, since a second pass over an item would prefix its description twice
        private void DisableRawOffsetItems(int start)
        {
            for (int i = start; i < rawOffsetItems.Count; i++)
            {
                rawOffsetItems[i].Enabled = false;
                rawOffsetItems[i].Description = DESC_UNAVAILABLE_ON_BUILD_PREFIX + rawOffsetItems[i].Description;
            }
        }

        // SpecialFlightHandlingFlags' own checkbox items and the Mode row, a subset of rawOffsetItems above
        // They are tracked separately since their version floor (RawMemory.SpecialFlightFlagsSupported) sits above the one the other raw items share
        private List<NativeItem> specialFlightFlagItems = new List<NativeItem>();

        private void DisableSpecialFlightFlagItemsIfUnsupported(int start)
        {
            if (RawMemory.SpecialFlightFlagsSupported) return;
            for (int i = start; i < specialFlightFlagItems.Count; i++)
            {
                // DisableRawOffsetItems already disabled and prefixed an item when RawMemory.RawOffsetsSupported is False, so it is never prefixed twice
                if (!specialFlightFlagItems[i].Enabled) continue;
                specialFlightFlagItems[i].Enabled = false;
                specialFlightFlagItems[i].Description = DESC_UNAVAILABLE_ON_BUILD_PREFIX + specialFlightFlagItems[i].Description;
            }
        }

        // One AdvancedData entry's own members. Each element's vtable pointer is never read or written, so moving entries only moves these 3 values
        private struct AdvancedDataEntry
        {
            public int Slot;
            public int Index;
            public float Value;
        }

        // Static, because the running build is fixed for the life of the process, and the static field tables reach these helpers with no script instance
        private enum ColliderState { Unknown, Rigid, Articulated }

        private static class RawMemory
        {
            // False when the running build predates the struct layout the offset table targets. Every raw path below is inert while it is False
            private static bool rawOffsetsSupported = true;
            // A higher threshold than rawOffsetsSupported, since SpecialFlightHandlingFlags' own offset does not exist at all below this build
            private static bool specialFlightFlagsSupported = true;
            // Byte offset of the vehicles.meta flag bitset inside a vehicle's model info, read from the API core at startup, and 0 when it could not be read
            private static int modelFlagBaseOffset;
            // FLAG_HAS_REAR_MOUNTED_TURRET per model hash, read once per model
            private static readonly Dictionary<int, bool> rearMountedTurretByModel = new Dictionary<int, bool>();
            // FLAG_HAS_GLIDER per model hash, read once per model
            private static readonly Dictionary<int, bool> gliderByModel = new Dictionary<int, bool>();
            // Each distinct failure signature (offset/address plus reason) gets its own one-time warning, rather than one global latch silencing a second failure
            private static List<string> rawMemoryAccessWarningPending = new List<string>();
            private static HashSet<string> rawMemoryAccessWarningLogged = new HashSet<string>();

            public static bool RawOffsetsSupported { get { return rawOffsetsSupported; } }
            public static bool SpecialFlightFlagsSupported { get { return specialFlightFlagsSupported; } }
            public static bool HasPendingAccessWarnings { get { return rawMemoryAccessWarningPending.Count > 0; } }

            // The running build decides both gates, so they are set once per script load, before any raw access
            public static void ConfigureBuild(bool supportsRawOffsets, bool supportsSpecialFlightFlags)
            {
                rawOffsetsSupported = supportsRawOffsets;
                specialFlightFlagsSupported = supportsSpecialFlightFlags;
            }

            // A prior load's warning dedupe would silence a genuine repeat failure in the new session's log
            // The rear-mounted turret and glider caches also start empty with each load
            public static void ResetForScriptLoad()
            {
                rawMemoryAccessWarningPending.Clear();
                rawMemoryAccessWarningLogged.Clear();
                rearMountedTurretByModel.Clear();
                gliderByModel.Clear();
            }

            // Hands over every warning queued since the last call and marks each one logged, so the same failure is never queued again
            public static List<string> TakeAccessWarnings()
            {
                var warnings = new List<string>(rawMemoryAccessWarningPending);
                for (int i = 0; i < warnings.Count; i++) rawMemoryAccessWarningLogged.Add(warnings[i]);
                rawMemoryAccessWarningPending.Clear();
                return warnings;
            }

            // One region VirtualQuery reported, so every later request inside it skips the kernel call, whatever object it reads
            // A query sizes its region by walking the pages, which takes milliseconds on the game's large heap regions, so a region is queried once per script load
            // The game's heap regions stay mapped for the life of the process, and an access violation evicts the region holding the address (EvictRawRegion)
            private struct RawRegionCacheEntry { public ulong RegionStart; public ulong RegionSize; public bool Writable; }
            private static readonly List<RawRegionCacheEntry> rawRegionCache = new List<RawRegionCacheEntry>();

            // One shared implementation validates a fresh VirtualQuery result and tests a cache hit, so the two checks can never drift apart
            private static bool IsSpanWithinRegion(ulong regionStart, ulong regionSize, ulong requestedStart, ulong requestedSize)
            {
                return requestedStart >= regionStart && requestedStart - regionStart <= regionSize && requestedSize <= regionSize - (requestedStart - regionStart);
            }

            // An entry whose region holds the whole span and allows the direction, or -1
            private static int FindRawRegion(ulong requestedStart, ulong requestedSize, bool write)
            {
                for (int i = 0; i < rawRegionCache.Count; i++)
                {
                    RawRegionCacheEntry entry = rawRegionCache[i];
                    if (write && !entry.Writable) continue;
                    if (IsSpanWithinRegion(entry.RegionStart, entry.RegionSize, requestedStart, requestedSize)) return i;
                }
                return -1;
            }

            // A fresh query replaces the entry for the same region, so the list holds each region once
            private static void StoreRawRegion(ulong regionStart, ulong regionSize, bool writable)
            {
                var entry = new RawRegionCacheEntry { RegionStart = regionStart, RegionSize = regionSize, Writable = writable };
                for (int i = 0; i < rawRegionCache.Count; i++)
                {
                    if (rawRegionCache[i].RegionStart != regionStart) continue;
                    rawRegionCache[i] = entry;
                    return;
                }
                rawRegionCache.Add(entry);
            }

            private static void EvictRawRegion(IntPtr address)
            {
                ulong violatingAddress = unchecked((ulong)address.ToInt64());
                rawRegionCache.RemoveAll(entry => violatingAddress >= entry.RegionStart && violatingAddress - entry.RegionStart < entry.RegionSize);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MemoryBasicInformation
            {
                public IntPtr BaseAddress;
                public IntPtr AllocationBase;
                public uint AllocationProtect;
                public UIntPtr RegionSize;
                public uint State;
                public uint Protect;
                public uint Type;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

            // The API core keeps the flag bitset's offset in an assembly scripts do not compile against, so it is read by reflection once at startup
            // A failure leaves the offset 0 so every flag gate stays open, and returns its reason for the caller's WARN
            // The bitset is an array of 32-bit words, but on one edition the API reads the offset from a byte test of its second byte
            // Rounding down to a word gives the real base
            public static int ResolveModelFlagBaseOffset(out int apiOffset, out string failureReason)
            {
                apiOffset = 0;
                failureReason = null;
                try
                {
                    Type vehicleMemory = null;
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        vehicleMemory = assembly.GetType(CORE_VEHICLE_MEMORY_TYPE, false);
                        if (vehicleMemory != null) break;
                    }
                    if (vehicleMemory == null)
                    {
                        failureReason = "type " + CORE_VEHICLE_MEMORY_TYPE + " not found";
                        return 0;
                    }
                    System.Reflection.PropertyInfo property = vehicleMemory.GetProperty(CORE_FLAG_OFFSET_PROPERTY, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (property == null)
                    {
                        failureReason = "property " + CORE_FLAG_OFFSET_PROPERTY + " not found";
                        return 0;
                    }
                    apiOffset = (int)property.GetValue(null, null);
                    if (apiOffset <= 0)
                    {
                        failureReason = "offset reads " + apiOffset;
                        return 0;
                    }
                    int offset = apiOffset / ModelInfoLayout.FlagWordBytes * ModelInfoLayout.FlagWordBytes;
                    modelFlagBaseOffset = offset;
                    return offset;
                }
                catch (Exception ex)
                {
                    failureReason = ex.GetType().Name + ": " + ex.Message;
                    return 0;
                }
            }

            private static bool ReadModelFlag(Vehicle vehicle, int flagIndex, out bool isSet)
            {
                isSet = false;
                if (modelFlagBaseOffset <= 0 || Entity.IsNullOrNotExisting(vehicle)) return false;
                IntPtr modelInfo;
                if (!ReadRawIntPtr(vehicle.MemoryAddress, EntityLayout.ModelInfo, out modelInfo) || modelInfo == IntPtr.Zero) return false;
                int word;
                if (!ReadRawInt32(modelInfo, modelFlagBaseOffset + flagIndex / ModelInfoLayout.FlagsPerWord * sizeof(int), out word)) return false;
                isSet = (word & (1 << (flagIndex % ModelInfoLayout.FlagsPerWord))) != 0;
                return true;
            }

            // Unknown when the build is unsupported or any pointer in the chain is missing, so a caller never acts on a guess
            public static ColliderState ReadColliderState(Vehicle vehicle)
            {
                if (!rawOffsetsSupported || Entity.IsNullOrNotExisting(vehicle)) return ColliderState.Unknown;
                IntPtr physicsInstance;
                IntPtr cacheEntry;
                IntPtr collider;
                int type;
                if (!ReadRawIntPtr(vehicle.MemoryAddress, EntityLayout.PhysicsInstance, out physicsInstance) || physicsInstance == IntPtr.Zero) return ColliderState.Unknown;
                if (!ReadRawIntPtr(physicsInstance, PhysicsInstanceLayout.CacheEntry, out cacheEntry) || cacheEntry == IntPtr.Zero) return ColliderState.Unknown;
                if (!ReadRawIntPtr(cacheEntry, CacheEntryLayout.Collider, out collider) || collider == IntPtr.Zero) return ColliderState.Unknown;
                if (!ReadRawInt32(collider, Game.isEnhanced ? ColliderLayout.TypeEnhanced : ColliderLayout.Type, out type)) return ColliderState.Unknown;
                if (type < ColliderLayout.TypeRigid || type > ColliderLayout.TypeMax) return ColliderState.Unknown;
                return type == ColliderLayout.TypeRigid ? ColliderState.Rigid : ColliderState.Articulated;
            }

            // Only a model with FLAG_HAS_REAR_MOUNTED_TURRET reads TurretPitchLimitData
            // True when no vehicle is given or the flag cannot be read, so nothing is hidden on a guess
            public static bool HasRearMountedTurret(Vehicle vehicle)
            {
                if (Entity.IsNullOrNotExisting(vehicle)) return true;
                int modelHash = vehicle.Model.Hash;
                bool cached;
                if (rearMountedTurretByModel.TryGetValue(modelHash, out cached)) return cached;
                bool isSet;
                if (!ReadModelFlag(vehicle, ModelInfoLayout.FlagHasRearMountedTurretIndex, out isSet)) return true;
                rearMountedTurretByModel[modelHash] = isSet;
                return isSet;
            }

            // False when no vehicle is given or the flag cannot be read, since a glider call made on a guess would reach a vehicle with no wings
            public static bool HasGlider(Vehicle vehicle)
            {
                if (Entity.IsNullOrNotExisting(vehicle)) return false;
                int modelHash = vehicle.Model.Hash;
                bool cached;
                if (gliderByModel.TryGetValue(modelHash, out cached)) return cached;
                bool isSet;
                if (!ReadModelFlag(vehicle, ModelInfoLayout.FlagHasGliderIndex, out isSet)) return false;
                gliderByModel[modelHash] = isSet;
                return isSet;
            }

            public static void WriteDerivedFloat(HandlingData handling, int offset, float value)
            {
                if (!rawOffsetsSupported || handling == null || !handling.IsValid) return;
                WriteRawInt32(handling.MemoryAddress, offset, SingleToInt32Bits(value));
            }

            private static bool IsReadableProtection(uint protection)
            {
                return protection == PageProtection.ReadOnly || protection == PageProtection.ReadWrite || protection == PageProtection.WriteCopy ||
                    protection == PageProtection.ExecuteRead || protection == PageProtection.ExecuteReadWrite || protection == PageProtection.ExecuteWriteCopy;
            }

            private static bool IsWritableProtection(uint protection)
            {
                return protection == PageProtection.ReadWrite || protection == PageProtection.WriteCopy || protection == PageProtection.ExecuteReadWrite || protection == PageProtection.ExecuteWriteCopy;
            }

            // Built only on a failure branch, so every successful raw read/write skips this string work entirely
            private static string RawOffsetFailureDetail(int offset, bool write, string reason) { return "Offset=0x" + offset.ToString("X") + " | " + (write ? "Write" : "Read") + " | Reason=" + reason; }

            private static void NoteRawMemoryAccessFailure(string detail)
            {
                if (rawMemoryAccessWarningLogged.Contains(detail)) return;
                if (!rawMemoryAccessWarningPending.Contains(detail)) rawMemoryAccessWarningPending.Add(detail);
            }

            // Validates the complete requested span in one committed memory region, not merely the starting address
            // A game allocation can end between adjacent fields, so accepting a readable start alone would still leave the Marshal operation unsafe
            private static bool GetRawMemoryAddress(IntPtr baseAddress, int offset, int byteCount, bool write, out IntPtr address)
            {
                address = IntPtr.Zero;
                if (baseAddress == IntPtr.Zero || offset < 0 || byteCount <= 0)
                {
                    NoteRawMemoryAccessFailure(RawOffsetFailureDetail(offset, write, "invalid request parameters"));
                    return false;
                }
                long addressValue;
                try { addressValue = checked(baseAddress.ToInt64() + offset); }
                catch (OverflowException)
                {
                    NoteRawMemoryAccessFailure(RawOffsetFailureDetail(offset, write, "address overflow"));
                    return false;
                }
                if (addressValue == 0)
                {
                    NoteRawMemoryAccessFailure(RawOffsetFailureDetail(offset, write, "resolved address is zero"));
                    return false;
                }
                ulong requestedStart = unchecked((ulong)addressValue);
                ulong requestedSize = (uint)byteCount;
                if (FindRawRegion(requestedStart, requestedSize, write) >= 0)
                {
                    address = new IntPtr(addressValue);
                    return true;
                }
                MemoryBasicInformation region;
                UIntPtr result = VirtualQuery(new IntPtr(addressValue), out region, new UIntPtr((uint)Marshal.SizeOf(typeof(MemoryBasicInformation))));
                if (result == UIntPtr.Zero || region.State != PageProtection.MemCommit || (region.Protect & (PageProtection.NoAccess | PageProtection.Guard)) != 0)
                {
                    NoteRawMemoryAccessFailure(RawOffsetFailureDetail(offset, write, "region uncommitted or unreadable"));
                    return false;
                }
                uint protection = region.Protect & 0xFF;
                if (write ? !IsWritableProtection(protection) : !IsReadableProtection(protection))
                {
                    NoteRawMemoryAccessFailure(RawOffsetFailureDetail(offset, write, "region protection does not allow " + (write ? "write" : "read")));
                    return false;
                }
                ulong regionStart = unchecked((ulong)region.BaseAddress.ToInt64());
                ulong regionSize = region.RegionSize.ToUInt64();
                if (!IsSpanWithinRegion(regionStart, regionSize, requestedStart, requestedSize))
                {
                    NoteRawMemoryAccessFailure(RawOffsetFailureDetail(offset, write, "requested span exceeds region bounds"));
                    return false;
                }
                address = new IntPtr(addressValue);
                StoreRawRegion(regionStart, regionSize, IsWritableProtection(protection));
                return true;
            }

            [HandleProcessCorruptedStateExceptions]
            private static bool ReadInt32At(IntPtr address, out int value)
            {
                value = 0;
                try
                {
                    value = Marshal.ReadInt32(address);
                    return true;
                }
                catch (AccessViolationException)
                {
                    EvictRawRegion(address);
                    NoteRawMemoryAccessFailure("address 0x" + address.ToInt64().ToString("X") + "; ReadInt32 access violation");
                    return false;
                }
            }

            [HandleProcessCorruptedStateExceptions]
            private static bool WriteInt32At(IntPtr address, int value)
            {
                try
                {
                    Marshal.WriteInt32(address, value);
                    return true;
                }
                catch (AccessViolationException)
                {
                    EvictRawRegion(address);
                    NoteRawMemoryAccessFailure("address 0x" + address.ToInt64().ToString("X") + "; WriteInt32 access violation");
                    return false;
                }
            }

            [HandleProcessCorruptedStateExceptions]
            private static bool WriteInt16At(IntPtr address, short value)
            {
                try
                {
                    Marshal.WriteInt16(address, value);
                    return true;
                }
                catch (AccessViolationException)
                {
                    EvictRawRegion(address);
                    NoteRawMemoryAccessFailure("address 0x" + address.ToInt64().ToString("X") + "; WriteInt16 access violation");
                    return false;
                }
            }

            [HandleProcessCorruptedStateExceptions]
            private static bool ReadInt16At(IntPtr address, out short value)
            {
                value = 0;
                try
                {
                    value = Marshal.ReadInt16(address);
                    return true;
                }
                catch (AccessViolationException)
                {
                    EvictRawRegion(address);
                    NoteRawMemoryAccessFailure("address 0x" + address.ToInt64().ToString("X") + "; ReadInt16 access violation");
                    return false;
                }
            }

            [HandleProcessCorruptedStateExceptions]
            private static bool ReadIntPtrAt(IntPtr address, out IntPtr value)
            {
                value = IntPtr.Zero;
                try
                {
                    value = Marshal.ReadIntPtr(address);
                    return true;
                }
                catch (AccessViolationException)
                {
                    EvictRawRegion(address);
                    NoteRawMemoryAccessFailure("address 0x" + address.ToInt64().ToString("X") + "; ReadIntPtr access violation");
                    return false;
                }
            }

            // Read/write for fields with no API property at all. Offsets are the game's own struct offsets, relative to the owning object's own MemoryAddress
            // Assumes the modern (v1.0.1180.2+) game layout, the one the fixed offset table above targets
            public static bool ReadRawInt32(IntPtr baseAddress, int offset, out int value)
            {
                value = 0;
                IntPtr address;
                return GetRawMemoryAddress(baseAddress, offset, sizeof(int), false, out address) && ReadInt32At(address, out value);
            }

            private static bool WriteRawInt32(IntPtr baseAddress, int offset, int value)
            {
                IntPtr address;
                return GetRawMemoryAddress(baseAddress, offset, sizeof(int), true, out address) && WriteInt32At(address, value);
            }

            public static bool ReadRawInt16(IntPtr baseAddress, int offset, out short value)
            {
                value = 0;
                IntPtr address;
                return GetRawMemoryAddress(baseAddress, offset, sizeof(short), false, out address) && ReadInt16At(address, out value);
            }

            private static bool WriteRawInt16(IntPtr baseAddress, int offset, short value)
            {
                IntPtr address;
                return GetRawMemoryAddress(baseAddress, offset, sizeof(short), true, out address) && WriteInt16At(address, value);
            }

            public static bool ReadRawIntPtr(IntPtr baseAddress, int offset, out IntPtr value)
            {
                value = IntPtr.Zero;
                IntPtr address;
                return GetRawMemoryAddress(baseAddress, offset, IntPtr.Size, false, out address) && ReadIntPtrAt(address, out value);
            }

            // One 4 byte value seen as both an int and a float, so a float's bits convert with no allocation, where BitConverter allocates an array per call
            [StructLayout(LayoutKind.Explicit)]
            private struct FloatBits
            {
                [FieldOffset(0)] public int Bits;
                [FieldOffset(0)] public float Value;
            }

            private static float Int32BitsToSingle(int bits) { return new FloatBits { Bits = bits }.Value; }
            private static int SingleToInt32Bits(float value) { return new FloatBits { Value = value }.Bits; }

            public static float ReadRawFloat(IntPtr baseAddress, int offset)
            {
                if (!rawOffsetsSupported) return 0.0f;
                int value;
                return ReadRawInt32(baseAddress, offset, out value) ? Int32BitsToSingle(value) : 0.0f;
            }

            public static bool ReadRawFloat(IntPtr baseAddress, int offset, out float value)
            {
                value = 0.0f;
                int bits;
                if (!rawOffsetsSupported || !ReadRawInt32(baseAddress, offset, out bits)) return false;
                value = Int32BitsToSingle(bits);
                return true;
            }

            // The 3 floats are validated as one span, so a vector costs one region check instead of 3
            public static bool ReadRawVector3(IntPtr baseAddress, int offset, out Vector3 value)
            {
                value = Vector3.Zero;
                IntPtr address;
                if (!rawOffsetsSupported || !GetRawMemoryAddress(baseAddress, offset, 3 * sizeof(float), false, out address)) return false;
                return ReadVector3At(address, out value);
            }

            // The first 3 floats of each of 4 columns spaced columnStride apart, validated as one span from the first column to the end of the last
            public static bool ReadRawMatrixColumns(IntPtr baseAddress, int offset, int columnStride, out Vector3 column0, out Vector3 column1, out Vector3 column2, out Vector3 column3)
            {
                column0 = column1 = column2 = column3 = Vector3.Zero;
                IntPtr address;
                if (!rawOffsetsSupported || !GetRawMemoryAddress(baseAddress, offset, 3 * columnStride + 3 * sizeof(float), false, out address)) return false;
                return ReadVector3At(address, out column0) && ReadVector3At(address + columnStride, out column1) && ReadVector3At(address + 2 * columnStride, out column2)
                    && ReadVector3At(address + 3 * columnStride, out column3);
            }

            private static bool ReadVector3At(IntPtr address, out Vector3 value)
            {
                value = Vector3.Zero;
                int x, y, z;
                if (!ReadInt32At(address, out x) || !ReadInt32At(address + 4, out y) || !ReadInt32At(address + 8, out z)) return false;
                value = new Vector3(Int32BitsToSingle(x), Int32BitsToSingle(y), Int32BitsToSingle(z));
                return true;
            }

            public static void WriteRawFloat(IntPtr baseAddress, int offset, float value)
            {
                if (rawOffsetsSupported) WriteRawInt32(baseAddress, offset, SingleToInt32Bits(value));
            }

            // Genuine int fields at a raw offset (e.g. SeaPlane pontoon component IDs), with no bit-reinterpretation, unlike ReadRawFloat/WriteRawFloat
            public static int ReadRawInt(IntPtr baseAddress, int offset)
            {
                if (!rawOffsetsSupported) return 0;
                int value;
                return ReadRawInt32(baseAddress, offset, out value) ? value : 0;
            }

            public static void WriteRawInt(IntPtr baseAddress, int offset, int value)
            {
                if (rawOffsetsSupported) WriteRawInt32(baseAddress, offset, value);
            }

            // Genuine uint field at a raw offset (SpecialFlightHandlingFlags and SpecialFlightHandlingData's Mode), with whole-value semantics like ReadRawInt/WriteRawInt
            // Gated the same way plus the narrower specialFlightFlagsSupported check, since neither offset exists below that build
            public static uint ReadRawUInt(IntPtr baseAddress, int offset)
            {
                if (!rawOffsetsSupported || !specialFlightFlagsSupported) return 0;
                int value;
                return ReadRawInt32(baseAddress, offset, out value) ? unchecked((uint)value) : 0;
            }

            public static void WriteRawUInt(IntPtr baseAddress, int offset, uint value)
            {
                if (rawOffsetsSupported && specialFlightFlagsSupported) WriteRawInt32(baseAddress, offset, unchecked((int)value));
            }

            public static void WriteRawUIntBit(IntPtr baseAddress, int offset, int bitIndex, bool value)
            {
                uint current = ReadRawUInt(baseAddress, offset);
                uint next = value ? current | (1u << bitIndex) : current & ~(1u << bitIndex);
                WriteRawUInt(baseAddress, offset, next);
            }

            // Hover mode slides the hover bone every frame while the upside-down flag is off, so a vehicle without that bone crashes the game
            // Returns True only when it turned the flag on, so the caller reports it. Never logs, like every other RawMemory function
            public static bool ForceWorksUpsideDownWithoutHoverBone(IntPtr specialFlightAddress, Vehicle vehicle)
            {
                if (!rawOffsetsSupported || !specialFlightFlagsSupported || specialFlightAddress == IntPtr.Zero || Entity.IsNullOrNotExisting(vehicle)) return false;
                if (vehicle.Bones.Contains(HOVER_SLIDE_BONE_NAME)) return false;
                if ((ReadRawUInt(specialFlightAddress, SpecialFlightLayout.Flags) & (1u << SF_WORKS_UPSIDE_DOWN_BIT)) != 0) return false;
                WriteRawUIntBit(specialFlightAddress, SpecialFlightLayout.Flags, SF_WORKS_UPSIDE_DOWN_BIT, true);
                return true;
            }

            // Flag bitmask read/write remains independent of rawOffsetsSupported
            // These offsets use the API-backed flag layout rather than the version-gated raw-field table
            public static uint ReadFlagBits(IntPtr baseAddress, int offset)
            {
                int value;
                return ReadRawInt32(baseAddress, offset, out value) ? (uint)value : 0;
            }

            public static void WriteFlagBit(IntPtr baseAddress, int offset, int bitIndex, bool value)
            {
                int currentValue;
                if (!ReadRawInt32(baseAddress, offset, out currentValue)) return;
                uint current = (uint)currentValue;
                uint next = value ? current | (1u << bitIndex) : current & ~(1u << bitIndex);
                WriteRawInt32(baseAddress, offset, unchecked((int)next));
            }

            public static void WriteFlagBits(IntPtr baseAddress, int offset, uint value)
            {
                WriteRawInt32(baseAddress, offset, unchecked((int)value));
            }

            // TractionCurveMaxRatio_ depends on TractionCurveMax and TractionCurveRatio_ on both it and TractionCurveMin, so both recompute when either changes
            // A divisor of 0 or less stores HandlingDataLayout.LargeFloat, the same value the game stores when it loads those values from handling.meta
            public static void RecomputeTractionCurveRatios(HandlingData handling)
            {
                WriteDerivedFloat(handling, HandlingDataLayout.TractionCurveMaxRatio, handling.TractionCurveMax > 0.0f ? 1.0f / handling.TractionCurveMax : HandlingDataLayout.LargeFloat);
                float range = handling.TractionCurveMax - handling.TractionCurveMin;
                WriteDerivedFloat(handling, HandlingDataLayout.TractionCurveRatio, range > 0.0f ? 1.0f / range : HandlingDataLayout.LargeFloat);
            }

            // Byte offset of one TurretPitchLimitData member for one turret slot, relative to the Weapon data's own MemoryAddress
            public static int TurretPitchLimitOffset(int slotIndex, int fieldIndex) { return WeaponLayout.TurretPitchLimitData + slotIndex * WeaponLayout.TurretPitchLimitEntryStride + WeaponLayout.TurretPitchLimitFirstField + fieldIndex * FLOAT_FIELD_STRIDE; }
            public static float[] ReadTurretPitchLimits(IntPtr baseAddress, int slotIndex)
            {
                var values = new float[TurretPitchLimitFields.Length];
                for (int i = 0; i < values.Length; i++) values[i] = ReadRawFloat(baseAddress, TurretPitchLimitOffset(slotIndex, i));
                return values;
            }

            public static void WriteTurretPitchLimits(IntPtr baseAddress, int slotIndex, float[] values)
            {
                for (int i = 0; i < values.Length; i++) WriteRawFloat(baseAddress, TurretPitchLimitOffset(slotIndex, i), values[i]);
            }

            // Re-derives the live AdvancedData entry count on every call
            // A count beyond capacity or the sanity ceiling is treated as unreadable and reported as empty
            public static int GetAdvancedDataCount(HandlingData handling)
            {
                if (!rawOffsetsSupported || handling == null || handling.CarHandlingData == null) return 0;
                IntPtr carAddress = handling.CarHandlingData.MemoryAddress;
                if (carAddress == IntPtr.Zero) return 0;
                short countValue;
                short capacityValue;
                if (!ReadRawInt16(carAddress, CarLayout.AdvancedDataArray + AdvancedDataLayout.ArrayCount, out countValue) ||
                    !ReadRawInt16(carAddress, CarLayout.AdvancedDataArray + AdvancedDataLayout.ArrayCapacity, out capacityValue)) return 0;
                int count = (ushort)countValue;
                int capacity = (ushort)capacityValue;
                if (count > capacity || count > ADVANCEDDATA_COUNT_SANITY_CEILING) return 0;
                return count;
            }

            // The array's allocated element count, which a count can move up to without reallocating
            // The array frees and destructs by capacity, not by count, and the game reads the count live
            public static int GetAdvancedDataCapacity(HandlingData handling)
            {
                if (!rawOffsetsSupported || handling == null || handling.CarHandlingData == null) return 0;
                IntPtr carAddress = handling.CarHandlingData.MemoryAddress;
                if (carAddress == IntPtr.Zero) return 0;
                short capacityValue;
                if (!ReadRawInt16(carAddress, CarLayout.AdvancedDataArray + AdvancedDataLayout.ArrayCapacity, out capacityValue)) return 0;
                int capacity = (ushort)capacityValue;
                return capacity > ADVANCEDDATA_COUNT_SANITY_CEILING ? 0 : capacity;
            }

            public static bool SetAdvancedDataCount(HandlingData handling, int newCount)
            {
                if (!rawOffsetsSupported || handling == null || handling.CarHandlingData == null) return false;
                if (newCount < 0 || newCount > GetAdvancedDataCapacity(handling)) return false;
                IntPtr carAddress = handling.CarHandlingData.MemoryAddress;
                return carAddress != IntPtr.Zero && WriteRawInt16(carAddress, CarLayout.AdvancedDataArray + AdvancedDataLayout.ArrayCount, (short)newCount);
            }

            public static bool ReadAdvancedDataEntry(HandlingData handling, int index, out AdvancedDataEntry entry)
            {
                entry = new AdvancedDataEntry();
                if (!rawOffsetsSupported) return false;
                IntPtr element = GetAdvancedDataElementAddress(handling, index);
                if (element == IntPtr.Zero) return false;
                int valueBits;
                if (!ReadRawInt32(element, AdvancedDataLayout.ElementSlot, out entry.Slot) || !ReadRawInt32(element, AdvancedDataLayout.ElementIndex, out entry.Index) ||
                    !ReadRawInt32(element, AdvancedDataLayout.ElementValue, out valueBits)) return false;
                entry.Value = Int32BitsToSingle(valueBits);
                return true;
            }

            public static bool WriteAdvancedDataEntry(HandlingData handling, int index, AdvancedDataEntry entry)
            {
                if (!rawOffsetsSupported) return false;
                IntPtr element = GetAdvancedDataElementAddress(handling, index);
                if (element == IntPtr.Zero) return false;
                return WriteRawInt32(element, AdvancedDataLayout.ElementSlot, entry.Slot) && WriteRawInt32(element, AdvancedDataLayout.ElementIndex, entry.Index) &&
                    WriteRawInt32(element, AdvancedDataLayout.ElementValue, SingleToInt32Bits(entry.Value));
            }

            // Returns the address of the index'th AdvancedData element, or IntPtr.Zero when out of range, re-deriving the array pointer fresh every call
            public static IntPtr GetAdvancedDataElementAddress(HandlingData handling, int index) { return GetAdvancedDataElementAddress(handling, index, GetAdvancedDataCount(handling)); }

            // count is the entry count the caller already read this access, so a guarded getter/setter never reads it twice
            public static IntPtr GetAdvancedDataElementAddress(HandlingData handling, int index, int count)
            {
                if (index < 0 || index >= count) return IntPtr.Zero;
                IntPtr carAddress = handling.CarHandlingData.MemoryAddress;
                IntPtr arrayPointer;
                if (!ReadRawIntPtr(carAddress, CarLayout.AdvancedDataArray, out arrayPointer)) return IntPtr.Zero;
                if (arrayPointer == IntPtr.Zero) return IntPtr.Zero;
                try { return new IntPtr(checked(arrayPointer.ToInt64() + index * AdvancedDataLayout.ElementStride)); }
                catch (OverflowException)
                {
                    NoteRawMemoryAccessFailure("AdvancedData element index " + index + "; address overflow");
                    return IntPtr.Zero;
                }
            }
        }
    }
}
