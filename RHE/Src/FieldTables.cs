using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Every wiring path reads these same members, so a row's rules hold on the live handling, an export-only element, and a pending AdvancedData element alike
        private class FieldEntry
        {
            public string Tag;
            public string Title;
            // The value a newly created export-only category starts this field at, and what a live row shows while its category is absent. An int row takes its whole part
            public float DefaultFloat;
            // The vector counterpart of DefaultFloat, for a row that holds a vector
            public Vector3 DefaultVector;
            public float Step = DEFAULT_FLOAT_STEP;
            public int StepInt = DEFAULT_INT_STEP;
            // Keeps the full Step near 0 instead of shrinking it, for a field whose setter snaps small values back to 0
            public bool FixedStep;
            // A field the game copies into the vehicle at spawn and that RefreshSpawnCopiedFields re-reads, so an edit refreshes the edited vehicle
            public bool RefreshesVehicle;
            // A field the game only reads when the vehicle spawns, so an edit requests an auto respawn of the edited vehicle
            public bool RequiresRespawn;
            // Replaces the plain rounded step for a field whose setter snaps a range of values. Null for every other field
            public Func<float, double, LemonUI.Menus.Direction, float> StepOverride;
            // Picks the step from the live handling for a field whose useful step depends on another setting. Null uses Step
            public Func<HandlingData, float> StepResolver;
            // The range Left/Right cycling stops at. Typed entry, applied handlings, undo, and Selective Copy may go past it. Null on a side with no bound
            public float? CycleMin;
            public float? CycleMax;
            // Builds text that leads the field's description from its current value. Null for every field without one
            public Func<float, string> DescriptionPrefix;
            // A confirmed game-crash boundary, or the limit past which the game's own result breaks, enforced on every write path whenever set
            // Typing, cycling, undo, Selective Copy, applied handlings, and new export-only defaults all hold it. Unset (null) for every field with no known boundary
            public float? HardMin;
            public int? HardMinInt;
            public int? HardMaxInt;
            // The smallest absolute value a field may hold, for a divisor where only values near 0 are invalid
            // A smaller value moves to the edge of that dead zone (ApplyMinimumMagnitude). Null for every field without one
            public float? MinimumMagnitude;
            // A live store registers the row through RawOffsetItem, so a build without RawMemory.RawOffsetsSupported grays it out instead of silently doing nothing
            // The file walkers skip the row on that build too
            public bool RawOffset;
            // A raw row whose offset exists only on a build with RawMemory.SpecialFlightFlagsSupported, a higher floor than RawOffset's
            // A live store grays it out on an older build, and the file walkers skip it there, since it reads 0 and a save would write that out as real
            public bool RequiresSpecialFlightFlagsBuild;
            // A field the game reads only on a vehicle with rocket boost
            // It is hidden from the editor, left out of a save, and not written when a handling is applied to any other vehicle
            public bool RequiresRocketBoost;
            // A field the game reads only on a model with a rear mounted turret, so Selective Copy refuses it for any other vehicle
            public bool RequiresRearMountedTurret;
            // The visualization elements this row's value shapes, which Isolated Drawing builds in while the row is highlighted
            // Disabled for every row that no element draws
            public HandlingVisualizationElements VisualizedElements;
            // True for a row that holds an integer, so a copy into an export-only element writes an integer attribute instead of a float one
            public virtual bool IsInt { get { return false; } }
            // True for a row that holds a vector, which an export-only menu lists as one row per axis
            public virtual bool IsVector { get { return false; } }
            // Counts the row's accessor groups for the startup check, which only a row that carries accessors has
            public virtual void CountAccessorGroups(out int completeGroups, out int halfSetGroups)
            {
                completeGroups = 0;
                halfSetGroups = 0;
            }
        }

        // Exactly one of the Float/Vec/Int delegate pairs is populated per row
        // Getter and setter closures carry any unit conversion or derived-value side effect, so the generic walkers never need a field's conversion factor
        private class HandlingFieldEntry<TData> : FieldEntry
        {
            public Func<TData, float> GetFloat;
            public Action<TData, float> SetFloat;
            public Func<TData, Vector3> GetVec;
            public Action<TData, Vector3> SetVec;
            public Func<TData, int> GetInt;
            public Action<TData, int> SetInt;
            // A post-write side effect on the vehicle itself (e.g. pushing CenterOfMassOffset into the vehicle's real CenterOfGravityOffset)
            // Runs after every write with the vehicle that write resolved, whether from a menu edit, a replay, drift reassertion, or an applied handling
            // Null for every field with no such effect
            public Action<TData, Vehicle> Recompute;
            public override bool IsInt { get { return GetInt != null || SetInt != null; } }
            public override bool IsVector { get { return GetVec != null || SetVec != null; } }
            public override void CountAccessorGroups(out int completeGroups, out int halfSetGroups)
            {
                completeGroups = CountCompleteAccessorPair(GetFloat, SetFloat) + CountCompleteAccessorPair(GetVec, SetVec) + CountCompleteAccessorPair(GetInt, SetInt);
                halfSetGroups = CountHalfSetAccessorPair(GetFloat, SetFloat) + CountHalfSetAccessorPair(GetVec, SetVec) + CountHalfSetAccessorPair(GetInt, SetInt);
            }
        }

        // Moves a value inside the dead zone around 0 to the zone's edge on the side of its own sign
        // Exactly 0 goes to the side a cycle step moves toward (direction -1 or 1), else positive
        private static float ApplyMinimumMagnitude(float value, float? minimumMagnitude, int direction)
        {
            if (!minimumMagnitude.HasValue || Math.Abs(value) >= minimumMagnitude.Value) return value;
            bool negative = value < 0.0f || (value == 0.0f && direction < 0);
            return negative ? -minimumMagnitude.Value : minimumMagnitude.Value;
        }

        // direction is the press direction (-1 or 1), or 0 for a write with none, and only decides which edge of the dead zone a value of exactly 0 moves to
        private static float ApplyHardBounds(FieldEntry entry, float value, int direction)
        {
            if (entry.HardMin.HasValue && value < entry.HardMin.Value) value = entry.HardMin.Value;
            return ApplyMinimumMagnitude(value, entry.MinimumMagnitude, direction);
        }

        private static int ApplyHardBounds(FieldEntry entry, int value)
        {
            if (entry.HardMinInt.HasValue && value < entry.HardMinInt.Value) return entry.HardMinInt.Value;
            if (entry.HardMaxInt.HasValue && value > entry.HardMaxInt.Value) return entry.HardMaxInt.Value;
            return value;
        }

        // Held to Left/Right cycling only, so a typed value or a replay can go past the range
        private static float ApplyCycleBounds(FieldEntry entry, float value)
        {
            if (!entry.CycleMin.HasValue && !entry.CycleMax.HasValue) return value;
            return Math.Max(entry.CycleMin ?? float.MinValue, Math.Min(entry.CycleMax ?? float.MaxValue, value));
        }

        private static uint BuildFlagMask(FlagNameEntry[] entries, Func<FlagNameEntry, bool> predicate)
        {
            uint mask = 0;
            for (int i = 0; i < entries.Length; i++) if (predicate(entries[i])) mask |= 1u << entries[i].BitIndex;
            return mask;
        }

        // A row with zero or more than one accessor group, or a group missing its getter or setter, would silently drop or half-wire its field with no exception
        // This logs an ERROR at startup instead
        private void ValidateFieldEntries()
        {
            ValidateFieldEntryArray("Base", BaseFields);
            var validatedTables = new List<FieldEntry[]>();
            foreach (CategoryDefinition definition in CategoryDefinitions)
            {
                // Flying and VerticalFlying share one table, so one pass covers both
                if (validatedTables.Contains(definition.Fields)) continue;
                validatedTables.Add(definition.Fields);
                ValidateFieldEntryArray(definition.Name, definition.Fields);
            }
        }

        private void ValidateFieldEntryArray(string category, FieldEntry[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                FieldEntry entry = fields[i];
                // The menu and the save read only the getter and every write uses the setter, so a group with one of the two never works
                int completeGroups;
                int halfSetGroups;
                entry.CountAccessorGroups(out completeGroups, out halfSetGroups);
                if (completeGroups != 1 || halfSetGroups != 0) logger.Log(Dbg.Level.Error, Dbg.FieldEntryMalformed(category, entry.Tag, completeGroups, halfSetGroups));
            }
        }

        private static int CountCompleteAccessorPair(Delegate getter, Delegate setter) { return getter != null && setter != null ? 1 : 0; }
        private static int CountHalfSetAccessorPair(Delegate getter, Delegate setter) { return (getter != null) != (setter != null) ? 1 : 0; }

        // A flag row with a bit outside the 32 bit value, a bit or name shared with another row, or a hub that has no group misbehaves with no exception
        // These are logged at ERROR on startup instead
        private void ValidateFlagEntryTables()
        {
            var seenNames = new HashSet<string>();
            foreach (FlagFamily family in FlagFamilies) ValidateFlagEntryTable(family.MenuKey, family.Entries, seenNames);
        }

        // Everything the editor, Preview, and Selective Copy know about one flag element, so a family is added in one row
        private sealed class FlagFamily
        {
            // The Preview menu key, which Preview and Diff file the family's checkboxes under
            public string MenuKey;
            public string XmlTag;
            // The title of the flag row, which differs from the menu key for the SpecialFlight flags
            public string ResidualTitle;
            public string DescriptorCategory;
            public FlagNameEntry[] Entries;
            // The sub-handling category that holds the flags element, null for a family on the base handling
            public string ElementCategory;
            public Func<HandlingData, IntPtr> ResolveAddress;
            public int Offset;
            // True for the one family version-gated above RawMemory.RawOffsetsSupported's own floor
            // It reads through the gated ReadRawUInt, and every other family through the always-available ReadFlagBits, which has no version check
            public bool IsVersionGated;
            public Func<IntPtr, int, uint> ReadFlags;
            // Takes the flags value in hex without its prefix and the number of ticked rows
            public string LiveSubtitleTemplate;
            // The same for Preview's value, which carries its 0x prefix already. A base family has none, since it shares SUBTITLE_FLAGS_TEXT
            public string PreviewSubtitleTemplate;
        }

        // Built on first use, since it reads the entry tables in another file and static field initializers across partial files run in no fixed order
        private static FlagFamily[] flagFamilies;
        private static FlagFamily[] FlagFamilies
        {
            get
            {
                if (flagFamilies == null) flagFamilies = BuildFlagFamilies();
                return flagFamilies;
            }
        }

        // The editor's family order
        private static FlagFamily[] BuildFlagFamilies()
        {
            return new[]
            {
                new FlagFamily { MenuKey = "ModelFlags", XmlTag = MODEL_FLAGS_TAG, ResidualTitle = "ModelFlags", DescriptorCategory = "ModelFlagBits", Entries = ModelFlagEntries, ResolveAddress = h => h.MemoryAddress, Offset = HandlingDataLayout.ModelFlags, ReadFlags = RawMemory.ReadFlagBits, LiveSubtitleTemplate = SUBTITLE_MODEL_FLAGS_VALUE },
                new FlagFamily { MenuKey = "HandlingFlags", XmlTag = HANDLING_FLAGS_TAG, ResidualTitle = "HandlingFlags", DescriptorCategory = "HandlingFlagBits", Entries = HandlingFlagEntries, ResolveAddress = h => h.MemoryAddress, Offset = HandlingDataLayout.HandlingFlags, ReadFlags = RawMemory.ReadFlagBits, LiveSubtitleTemplate = SUBTITLE_HANDLING_FLAGS_VALUE },
                new FlagFamily { MenuKey = "DamageFlags", XmlTag = DAMAGE_FLAGS_TAG, ResidualTitle = "DamageFlags", DescriptorCategory = "DamageFlagBits", Entries = DamageFlagEntries, ResolveAddress = h => h.MemoryAddress, Offset = HandlingDataLayout.DamageFlags, ReadFlags = RawMemory.ReadFlagBits, LiveSubtitleTemplate = SUBTITLE_DAMAGE_FLAGS_VALUE },
                new FlagFamily { MenuKey = "AdvancedFlags", XmlTag = ADVANCED_FLAGS_TAG, ResidualTitle = "AdvancedFlags", DescriptorCategory = "AdvancedFlagBits", Entries = AdvancedFlagEntries, ElementCategory = CATEGORY_CAR, ResolveAddress = h => h.CarHandlingData != null ? h.CarHandlingData.MemoryAddress : IntPtr.Zero, Offset = CarLayout.AdvancedFlags, ReadFlags = RawMemory.ReadFlagBits, LiveSubtitleTemplate = SUBTITLE_ADVANCED_FLAGS_VALUE, PreviewSubtitleTemplate = SUBTITLE_ADVANCED_FLAGS_TEXT },
                new FlagFamily { MenuKey = "SpecialFlightFlags", XmlTag = SPECIAL_FLIGHT_FLAGS_TAG, ResidualTitle = "Flags", DescriptorCategory = "SpecialFlightFlagBits", Entries = SpecialFlightFlagEntries, ElementCategory = CATEGORY_SPECIAL_FLIGHT, ResolveAddress = h => h.SpecialFlightHandlingData != null ? h.SpecialFlightHandlingData.MemoryAddress : IntPtr.Zero, Offset = SpecialFlightLayout.Flags, IsVersionGated = true, ReadFlags = RawMemory.ReadRawUInt, LiveSubtitleTemplate = SUBTITLE_SPECIAL_FLIGHT_FLAGS_VALUE, PreviewSubtitleTemplate = SUBTITLE_SPECIAL_FLIGHT_FLAGS_TEXT },
            };
        }

        private static FlagFamily FindFlagFamilyByMenuKey(string menuKey)
        {
            foreach (FlagFamily family in FlagFamilies) if (family.MenuKey == menuKey) return family;
            return null;
        }

        // Finds a sub-handling category's family. A family on the base handling holds no category, so a category name never finds one
        private static FlagFamily FindFlagFamilyByElementCategory(string category)
        {
            foreach (FlagFamily family in FlagFamilies) if (family.ElementCategory == category) return family;
            return null;
        }

        private static FlagFamily FindFlagFamilyByTag(string tag)
        {
            foreach (FlagFamily family in FlagFamilies) if (family.XmlTag == tag) return family;
            return null;
        }

        private void ValidateFlagEntryTable(string table, FlagNameEntry[] entries, HashSet<string> seenNames)
        {
            uint seenBits = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                FlagNameEntry entry = entries[i];
                if (entry.BitIndex < 0 || entry.BitIndex >= FLAG_BIT_COUNT) logger.Log(Dbg.Level.Error, Dbg.FlagEntryMalformed(table, entry.Name, entry.BitIndex, "Bit outside 0 to " + (FLAG_BIT_COUNT - 1)));
                else if ((seenBits & (1u << entry.BitIndex)) != 0) logger.Log(Dbg.Level.Error, Dbg.FlagEntryMalformed(table, entry.Name, entry.BitIndex, "Bit already used by another row of this table"));
                else seenBits |= 1u << entry.BitIndex;
                if (!seenNames.Add(entry.Name)) logger.Log(Dbg.Level.Error, Dbg.FlagEntryMalformed(table, entry.Name, entry.BitIndex, "Name already listed in a flag table"));
                if (entry.FlagConflictHub && entry.FlagConflictGroup < 0) logger.Log(Dbg.Level.Error, Dbg.FlagEntryMalformed(table, entry.Name, entry.BitIndex, "Hub without a conflict group"));
            }
        }

        // The game's loader snaps a file value below DRIVE_BIAS_RWD_SNAP or above DRIVE_BIAS_FWD_SNAP to a single driven axle, stored as 1.0 on it and 0.0 on the other
        // Any value between is stored as both axles scaled by BIAS_FRONT_SCALE
        // An axle stored at 0 is undriven: No rear bias reads 1.0 (front only), no front bias reads 0.0 (rear only), else the front bias divided by BIAS_FRONT_SCALE
        // SetDriveBiasFront writes these same 3 shapes, so both directions agree. Writing 2.0 onto a lone axle would double its drive force for the whole session
        private static float GetDriveBiasFront(HandlingData handling)
        {
            float front = handling.DriveBiasFront;
            float rear = RawMemory.ReadRawFloat(handling.MemoryAddress, HandlingDataLayout.DriveBiasRear);
            if (front == 0.0f) return 0.0f;
            if (rear == 0.0f) return 1.0f;
            return front / BIAS_FRONT_SCALE;
        }

        private static void SetDriveBiasFront(HandlingData handling, float value)
        {
            float front;
            float rear;
            if (value > DRIVE_BIAS_FWD_SNAP)
            {
                front = 1.0f;
                rear = 0.0f;
            }
            else if (value < DRIVE_BIAS_RWD_SNAP)
            {
                front = 0.0f;
                rear = 1.0f;
            }
            else
            {
                front = value * BIAS_FRONT_SCALE;
                rear = BIAS_FRONT_SCALE * (1.0f - value);
            }
            handling.DriveBiasFront = front;
            RawMemory.WriteDerivedFloat(handling, HandlingDataLayout.DriveBiasRear, rear);
        }

        // A step smaller than the snap distance could never leave a snapped value, since the setter snaps the result straight back
        // Stepping out of a snap zone lands on its edge instead, and every other step is the plain rounded one
        private static float StepDriveBiasFront(float current, double step, LemonUI.Menus.Direction direction)
        {
            if (direction == LemonUI.Menus.Direction.Right && current < DRIVE_BIAS_RWD_SNAP) return DRIVE_BIAS_RWD_SNAP;
            if (direction == LemonUI.Menus.Direction.Left && current > DRIVE_BIAS_FWD_SNAP) return DRIVE_BIAS_FWD_SNAP;
            return StepRoundedInDouble(current, step, direction);
        }

        // A CVT has no gear ratios to scale, so its top speed tunes finely. Every other gearbox steps whole kilometers per hour
        private static float ResolveInitialDriveMaxFlatVelocityStep(HandlingData handling)
        {
            return (handling.HandlingFlags & VehicleHandlingFlags.CVT) != 0 ? STEP_BASE_INITIAL_DRIVE_MAX_FLAT_VELOCITY_CVT : STEP_BASE_INITIAL_DRIVE_MAX_FLAT_VELOCITY;
        }

        // A negative value is the game's no-limit setting and leads with Infinite
        private static string MilesPerHourDescriptionPrefix(float milesPerHour)
        {
            if (milesPerHour < 0.0f) return "(Infinite) ";
            return "(" + milesPerHour.ToString("F0", CultureInfo.InvariantCulture) + " MPH) ";
        }

        private static string BoostMaxSpeedDescriptionPrefix(float metersPerSecond) { return MilesPerHourDescriptionPrefix(metersPerSecond * MS_TO_MPH); }
        private static string InitialDriveMaxFlatVelocityDescriptionPrefix(float kilometersPerHour) { return MilesPerHourDescriptionPrefix(kilometersPerHour * KMH_TO_MPH); }

        private enum GearRatioPatchState { NotLoaded, NoExport, Inactive, Active }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GearRatioPatchActiveFunction();

        // Read once, above BaseFields, since static fields initialize in declaration order and the InitialDriveGears row reads it
        // The plugin installs its patch at load and pins itself, so the state read here never changes while the game runs
        private static readonly GearRatioPatchState gearRatioPatchState = ReadGearRatioPatchState();

        private static GearRatioPatchState ReadGearRatioPatchState()
        {
            Delegate[] functions;
            PluginBridge.BindResult result = PluginBridge.BindExports(GEAR_RATIO_PATCH_MODULE_NAME, new[] { GEAR_RATIO_PATCH_ACTIVE_EXPORT }, typeof(GearRatioPatchActiveFunction), out functions);
            if (result == PluginBridge.BindResult.ModuleNotLoaded) return GearRatioPatchState.NotLoaded;
            if (result == PluginBridge.BindResult.ExportMissing) return GearRatioPatchState.NoExport;
            return ((GearRatioPatchActiveFunction)functions[0])() != 0 ? GearRatioPatchState.Active : GearRatioPatchState.Inactive;
        }

        // Getters and setters work in save-file units and absorb every conversion and derived-value side effect (WriteDerivedFloat, RecomputeTractionCurveRatios)
        // BuildEntryField and ApplyEntryField never need to know a field's conversion factor
        private static readonly HandlingFieldEntry<HandlingData>[] BaseFields =
        {
            new HandlingFieldEntry<HandlingData> { Tag = "fMass", Title = "Mass", GetFloat = h => h.Mass, SetFloat = (h, v) => h.Mass = v, Step = STEP_BASE_MASS },
            new HandlingFieldEntry<HandlingData> { Tag = "fInitialDragCoeff", Title = "InitialDragCoefficient",
                GetFloat = h => h.InitialDragCoefficient * DRAG_COEFF_SCALE, SetFloat = (h, v) => h.InitialDragCoefficient = v / DRAG_COEFF_SCALE, Step = STEP_BASE_INITIAL_DRAG_COEFFICIENT, HardMin = INITIAL_DRAG_COEFFICIENT_HARD_MIN, RefreshesVehicle = true },
            new HandlingFieldEntry<HandlingData> { Tag = "fDownforceModifier", Title = "DownforceModifier", GetFloat = h => h.DownForceModifier, SetFloat = (h, v) => h.DownForceModifier = v, Step = STEP_BASE_DOWNFORCE_MODIFIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fPopUpLightRotation", Title = "PopUpLightRotation", GetFloat = h => h.PopUpLightRotation, SetFloat = (h, v) => h.PopUpLightRotation = v, Step = STEP_BASE_POP_UP_LIGHT_ROTATION },
            new HandlingFieldEntry<HandlingData> { Tag = "fPercentSubmerged", Title = "PercentSubmerged",
                GetFloat = h => h.PercentSubmerged, SetFloat = (h, v) =>
                {
                    h.PercentSubmerged = v;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.SubmergedRatio, v != 0.0f ? 100.0f / v : 0.0f);
                }, VisualizedElements = HandlingVisualizationElements.Submerged, Step = STEP_BASE_PERCENT_SUBMERGED },
            new HandlingFieldEntry<HandlingData> { Tag = "vecCentreOfMassOffset", Title = "CenterOfMassOffset", GetVec = h => h.CenterOfMassOffset, SetVec = (h, v) => h.CenterOfMassOffset = v, Step = STEP_BASE_CENTER_OF_MASS_OFFSET, Recompute = SyncCenterOfGravityOffset, VisualizedElements = HandlingVisualizationElements.COM | HandlingVisualizationElements.CenterOfGravity | HandlingVisualizationElements.SuspensionLoad },
            new HandlingFieldEntry<HandlingData> { Tag = "vecInertiaMultiplier", Title = "InertiaMultiplier", GetVec = h => h.InertiaMultiplier, SetVec = (h, v) => h.InertiaMultiplier = v, Step = STEP_BASE_INERTIA_MULTIPLIER },
            // Both accessors use the raw rear bias, so the row is raw offset backed even though the front bias is an API property
            new HandlingFieldEntry<HandlingData> { Tag = "fDriveBiasFront", Title = "DriveBiasFront",
                GetFloat = GetDriveBiasFront, SetFloat = SetDriveBiasFront, Step = STEP_BASE_DRIVE_BIAS_FRONT, FixedStep = true, StepOverride = StepDriveBiasFront, RawOffset = true, RequiresRespawn = true },
            new HandlingFieldEntry<HandlingData> { Tag = "nInitialDriveGears", Title = "InitialDriveGears", GetInt = h => h.InitialDriveGears, SetInt = (h, v) => h.InitialDriveGears = v, HardMinInt = 0, HardMaxInt = gearRatioPatchState == GearRatioPatchState.Active ? INITIAL_DRIVE_GEARS_MAX_WITH_PATCH : INITIAL_DRIVE_GEARS_MAX, RefreshesVehicle = true },
            new HandlingFieldEntry<HandlingData> { Tag = "fInitialDriveForce", Title = "InitialDriveForce", GetFloat = h => h.InitialDriveForce, SetFloat = (h, v) => h.InitialDriveForce = v, RefreshesVehicle = true, Step = STEP_BASE_INITIAL_DRIVE_FORCE },
            new HandlingFieldEntry<HandlingData> { Tag = "fDriveInertia", Title = "DriveInertia", GetFloat = h => h.DriveInertia, SetFloat = (h, v) => h.DriveInertia = v, Step = STEP_BASE_DRIVE_INERTIA },
            new HandlingFieldEntry<HandlingData> { Tag = "fClutchChangeRateScaleUpShift", Title = "ClutchChangeRateUpShift", GetFloat = h => h.ClutchChangeRateScaleUpShift, SetFloat = (h, v) => h.ClutchChangeRateScaleUpShift = v, Step = STEP_BASE_CLUTCH_CHANGE_RATE_UP_SHIFT },
            new HandlingFieldEntry<HandlingData> { Tag = "fClutchChangeRateScaleDownShift", Title = "ClutchChangeRateDownShift", GetFloat = h => h.ClutchChangeRateScaleDownShift, SetFloat = (h, v) => h.ClutchChangeRateScaleDownShift = v, Step = STEP_BASE_CLUTCH_CHANGE_RATE_DOWN_SHIFT },
            // Rounded to 2 decimals in the getter, so the display, the undo text, and the reassertion all read the clean value
            // The m/s to km/h conversion through KMH_TO_MS carries float32 noise large enough that a coarser round still leaves 999.9999 instead of 1000
            new HandlingFieldEntry<HandlingData> { Tag = "fInitialDriveMaxFlatVel", Title = "InitialDriveMaxFlatVelocity",
                GetFloat = h => (float)Math.Round(h.InitialDriveMaxFlatVelocity * KMH_TO_MS, 2),
                SetFloat = (h, v) =>
                {
                    h.InitialDriveMaxFlatVelocity = v / KMH_TO_MS;
                    h.InitialDriveMaxVelocity = h.InitialDriveMaxFlatVelocity * DRIVE_MAX_VELOCITY_MULT;
                }, Step = STEP_BASE_INITIAL_DRIVE_MAX_FLAT_VELOCITY, StepResolver = ResolveInitialDriveMaxFlatVelocityStep, RefreshesVehicle = true, DescriptionPrefix = InitialDriveMaxFlatVelocityDescriptionPrefix },
            new HandlingFieldEntry<HandlingData> { Tag = "fBrakeForce", Title = "BrakeForce", GetFloat = h => h.BrakeForce, SetFloat = (h, v) => h.BrakeForce = v, Step = STEP_BASE_BRAKE_FORCE },
            new HandlingFieldEntry<HandlingData> { Tag = "fBrakeBiasFront", Title = "BrakeBiasFront",
                GetFloat = h => h.BrakeBiasFront / BIAS_FRONT_SCALE, SetFloat = (h, v) =>
                {
                    h.BrakeBiasFront = v * BIAS_FRONT_SCALE;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.BrakeBiasRear, BIAS_FRONT_SCALE - h.BrakeBiasFront);
                }, Step = STEP_BASE_BRAKE_BIAS_FRONT },
            new HandlingFieldEntry<HandlingData> { Tag = "fHandBrakeForce", Title = "HandbrakeForce", GetFloat = h => h.HandBrakeForce, SetFloat = (h, v) => h.HandBrakeForce = v, Step = STEP_BASE_HANDBRAKE_FORCE },
            new HandlingFieldEntry<HandlingData> { Tag = "fSteeringLock", Title = "SteeringLock",
                GetFloat = h => h.SteeringLock * RAD2DEG, SetFloat = (h, v) =>
                {
                    h.SteeringLock = v * DEG2RAD;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.SteeringLockRatio, h.SteeringLock != 0.0f ? 1.0f / h.SteeringLock : 0.0f);
                }, Step = STEP_BASE_STEERING_LOCK, HardMin = STEERING_LOCK_HARD_MIN },
            new HandlingFieldEntry<HandlingData> { Tag = "fTractionCurveMax", Title = "TractionCurveMax",
                GetFloat = h => h.TractionCurveMax, SetFloat = (h, v) =>
                {
                    h.TractionCurveMax = v;
                    RawMemory.RecomputeTractionCurveRatios(h);
                }, Step = STEP_BASE_TRACTION_CURVE_MAX },
            new HandlingFieldEntry<HandlingData> { Tag = "fTractionCurveMin", Title = "TractionCurveMin",
                GetFloat = h => h.TractionCurveMin, SetFloat = (h, v) =>
                {
                    h.TractionCurveMin = v;
                    RawMemory.RecomputeTractionCurveRatios(h);
                }, Step = STEP_BASE_TRACTION_CURVE_MIN },
            new HandlingFieldEntry<HandlingData> { Tag = "fTractionCurveLateral", Title = "TractionCurveLateral",
                GetFloat = h => h.TractionCurveLateral * RAD2DEG, SetFloat = (h, v) =>
                {
                    h.TractionCurveLateral = v * DEG2RAD;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.TractionCurveLateralRatio, h.TractionCurveLateral != 0.0f ? 1.0f / h.TractionCurveLateral : 0.0f);
                }, Step = STEP_BASE_TRACTION_CURVE_LATERAL },
            new HandlingFieldEntry<HandlingData> { Tag = "fTractionSpringDeltaMax", Title = "TractionSpringDeltaMax",
                GetFloat = h => h.TractionSpringDeltaMax, SetFloat = (h, v) =>
                {
                    h.TractionSpringDeltaMax = v;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.TractionSpringDeltaMaxRatio, v != 0.0f ? 1.0f / v : 0.0f);
                }, Step = STEP_BASE_TRACTION_SPRING_DELTA_MAX },
            new HandlingFieldEntry<HandlingData> { Tag = "fLowSpeedTractionLossMult", Title = "LowSpeedTractionLossMultiplier", GetFloat = h => h.LowSpeedTractionLossMultiplier, SetFloat = (h, v) => h.LowSpeedTractionLossMultiplier = v, Step = STEP_BASE_LOW_SPEED_TRACTION_LOSS_MULTIPLIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fCamberStiffnesss", Title = "CamberStiffness", GetFloat = h => h.CamberStiffness, SetFloat = (h, v) => h.CamberStiffness = v, Step = STEP_BASE_CAMBER_STIFFNESS },
            new HandlingFieldEntry<HandlingData> { Tag = "fTractionBiasFront", Title = "TractionBiasFront",
                GetFloat = h => h.TractionBiasFront / BIAS_FRONT_SCALE, SetFloat = (h, v) =>
                {
                    h.TractionBiasFront = v * BIAS_FRONT_SCALE;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.TractionBiasRear, BIAS_FRONT_SCALE - h.TractionBiasFront);
                }, Step = STEP_BASE_TRACTION_BIAS_FRONT },
            new HandlingFieldEntry<HandlingData> { Tag = "fTractionLossMult", Title = "TractionLossMultiplier", GetFloat = h => h.TractionLossMultiplier, SetFloat = (h, v) => h.TractionLossMultiplier = v, Step = STEP_BASE_TRACTION_LOSS_MULTIPLIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionForce", Title = "SuspensionForce", GetFloat = h => h.SuspensionForce, SetFloat = (h, v) => h.SuspensionForce = v, Step = STEP_BASE_SUSPENSION_FORCE },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionCompDamp", Title = "SuspensionCompressionDamping",
                GetFloat = h => h.SuspensionCompressionDamping * SUSPENSION_DAMPING_SCALE, SetFloat = (h, v) => h.SuspensionCompressionDamping = v / SUSPENSION_DAMPING_SCALE, Step = STEP_BASE_SUSPENSION_COMPRESSION_DAMPING },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionReboundDamp", Title = "SuspensionReboundDamping",
                GetFloat = h => h.SuspensionReboundDamping * SUSPENSION_DAMPING_SCALE, SetFloat = (h, v) => h.SuspensionReboundDamping = v / SUSPENSION_DAMPING_SCALE, Step = STEP_BASE_SUSPENSION_REBOUND_DAMPING },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionUpperLimit", Title = "SuspensionUpperLimit", GetFloat = h => h.SuspensionUpperLimit, SetFloat = (h, v) => h.SuspensionUpperLimit = v, RequiresRespawn = true, Step = STEP_BASE_SUSPENSION_UPPER_LIMIT },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionLowerLimit", Title = "SuspensionLowerLimit", GetFloat = h => h.SuspensionLowerLimit, SetFloat = (h, v) => h.SuspensionLowerLimit = v, RequiresRespawn = true, Step = STEP_BASE_SUSPENSION_LOWER_LIMIT },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionRaise", Title = "SuspensionRaise", GetFloat = h => h.SuspensionRaise, SetFloat = (h, v) => h.SuspensionRaise = v, RequiresRespawn = true, Step = STEP_BASE_SUSPENSION_RAISE },
            new HandlingFieldEntry<HandlingData> { Tag = "fSuspensionBiasFront", Title = "SuspensionBiasFront",
                GetFloat = h => h.SuspensionBiasFront / BIAS_FRONT_SCALE, SetFloat = (h, v) =>
                {
                    h.SuspensionBiasFront = v * BIAS_FRONT_SCALE;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.SuspensionBiasRear, BIAS_FRONT_SCALE - h.SuspensionBiasFront);
                }, RequiresRespawn = true, VisualizedElements = HandlingVisualizationElements.SuspensionLoad, Step = STEP_BASE_SUSPENSION_BIAS_FRONT },
            new HandlingFieldEntry<HandlingData> { Tag = "fAntiRollBarForce", Title = "AntiRollBarForce", GetFloat = h => h.AntiRollBarForce, SetFloat = (h, v) => h.AntiRollBarForce = v, VisualizedElements = HandlingVisualizationElements.AntiRollBarBias, Step = STEP_BASE_ANTI_ROLL_BAR_FORCE },
            new HandlingFieldEntry<HandlingData> { Tag = "fAntiRollBarBiasFront", Title = "AntiRollBarBiasFront",
                GetFloat = h => h.AntiRollBarBiasFront / BIAS_FRONT_SCALE, SetFloat = (h, v) =>
                {
                    h.AntiRollBarBiasFront = v * BIAS_FRONT_SCALE;
                    RawMemory.WriteDerivedFloat(h, HandlingDataLayout.AntiRollBarBiasRear, BIAS_FRONT_SCALE - h.AntiRollBarBiasFront);
                }, VisualizedElements = HandlingVisualizationElements.AntiRollBarBias, Step = STEP_BASE_ANTI_ROLL_BAR_BIAS_FRONT },
            new HandlingFieldEntry<HandlingData> { Tag = "fRollCentreHeightFront", Title = "RollCenterHeightFront", GetFloat = h => h.RollCenterHeightFront, SetFloat = (h, v) => h.RollCenterHeightFront = v, VisualizedElements = HandlingVisualizationElements.RollCenter, Step = STEP_BASE_ROLL_CENTER_HEIGHT_FRONT },
            new HandlingFieldEntry<HandlingData> { Tag = "fRollCentreHeightRear", Title = "RollCenterHeightRear", GetFloat = h => h.RollCenterHeightRear, SetFloat = (h, v) => h.RollCenterHeightRear = v, VisualizedElements = HandlingVisualizationElements.RollCenter, Step = STEP_BASE_ROLL_CENTER_HEIGHT_REAR },
            new HandlingFieldEntry<HandlingData> { Tag = "fCollisionDamageMult", Title = "CollisionDamageMultiplier", GetFloat = h => h.CollisionDamageMultiplier, SetFloat = (h, v) => h.CollisionDamageMultiplier = v, Step = STEP_BASE_COLLISION_DAMAGE_MULTIPLIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fWeaponDamageMult", Title = "WeaponDamageMultiplier", GetFloat = h => h.WeaponDamageMultiplier, SetFloat = (h, v) => h.WeaponDamageMultiplier = v, Step = STEP_BASE_WEAPON_DAMAGE_MULTIPLIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fDeformationDamageMult", Title = "DeformationDamageMultiplier", GetFloat = h => h.DeformationDamageMultiplier, SetFloat = (h, v) => h.DeformationDamageMultiplier = v, Step = STEP_BASE_DEFORMATION_DAMAGE_MULTIPLIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fEngineDamageMult", Title = "EngineDamageMultiplier", GetFloat = h => h.EngineDamageMultiplier, SetFloat = (h, v) => h.EngineDamageMultiplier = v, Step = STEP_BASE_ENGINE_DAMAGE_MULTIPLIER },
            new HandlingFieldEntry<HandlingData> { Tag = "fPetrolTankVolume", Title = "PetrolTankVolume", GetFloat = h => h.PetrolTankVolume, SetFloat = (h, v) => h.PetrolTankVolume = v, Step = STEP_BASE_PETROL_TANK_VOLUME, RequiresRespawn = true },
            new HandlingFieldEntry<HandlingData> { Tag = "fOilVolume", Title = "OilVolume", GetFloat = h => h.OilVolume, SetFloat = (h, v) => h.OilVolume = v, Step = STEP_BASE_OIL_VOLUME },
            new HandlingFieldEntry<HandlingData> { Tag = "fSeatOffsetDistX", Title = "SeatOffsetDistanceX", GetFloat = h => h.SeatOffsetDistanceX, SetFloat = (h, v) => h.SeatOffsetDistanceX = v, Step = STEP_BASE_SEAT_OFFSET_DISTANCE_X },
            new HandlingFieldEntry<HandlingData> { Tag = "fSeatOffsetDistY", Title = "SeatOffsetDistanceY", GetFloat = h => h.SeatOffsetDistanceY, SetFloat = (h, v) => h.SeatOffsetDistanceY = v, Step = STEP_BASE_SEAT_OFFSET_DISTANCE_Y },
            new HandlingFieldEntry<HandlingData> { Tag = "fSeatOffsetDistZ", Title = "SeatOffsetDistanceZ", GetFloat = h => h.SeatOffsetDistanceZ, SetFloat = (h, v) => h.SeatOffsetDistanceZ = v, Step = STEP_BASE_SEAT_OFFSET_DISTANCE_Z },
            new HandlingFieldEntry<HandlingData> { Tag = "nMonetaryValue", Title = "MonetaryValue", GetInt = h => h.MonetaryValue, SetInt = (h, v) => h.MonetaryValue = v },
            new HandlingFieldEntry<HandlingData> { Tag = "fRocketBoostCapacity", Title = "RocketBoostCapacity",
                GetFloat = h => RawMemory.ReadRawFloat(h.MemoryAddress, HandlingDataLayout.RocketBoostCapacity), SetFloat = (h, v) => RawMemory.WriteRawFloat(h.MemoryAddress, HandlingDataLayout.RocketBoostCapacity, v), DefaultFloat = 1.25f, RawOffset = true, RequiresRocketBoost = true, Step = STEP_BASE_ROCKET_BOOST_CAPACITY },
            new HandlingFieldEntry<HandlingData> { Tag = "fBoostMaxSpeed", Title = "BoostMaxSpeed",
                GetFloat = h => RawMemory.ReadRawFloat(h.MemoryAddress, HandlingDataLayout.BoostMaxSpeed), SetFloat = (h, v) => RawMemory.WriteRawFloat(h.MemoryAddress, HandlingDataLayout.BoostMaxSpeed, v), DefaultFloat = 70.0f, RawOffset = true, RequiresRocketBoost = true, Step = STEP_BASE_BOOST_MAX_SPEED, MinimumMagnitude = BOOST_MAX_SPEED_MINIMUM_MAGNITUDE, DescriptionPrefix = BoostMaxSpeedDescriptionPrefix },
        };

        // DefaultFloat in every table is the game's own parser schema init value, filled into a newly created export-only category. A row without one starts at 0
        // The one exception is SpecialFlight, whose rows start at the Deluxo's own values, since it is the only stock SpecialFlight vehicle
        // In this table -1.0 disables the drive-bias-transfer clamp, and the 2 scale factors start at their neutral 1.0
        private static readonly HandlingFieldEntry<CarHandlingData>[] CarFields =
        {
            new HandlingFieldEntry<CarHandlingData> { Tag = "fBackEndPopUpCarImpulseMult", Title = "BackEndPopUpCarImpulseMult",
                GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, CarLayout.BackEndPopUpCarImpulseMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, CarLayout.BackEndPopUpCarImpulseMultiplier, v), RawOffset = true, Step = STEP_CAR_BACK_END_POP_UP_CAR_IMPULSE_MULT },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fBackEndPopUpBuildingImpulseMult", Title = "BackEndPopUpBuildingImpulseMult",
                GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, CarLayout.BackEndPopUpBuildingImpulseMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, CarLayout.BackEndPopUpBuildingImpulseMultiplier, v), RawOffset = true, Step = STEP_CAR_BACK_END_POP_UP_BUILDING_IMPULSE_MULT },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fBackEndPopUpMaxDeltaSpeed", Title = "BackEndPopUpMaxDeltaSpeed",
                GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, CarLayout.BackEndPopUpMaxDeltaSpeed), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, CarLayout.BackEndPopUpMaxDeltaSpeed, v), RawOffset = true, Step = STEP_CAR_BACK_END_POP_UP_MAX_DELTA_SPEED },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fToeFront", Title = "ToeFront", GetFloat = d => d.ToeFront, SetFloat = (d, v) => d.ToeFront = v, Step = STEP_CAR_TOE_FRONT },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fToeRear", Title = "ToeRear", GetFloat = d => d.ToeRear, SetFloat = (d, v) => d.ToeRear = v, Step = STEP_CAR_TOE_REAR },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fCamberFront", Title = "CamberFront", GetFloat = d => d.CamberFront, SetFloat = (d, v) => d.CamberFront = v, RequiresRespawn = true, Step = STEP_CAR_CAMBER_FRONT },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fCamberRear", Title = "CamberRear", GetFloat = d => d.CamberRear, SetFloat = (d, v) => d.CamberRear = v, RequiresRespawn = true, Step = STEP_CAR_CAMBER_REAR },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fCastor", Title = "Castor", GetFloat = d => d.Castor, SetFloat = (d, v) => d.Castor = v, Step = STEP_CAR_CASTOR },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fEngineResistance", Title = "EngineResistance", GetFloat = d => d.EngineResistance, SetFloat = (d, v) => d.EngineResistance = v, Step = STEP_CAR_ENGINE_RESISTANCE },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fMaxDriveBiasTransfer", Title = "MaxDriveBiasTransfer",
                GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, CarLayout.MaxDriveBiasTransfer), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, CarLayout.MaxDriveBiasTransfer, v), DefaultFloat = -1.0f, RawOffset = true, RequiresRespawn = true, Step = STEP_CAR_MAX_DRIVE_BIAS_TRANSFER, CycleMin = -1.0f, CycleMax = 1.0f },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fJumpForceScale", Title = "JumpForceScale",
                GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, CarLayout.JumpForceScale), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, CarLayout.JumpForceScale, v), DefaultFloat = 1.0f, RawOffset = true, Step = STEP_CAR_JUMP_FORCE_SCALE },
            new HandlingFieldEntry<CarHandlingData> { Tag = "fIncreasedRammingForceScale", Title = "IncreasedRammingForceScale",
                GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, CarLayout.IncreasedRammingForceScale), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, CarLayout.IncreasedRammingForceScale, v), DefaultFloat = 1.0f, RawOffset = true, Step = STEP_CAR_INCREASED_RAMMING_FORCE_SCALE },
        };

        // The 3 members of an AdvancedData entry, held to one set of rules on the live array, a pending entry, and an export-only entry
        private static readonly FieldEntry AdvancedDataSlotField = new FieldEntry { Tag = "Slot", Title = "Slot", HardMinInt = ADVANCED_DATA_SLOT_MIN, HardMaxInt = ADVANCED_DATA_SLOT_INDEX_MAX, RawOffset = true };
        private static readonly FieldEntry AdvancedDataIndexField = new FieldEntry { Tag = "Index", Title = "Index", HardMinInt = 0, HardMaxInt = ADVANCED_DATA_SLOT_INDEX_MAX, RawOffset = true };
        private static readonly FieldEntry AdvancedDataValueField = new FieldEntry { Tag = "Value", Title = "Value", Step = STEP_CAR_ADVANCED_DATA_VALUE, RawOffset = true };

        private static FieldEntry ResolveAdvancedDataField(string member)
        {
            if (member == "Slot") return AdvancedDataSlotField;
            if (member == "Index") return AdvancedDataIndexField;
            return member == "Value" ? AdvancedDataValueField : null;
        }

        private static readonly HandlingFieldEntry<BikeHandlingData>[] BikeFields =
        {
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fLeanFwdCOMMult", Title = "LeanForwardCOMMultiplier", GetFloat = d => d.LeanForwardCenterOfMassMultiplier, SetFloat = (d, v) => d.LeanForwardCenterOfMassMultiplier = v, Step = STEP_BIKE_LEAN_FORWARD_COM_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fLeanFwdForceMult", Title = "LeanForwardForceMultiplier", GetFloat = d => d.LeanForwardForceMultiplier, SetFloat = (d, v) => d.LeanForwardForceMultiplier = v, Step = STEP_BIKE_LEAN_FORWARD_FORCE_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fLeanBakCOMMult", Title = "LeanBackCOMMultiplier", GetFloat = d => d.LeanBackCenterOfMassMultiplier, SetFloat = (d, v) => d.LeanBackCenterOfMassMultiplier = v, Step = STEP_BIKE_LEAN_BACK_COM_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fLeanBakForceMult", Title = "LeanBackForceMultiplier", GetFloat = d => d.LeanBackwardForceMultiplier, SetFloat = (d, v) => d.LeanBackwardForceMultiplier = v, Step = STEP_BIKE_LEAN_BACK_FORCE_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fMaxBankAngle", Title = "MaxBankAngle", GetFloat = d => d.MaxBankAngle * RAD2DEG, SetFloat = (d, v) => d.MaxBankAngle = v * DEG2RAD, Step = STEP_BIKE_MAX_BANK_ANGLE, Recompute = (d, vehicle) => RawMemory.WriteRawFloat(d.MemoryAddress, BikeLayout.FullAnimAngleInverse, d.MaxBankAngle != 0.0f ? 1.0f / d.MaxBankAngle : 0.0f) },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fFullAnimAngle", Title = "FullAnimAngle", GetFloat = d => d.FullAnimAngle * RAD2DEG, SetFloat = (d, v) => d.FullAnimAngle = v * DEG2RAD, Step = STEP_BIKE_FULL_ANIM_ANGLE },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fDesLeanReturnFrac", Title = "DesLeanReturnFraction", GetFloat = d => d.DesLeanReturnFraction, SetFloat = (d, v) => d.DesLeanReturnFraction = v, Step = STEP_BIKE_DES_LEAN_RETURN_FRACTION, CycleMin = 0.0f, CycleMax = 1.0f },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fStickLeanMult", Title = "StickLeanMultiplier", GetFloat = d => d.StickLeanMultiplier, SetFloat = (d, v) => d.StickLeanMultiplier = v, Step = STEP_BIKE_STICK_LEAN_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fBrakingStabilityMult", Title = "BrakingStabilityMultiplier", GetFloat = d => d.BrakingStabilityMultiplier, SetFloat = (d, v) => d.BrakingStabilityMultiplier = v, Step = STEP_BIKE_BRAKING_STABILITY_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fInAirSteerMult", Title = "InAirSteerMultiplier", GetFloat = d => d.InAirSteerMultiplier, SetFloat = (d, v) => d.InAirSteerMultiplier = v, Step = STEP_BIKE_IN_AIR_STEER_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fWheelieBalancePoint", Title = "WheelieBalancePoint", GetFloat = d => d.WheelieBalancePoint * RAD2DEG, SetFloat = (d, v) => d.WheelieBalancePoint = v * DEG2RAD, Step = STEP_BIKE_WHEELIE_BALANCE_POINT },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fStoppieBalancePoint", Title = "StoppieBalancePoint", GetFloat = d => d.StoppieBalancePoint * RAD2DEG, SetFloat = (d, v) => d.StoppieBalancePoint = v * DEG2RAD, Step = STEP_BIKE_STOPPIE_BALANCE_POINT },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fWheelieSteerMult", Title = "WheelieSteerMultiplier", GetFloat = d => d.WheelieSteerMultiplier, SetFloat = (d, v) => d.WheelieSteerMultiplier = v, Step = STEP_BIKE_WHEELIE_STEER_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fRearBalanceMult", Title = "RearBalanceMultiplier", GetFloat = d => d.RearBalanceMultiplier, SetFloat = (d, v) => d.RearBalanceMultiplier = v, Step = STEP_BIKE_REAR_BALANCE_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fFrontBalanceMult", Title = "FrontBalanceMultiplier", GetFloat = d => d.FrontBalanceMultiplier, SetFloat = (d, v) => d.FrontBalanceMultiplier = v, Step = STEP_BIKE_FRONT_BALANCE_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fBikeGroundSideFrictionMult", Title = "GroundSideFrictionMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BikeLayout.GroundSideFrictionMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BikeLayout.GroundSideFrictionMultiplier, v), RawOffset = true, Step = STEP_BIKE_GROUND_SIDE_FRICTION_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fBikeWheelGroundSideFrictionMult", Title = "WheelGroundSideFrictionMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BikeLayout.WheelGroundSideFrictionMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BikeLayout.WheelGroundSideFrictionMultiplier, v), RawOffset = true, Step = STEP_BIKE_WHEEL_GROUND_SIDE_FRICTION_MULTIPLIER },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fBikeOnStandLeanAngle", Title = "OnStandLeanAngle", GetFloat = d => d.BikeOnStandLeanAngle * RAD2DEG, SetFloat = (d, v) => d.BikeOnStandLeanAngle = v * DEG2RAD, Step = STEP_BIKE_ON_STAND_LEAN_ANGLE },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fBikeOnStandSteerAngle", Title = "OnStandSteerAngle", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BikeLayout.OnStandSteerAngle), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BikeLayout.OnStandSteerAngle, v), RawOffset = true, Step = STEP_BIKE_ON_STAND_STEER_ANGLE },
            new HandlingFieldEntry<BikeHandlingData> { Tag = "fJumpForce", Title = "JumpForce", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BikeLayout.JumpForce), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BikeLayout.JumpForce, v), RawOffset = true, Step = STEP_BIKE_JUMP_FORCE },
        };

        private static readonly HandlingFieldEntry<FlyingHandlingData>[] FlyingFields =
        {
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fThrust", Title = "Thrust", GetFloat = d => d.Thrust, SetFloat = (d, v) => d.Thrust = v, Step = STEP_FLYING_THRUST },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fThrustFallOff", Title = "ThrustFallOff", GetFloat = d => d.ThrustFallOff, SetFloat = (d, v) => d.ThrustFallOff = v, Step = STEP_FLYING_THRUST_FALL_OFF },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fThrustVectoring", Title = "ThrustVectoring", GetFloat = d => d.ThrustVectoring, SetFloat = (d, v) => d.ThrustVectoring = v, Step = STEP_FLYING_THRUST_VECTORING },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fInitialThrust", Title = "InitialThrust", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.InitialThrust), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.InitialThrust, v), RawOffset = true, Step = STEP_FLYING_INITIAL_THRUST },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fInitialThrustFallOff", Title = "InitialThrustFallOff", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.InitialThrustFallOff), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.InitialThrustFallOff, v), RawOffset = true, Step = STEP_FLYING_INITIAL_THRUST_FALL_OFF },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fYawMult", Title = "YawMultiplier", GetFloat = d => d.YawMultiplier, SetFloat = (d, v) => d.YawMultiplier = v, Step = STEP_FLYING_YAW_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fYawStabilise", Title = "YawStabilize", GetFloat = d => d.YawStabilize, SetFloat = (d, v) => d.YawStabilize = v, Step = STEP_FLYING_YAW_STABILIZE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fSideSlipMult", Title = "SideSlipMultiplier", GetFloat = d => d.SideSlipMultiplier, SetFloat = (d, v) => d.SideSlipMultiplier = v, Step = STEP_FLYING_SIDE_SLIP_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fInitialYawMult", Title = "InitialYawMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.InitialYawMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.InitialYawMultiplier, v), RawOffset = true, Step = STEP_FLYING_INITIAL_YAW_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fRollMult", Title = "RollMultiplier", GetFloat = d => d.RollMultiplier, SetFloat = (d, v) => d.RollMultiplier = v, Step = STEP_FLYING_ROLL_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fRollStabilise", Title = "RollStabilize", GetFloat = d => d.RollStabilize, SetFloat = (d, v) => d.RollStabilize = v, Step = STEP_FLYING_ROLL_STABILIZE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fInitialRollMult", Title = "InitialRollMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.InitialRollMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.InitialRollMultiplier, v), RawOffset = true, Step = STEP_FLYING_INITIAL_ROLL_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fPitchMult", Title = "PitchMultiplier", GetFloat = d => d.PitchMultiplier, SetFloat = (d, v) => d.PitchMultiplier = v, Step = STEP_FLYING_PITCH_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fPitchStabilise", Title = "PitchStabilize", GetFloat = d => d.PitchStabilize, SetFloat = (d, v) => d.PitchStabilize = v, Step = STEP_FLYING_PITCH_STABILIZE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fInitialPitchMult", Title = "InitialPitchMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.InitialPitchMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.InitialPitchMultiplier, v), RawOffset = true, Step = STEP_FLYING_INITIAL_PITCH_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fFormLiftMult", Title = "FormLiftMultiplier", GetFloat = d => d.FormLiftMultiplier, SetFloat = (d, v) => d.FormLiftMultiplier = v, Step = STEP_FLYING_FORM_LIFT_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fAttackLiftMult", Title = "AttackLiftMultiplier", GetFloat = d => d.AttackLiftMultiplier, SetFloat = (d, v) => d.AttackLiftMultiplier = v, Step = STEP_FLYING_ATTACK_LIFT_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fAttackDiveMult", Title = "AttackDiveMultiplier", GetFloat = d => d.AttackDiveMultiplier, SetFloat = (d, v) => d.AttackDiveMultiplier = v, Step = STEP_FLYING_ATTACK_DIVE_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fGearDownDragV", Title = "GearDownDragV", GetFloat = d => d.GearDownDragV, SetFloat = (d, v) => d.GearDownDragV = v, Step = STEP_FLYING_GEAR_DOWN_DRAG_V },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fGearDownLiftMult", Title = "GearDownLiftMultiplier", GetFloat = d => d.GearDownLiftMultiplier, SetFloat = (d, v) => d.GearDownLiftMultiplier = v, Step = STEP_FLYING_GEAR_DOWN_LIFT_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fWindMult", Title = "WindMultiplier", GetFloat = d => d.WindMultiplier, SetFloat = (d, v) => d.WindMultiplier = v, Step = STEP_FLYING_WIND_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fMoveRes", Title = "MoveResistance", GetFloat = d => d.MoveResistance, SetFloat = (d, v) => d.MoveResistance = v, Step = STEP_FLYING_MOVE_RESISTANCE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "vecTurnRes", Title = "TurnResistance", GetVec = d => d.VectorTurnResistance, SetVec = (d, v) => d.VectorTurnResistance = v, Step = STEP_FLYING_TURN_RESISTANCE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "vecSpeedRes", Title = "SpeedResistance", GetVec = d => d.VectorSpeedResistance, SetVec = (d, v) => d.VectorSpeedResistance = v, Step = STEP_FLYING_SPEED_RESISTANCE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fGearDoorFrontOpen", Title = "GearDoorFrontOpen", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.GearDoorFrontOpen) * RAD2DEG, SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.GearDoorFrontOpen, v * DEG2RAD), RawOffset = true, Step = STEP_FLYING_GEAR_DOOR_FRONT_OPEN },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fGearDoorRearOpen", Title = "GearDoorRearOpen", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.GearDoorRearOpen) * RAD2DEG, SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.GearDoorRearOpen, v * DEG2RAD), RawOffset = true, Step = STEP_FLYING_GEAR_DOOR_REAR_OPEN },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fGearDoorRearOpen2", Title = "GearDoorRearOpen2", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.GearDoorRearOpen2) * RAD2DEG, SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.GearDoorRearOpen2, v * DEG2RAD), RawOffset = true, Step = STEP_FLYING_GEAR_DOOR_REAR_OPEN2 },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fGearDoorRearMOpen", Title = "GearDoorRearMOpen", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.GearDoorRearMOpen) * RAD2DEG, SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.GearDoorRearMOpen, v * DEG2RAD), RawOffset = true, Step = STEP_FLYING_GEAR_DOOR_REAR_M_OPEN },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fTurublenceMagnitudeMax", Title = "TurbulenceMagnitudeMax", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.TurbulenceMagnitudeMax), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.TurbulenceMagnitudeMax, v), RawOffset = true, Step = STEP_FLYING_TURBULENCE_MAGNITUDE_MAX },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fTurublenceForceMulti", Title = "TurbulenceForceMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.TurbulenceForceMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.TurbulenceForceMultiplier, v), RawOffset = true, Step = STEP_FLYING_TURBULENCE_FORCE_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fTurublenceRollTorqueMulti", Title = "TurbulenceRollTorqueMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.TurbulenceRollTorqueMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.TurbulenceRollTorqueMultiplier, v), RawOffset = true, Step = STEP_FLYING_TURBULENCE_ROLL_TORQUE_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fTurublencePitchTorqueMulti", Title = "TurbulencePitchTorqueMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.TurbulencePitchTorqueMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.TurbulencePitchTorqueMultiplier, v), RawOffset = true, Step = STEP_FLYING_TURBULENCE_PITCH_TORQUE_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fBodyDamageControlEffectMult", Title = "BodyDamageControlEffectMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.BodyDamageControlEffectMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.BodyDamageControlEffectMultiplier, v), RawOffset = true, Step = STEP_FLYING_BODY_DAMAGE_CONTROL_EFFECT_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fInputSensitivityForDifficulty", Title = "InputSensitivityForDifficulty", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.InputSensitivityForDifficulty), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.InputSensitivityForDifficulty, v), RawOffset = true, Step = STEP_FLYING_INPUT_SENSITIVITY_FOR_DIFFICULTY },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fOnGroundYawBoostSpeedPeak", Title = "OnGroundYawBoostSpeedPeak", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.OnGroundYawBoostSpeedPeak), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.OnGroundYawBoostSpeedPeak, v), RawOffset = true, Step = STEP_FLYING_ON_GROUND_YAW_BOOST_SPEED_PEAK, HardMin = ON_GROUND_YAW_BOOST_SPEED_PEAK_HARD_MIN },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fOnGroundYawBoostSpeedCap", Title = "OnGroundYawBoostSpeedCap", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.OnGroundYawBoostSpeedCap), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.OnGroundYawBoostSpeedCap, v), RawOffset = true, Step = STEP_FLYING_ON_GROUND_YAW_BOOST_SPEED_CAP },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fEngineOffGlideMulti", Title = "EngineOffGlideMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.EngineOffGlideMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.EngineOffGlideMultiplier, v), RawOffset = true, Step = STEP_FLYING_ENGINE_OFF_GLIDE_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fAfterburnerEffectRadius", Title = "AfterburnerEffectRadius", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.AfterburnerEffectRadius), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.AfterburnerEffectRadius, v), DefaultFloat = 0.5f, RawOffset = true, Step = STEP_FLYING_AFTERBURNER_EFFECT_RADIUS },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fAfterburnerEffectDistance", Title = "AfterburnerEffectDistance", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.AfterburnerEffectDistance), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.AfterburnerEffectDistance, v), DefaultFloat = 4.0f, RawOffset = true, Step = STEP_FLYING_AFTERBURNER_EFFECT_DISTANCE },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fAfterburnerEffectForceMulti", Title = "AfterburnerEffectForceMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.AfterburnerEffectForceMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.AfterburnerEffectForceMultiplier, v), DefaultFloat = 0.2f, RawOffset = true, Step = STEP_FLYING_AFTERBURNER_EFFECT_FORCE_MULTIPLIER },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fSubmergeLevelToPullHeliUnderwater", Title = "SubmergeLevelToPullHeliUnderwater", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, FlyingLayout.SubmergeLevelToPullHeliUnderwater), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, FlyingLayout.SubmergeLevelToPullHeliUnderwater, v), DefaultFloat = 0.3f, RawOffset = true, Step = STEP_FLYING_SUBMERGE_LEVEL_TO_PULL_HELI_UNDERWATER, CycleMin = 0.0f, CycleMax = 1.0f },
            new HandlingFieldEntry<FlyingHandlingData> { Tag = "fExtraLiftWithRoll", Title = "ExtraLiftWithRoll", GetFloat = d => d.ExtraLiftWithRoll, SetFloat = (d, v) => d.ExtraLiftWithRoll = v, Step = STEP_FLYING_EXTRA_LIFT_WITH_ROLL },
        };

        private static readonly HandlingFieldEntry<BoatHandlingData>[] BoatFields =
        {
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fBoxFrontMult", Title = "BoxFrontMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.BoxFrontMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.BoxFrontMultiplier, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples | HandlingVisualizationElements.KeelSpheres, Step = STEP_BOAT_BOX_FRONT_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fBoxRearMult", Title = "BoxRearMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.BoxRearMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.BoxRearMultiplier, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples | HandlingVisualizationElements.KeelSpheres, Step = STEP_BOAT_BOX_REAR_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fBoxSideMult", Title = "BoxSideMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.BoxSideMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.BoxSideMultiplier, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_BOAT_BOX_SIDE_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fSampleTop", Title = "SampleTop", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.SampleTop), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.SampleTop, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_BOAT_SAMPLE_TOP },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fSampleBottom", Title = "SampleBottom", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.SampleBottom), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.SampleBottom, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_BOAT_SAMPLE_BOTTOM },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fSampleBottomTestCorrection", Title = "SampleBottomTestCorrection", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.SampleBottomTestCorrection), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.SampleBottomTestCorrection, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_BOAT_SAMPLE_BOTTOM_TEST_CORRECTION },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fAquaplaneForce", Title = "AquaplaneForce", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.AquaplaneForce), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.AquaplaneForce, v), RawOffset = true, Step = STEP_BOAT_AQUAPLANE_FORCE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fAquaplanePushWaterMult", Title = "AquaplanePushWaterMultiplier", GetFloat = d => d.AquaplanePushWaterMultiplier, SetFloat = (d, v) => d.AquaplanePushWaterMultiplier = v, Step = STEP_BOAT_AQUAPLANE_PUSH_WATER_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fAquaplanePushWaterCap", Title = "AquaplanePushWaterCap", GetFloat = d => d.AquaplanePushWaterCap, SetFloat = (d, v) => d.AquaplanePushWaterCap = v, Step = STEP_BOAT_AQUAPLANE_PUSH_WATER_CAP },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fAquaplanePushWaterApply", Title = "AquaplanePushWaterApply", GetFloat = d => d.AquaplanePushWaterApply, SetFloat = (d, v) => d.AquaplanePushWaterApply = v, Step = STEP_BOAT_AQUAPLANE_PUSH_WATER_APPLY },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fRudderForce", Title = "RudderForce", GetFloat = d => d.RudderForce, SetFloat = (d, v) => d.RudderForce = v, Step = STEP_BOAT_RUDDER_FORCE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fRudderOffsetSubmerge", Title = "RudderOffsetSubmerge", GetFloat = d => d.RudderOffsetSubmerge, SetFloat = (d, v) => d.RudderOffsetSubmerge = v, VisualizedElements = HandlingVisualizationElements.RudderOffsets | HandlingVisualizationElements.PropRadius, Step = STEP_BOAT_RUDDER_OFFSET_SUBMERGE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fRudderOffsetForce", Title = "RudderOffsetForce", GetFloat = d => d.RudderOffsetForce, SetFloat = (d, v) => d.RudderOffsetForce = v, VisualizedElements = HandlingVisualizationElements.RudderOffsets, Step = STEP_BOAT_RUDDER_OFFSET_FORCE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fRudderOffsetForceZMult", Title = "RudderOffsetForceZMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.RudderOffsetForceZMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.RudderOffsetForceZMultiplier, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.RudderOffsets | HandlingVisualizationElements.PropRadius, Step = STEP_BOAT_RUDDER_OFFSET_FORCE_Z_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fWaveAudioMult", Title = "WaveAudioMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.WaveAudioMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.WaveAudioMultiplier, v), RawOffset = true, Step = STEP_BOAT_WAVE_AUDIO_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "vecMoveResistance", Title = "MoveResistance",
                GetVec = d => new Vector3(RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.MoveResistance), RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.MoveResistanceY), RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.MoveResistanceZ)),
                SetVec = (d, v) =>
                {
                    RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.MoveResistance, v.X);
                    RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.MoveResistanceY, v.Y);
                    RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.MoveResistanceZ, v.Z);
                }, RawOffset = true, Step = STEP_BOAT_MOVE_RESISTANCE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "vecTurnResistance", Title = "TurnResistance", GetVec = d => d.VectorTurnResistance, SetVec = (d, v) => d.VectorTurnResistance = v, Step = STEP_BOAT_TURN_RESISTANCE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fLook_L_R_CamHeight", Title = "LookLRCamHeight", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.LookLRCamHeight), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.LookLRCamHeight, v), RawOffset = true, Step = STEP_BOAT_LOOK_LR_CAM_HEIGHT },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fDragCoefficient", Title = "DragCoefficient", GetFloat = d => d.DragCoefficient, SetFloat = (d, v) => d.DragCoefficient = v, Step = STEP_BOAT_DRAG_COEFFICIENT },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fKeelSphereSize", Title = "KeelSphereSize", GetFloat = d => d.KeelSphereSize, SetFloat = (d, v) => d.KeelSphereSize = v, VisualizedElements = HandlingVisualizationElements.KeelSpheres, Step = STEP_BOAT_KEEL_SPHERE_SIZE },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fPropRadius", Title = "PropRadius", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.PropRadius), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.PropRadius, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.PropRadius, Step = STEP_BOAT_PROP_RADIUS },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fLowLodAngOffset", Title = "LowLodAngOffset", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.LowLodAngOffset), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.LowLodAngOffset, v), RawOffset = true, Step = STEP_BOAT_LOW_LOD_ANG_OFFSET },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fLowLodDraughtOffset", Title = "LowLodDraughtOffset", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.LowLodDraughtOffset), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.LowLodDraughtOffset, v), RawOffset = true, Step = STEP_BOAT_LOW_LOD_DRAUGHT_OFFSET },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fImpellerOffset", Title = "ImpellerOffset", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.ImpellerOffset), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.ImpellerOffset, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.Impeller, Step = STEP_BOAT_IMPELLER_OFFSET },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fImpellerForceMult", Title = "ImpellerForceMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.ImpellerForceMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.ImpellerForceMultiplier, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.Impeller, Step = STEP_BOAT_IMPELLER_FORCE_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fDinghySphereBuoyConst", Title = "DinghySphereBuoyConstant", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.DinghySphereBuoyConstant), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.DinghySphereBuoyConstant, v), RawOffset = true, Step = STEP_BOAT_DINGHY_SPHERE_BUOY_CONSTANT },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fProwRaiseMult", Title = "ProwRaiseMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.ProwRaiseMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.ProwRaiseMultiplier, v), RawOffset = true, Step = STEP_BOAT_PROW_RAISE_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fDeepWaterSampleBuoyancyMult", Title = "DeepWaterSampleBuoyancyMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.DeepWaterSampleBuoyancyMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.DeepWaterSampleBuoyancyMultiplier, v), DefaultFloat = 1.0f, RawOffset = true, Step = STEP_BOAT_DEEP_WATER_SAMPLE_BUOYANCY_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fTransmissionMultiplier", Title = "TransmissionMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.TransmissionMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.TransmissionMultiplier, v), DefaultFloat = 1.0f, RawOffset = true, Step = STEP_BOAT_TRANSMISSION_MULTIPLIER },
            new HandlingFieldEntry<BoatHandlingData> { Tag = "fTractionMultiplier", Title = "TractionMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, BoatLayout.TractionMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, BoatLayout.TractionMultiplier, v), DefaultFloat = 1.0f, RawOffset = true, Step = STEP_BOAT_TRACTION_MULTIPLIER },
        };

        private static readonly HandlingFieldEntry<SeaPlaneHandlingData>[] SeaPlaneFields =
        {
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fLeftPontoonComponentId", Title = "LeftPontoonComponentId", GetInt = d => RawMemory.ReadRawInt(d.MemoryAddress, SeaPlaneLayout.LeftPontoonComponentId), SetInt = (d, v) => RawMemory.WriteRawInt(d.MemoryAddress, SeaPlaneLayout.LeftPontoonComponentId, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fRightPontoonComponentId", Title = "RightPontoonComponentId", GetInt = d => RawMemory.ReadRawInt(d.MemoryAddress, SeaPlaneLayout.RightPontoonComponentId), SetInt = (d, v) => RawMemory.WriteRawInt(d.MemoryAddress, SeaPlaneLayout.RightPontoonComponentId, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonBuoyConst", Title = "PontoonBuoyConstant", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonBuoyConstant), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonBuoyConstant, v), RawOffset = true, Step = STEP_SEA_PLANE_PONTOON_BUOY_CONSTANT },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonSampleSizeFront", Title = "PontoonSampleSizeFront", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonSampleSizeFront), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonSampleSizeFront, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_SEA_PLANE_PONTOON_SAMPLE_SIZE_FRONT },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonSampleSizeMiddle", Title = "PontoonSampleSizeMiddle", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonSampleSizeMiddle), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonSampleSizeMiddle, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_SEA_PLANE_PONTOON_SAMPLE_SIZE_MIDDLE },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonSampleSizeRear", Title = "PontoonSampleSizeRear", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonSampleSizeRear), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonSampleSizeRear, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_SEA_PLANE_PONTOON_SAMPLE_SIZE_REAR },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonLengthFractionForSamples", Title = "PontoonLengthFractionForSamples", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonLengthFractionForSamples), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SeaPlaneLayout.PontoonLengthFractionForSamples, v), RawOffset = true, VisualizedElements = HandlingVisualizationElements.WaterSamples, Step = STEP_SEA_PLANE_PONTOON_LENGTH_FRACTION_FOR_SAMPLES },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonDragCoefficient", Title = "PontoonDragCoefficient", GetFloat = d => d.PontoonDragCoefficient, SetFloat = (d, v) => d.PontoonDragCoefficient = v, Step = STEP_SEA_PLANE_PONTOON_DRAG_COEFFICIENT },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonVerticalDampingCoefficientUp", Title = "PontoonVerticalDampingUp", GetFloat = d => d.PontoonVerticalDampingCoefficientUp, SetFloat = (d, v) => d.PontoonVerticalDampingCoefficientUp = v, Step = STEP_SEA_PLANE_PONTOON_VERTICAL_DAMPING_UP },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fPontoonVerticalDampingCoefficientDown", Title = "PontoonVerticalDampingDown", GetFloat = d => d.PontoonVerticalDampingCoefficientDown, SetFloat = (d, v) => d.PontoonVerticalDampingCoefficientDown = v, Step = STEP_SEA_PLANE_PONTOON_VERTICAL_DAMPING_DOWN },
            new HandlingFieldEntry<SeaPlaneHandlingData> { Tag = "fKeelSphereSize", Title = "KeelSphereSize", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SeaPlaneLayout.KeelSphereSize), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SeaPlaneLayout.KeelSphereSize, v), RawOffset = true, Step = STEP_SEA_PLANE_KEEL_SPHERE_SIZE },
        };

        private static readonly HandlingFieldEntry<SubmarineHandlingData>[] SubmarineFields =
        {
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "vTurnRes", Title = "TurnResistance",
                GetVec = d => new Vector3(RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.TurnResistance), RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.TurnResistanceY), RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.TurnResistanceZ)),
                SetVec = (d, v) =>
                {
                    RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.TurnResistance, v.X);
                    RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.TurnResistanceY, v.Y);
                    RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.TurnResistanceZ, v.Z);
                }, RawOffset = true, Step = STEP_SUBMARINE_TURN_RESISTANCE },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fMoveResXY", Title = "MoveResistanceXY", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.MoveResistanceXY), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.MoveResistanceXY, v), RawOffset = true, Step = STEP_SUBMARINE_MOVE_RESISTANCE_XY },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fMoveResZ", Title = "MoveResistanceZ", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.MoveResistanceZ), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.MoveResistanceZ, v), RawOffset = true, Step = STEP_SUBMARINE_MOVE_RESISTANCE_Z },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fPitchMult", Title = "PitchMultiplier", GetFloat = d => d.PitchMultiplier, SetFloat = (d, v) => d.PitchMultiplier = v, Step = STEP_SUBMARINE_PITCH_MULTIPLIER },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fPitchAngle", Title = "PitchAngle", GetFloat = d => d.PitchAngle, SetFloat = (d, v) => d.PitchAngle = v, Step = STEP_SUBMARINE_PITCH_ANGLE },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fYawMult", Title = "YawMultiplier", GetFloat = d => d.YawMultiplier, SetFloat = (d, v) => d.YawMultiplier = v, Step = STEP_SUBMARINE_YAW_MULTIPLIER },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fDiveSpeed", Title = "DiveSpeed", GetFloat = d => d.DiveSpeed, SetFloat = (d, v) => d.DiveSpeed = v, Step = STEP_SUBMARINE_DIVE_SPEED, CycleMin = 0.0f, CycleMax = 1.0f },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fRollMult", Title = "RollMultiplier", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.RollMultiplier), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.RollMultiplier, v), RawOffset = true, Step = STEP_SUBMARINE_ROLL_MULTIPLIER },
            new HandlingFieldEntry<SubmarineHandlingData> { Tag = "fRollStab", Title = "RollStabilize", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SubmarineLayout.RollStabilize), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SubmarineLayout.RollStabilize, v), RawOffset = true, Step = STEP_SUBMARINE_ROLL_STABILIZE },
        };

        private static readonly HandlingFieldEntry<TrailerHandlingData>[] TrailerFields =
        {
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fAttachLimitPitch", Title = "AttachLimitPitch", GetFloat = d => d.AttachLimitPitch, SetFloat = (d, v) => d.AttachLimitPitch = v, Step = STEP_TRAILER_ATTACH_LIMIT_PITCH },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fAttachLimitRoll", Title = "AttachLimitRoll", GetFloat = d => d.AttachLimitRoll, SetFloat = (d, v) => d.AttachLimitRoll = v, Step = STEP_TRAILER_ATTACH_LIMIT_ROLL },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fAttachLimitYaw", Title = "AttachLimitYaw", GetFloat = d => d.AttachLimitYaw, SetFloat = (d, v) => d.AttachLimitYaw = v, Step = STEP_TRAILER_ATTACH_LIMIT_YAW },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fUprightSpringConstant", Title = "UprightSpringConstant", GetFloat = d => d.UprightSpringConstant, SetFloat = (d, v) => d.UprightSpringConstant = v, Step = STEP_TRAILER_UPRIGHT_SPRING_CONSTANT },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fUprightDampingConstant", Title = "UprightDampingConstant", GetFloat = d => d.UprightDampingConstant, SetFloat = (d, v) => d.UprightDampingConstant = v, Step = STEP_TRAILER_UPRIGHT_DAMPING_CONSTANT },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fAttachedMaxDistance", Title = "AttachedMaxDistance", GetFloat = d => d.AttachedMaxDistance, SetFloat = (d, v) => d.AttachedMaxDistance = v, Step = STEP_TRAILER_ATTACHED_MAX_DISTANCE },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fAttachedMaxPenetration", Title = "AttachedMaxPenetration", GetFloat = d => d.AttachedMaxPenetration, SetFloat = (d, v) => d.AttachedMaxPenetration = v, Step = STEP_TRAILER_ATTACHED_MAX_PENETRATION },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fAttachRaiseZ", Title = "AttachRaiseZ", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, TrailerLayout.AttachRaiseZ), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, TrailerLayout.AttachRaiseZ, v), RawOffset = true, Step = STEP_TRAILER_ATTACH_RAISE_Z },
            new HandlingFieldEntry<TrailerHandlingData> { Tag = "fPosConstraintMassRatio", Title = "PositionConstraintMassRatio", GetFloat = d => d.PositionConstraintMassRatio, SetFloat = (d, v) => d.PositionConstraintMassRatio = v, Step = STEP_TRAILER_POSITION_CONSTRAINT_MASS_RATIO },
        };

        // Mode is first, so a save writes it first like the game's own files. 0 runs the glider model and 1 the hover model
        // The game treats every value but 1 as glider, so the row holds to 0 and 1, and a new export-only category starts at 1, the stock SpecialFlight value
        private static readonly HandlingFieldEntry<SpecialFlightHandlingData>[] SpecialFlightFields =
        {
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "mode", Title = "Mode", GetInt = d => RawMemory.ReadRawInt(d.MemoryAddress, SpecialFlightLayout.Mode), SetInt = (d, v) => RawMemory.WriteRawInt(d.MemoryAddress, SpecialFlightLayout.Mode, v), DefaultFloat = 1.0f, HardMinInt = 0, HardMaxInt = 1, RawOffset = true, RequiresSpecialFlightFlagsBuild = true },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "vecAngularDamping", Title = "AngularDamping", GetVec = d => d.VectorAngularDamping, SetVec = (d, v) => d.VectorAngularDamping = v, DefaultVector = new Vector3(3.0f, 2.0f, 1.2f), Step = STEP_SPECIAL_FLIGHT_ANGULAR_DAMPING },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "vecAngularDampingMin", Title = "AngularDampingMin", GetVec = d => d.VectorAngularDampingMin, SetVec = (d, v) => d.VectorAngularDampingMin = v, Step = STEP_SPECIAL_FLIGHT_ANGULAR_DAMPING_MIN },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "vecLinearDamping", Title = "LinearDamping", GetVec = d => d.VectorLinearDamping, SetVec = (d, v) => d.VectorLinearDamping = v, DefaultVector = new Vector3(0.9f, 0.1f, 0.7f), Step = STEP_SPECIAL_FLIGHT_LINEAR_DAMPING },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "vecLinearDampingMin", Title = "LinearDampingMin", GetVec = d => d.VectorLinearDampingMin, SetVec = (d, v) => d.VectorLinearDampingMin = v, Step = STEP_SPECIAL_FLIGHT_LINEAR_DAMPING_MIN },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fLiftCoefficient", Title = "LiftCoefficient", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.LiftCoefficient), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.LiftCoefficient, v), DefaultFloat = 50.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_LIFT_COEFFICIENT },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fCriticalLiftAngle", Title = "CriticalLiftAngle", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.CriticalLiftAngle), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.CriticalLiftAngle, v), DefaultFloat = 45.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_CRITICAL_LIFT_ANGLE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fInitialLiftAngle", Title = "InitialLiftAngle", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.InitialLiftAngle), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.InitialLiftAngle, v), DefaultFloat = 1.5f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_INITIAL_LIFT_ANGLE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMaxLiftAngle", Title = "MaxLiftAngle", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxLiftAngle), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxLiftAngle, v), DefaultFloat = 25.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MAX_LIFT_ANGLE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fDragCoefficient", Title = "DragCoefficient", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.DragCoefficient), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.DragCoefficient, v), RawOffset = true, Step = STEP_SPECIAL_FLIGHT_DRAG_COEFFICIENT },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fBrakingDrag", Title = "BrakingDrag", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.BrakingDrag), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.BrakingDrag, v), DefaultFloat = 10.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_BRAKING_DRAG },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMaxLiftVelocity", Title = "MaxLiftVelocity", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxLiftVelocity), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxLiftVelocity, v), DefaultFloat = 2000.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MAX_LIFT_VELOCITY },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMinLiftVelocity", Title = "MinLiftVelocity", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MinLiftVelocity), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MinLiftVelocity, v), RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MIN_LIFT_VELOCITY },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fRollTorqueScale", Title = "RollTorqueScale", GetFloat = d => d.RollTorqueScale, SetFloat = (d, v) => d.RollTorqueScale = v, DefaultFloat = 7.5f, Step = STEP_SPECIAL_FLIGHT_ROLL_TORQUE_SCALE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMaxTorqueVelocity", Title = "MaxTorqueVelocity", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxTorqueVelocity), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxTorqueVelocity, v), DefaultFloat = 100.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MAX_TORQUE_VELOCITY },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMinTorqueVelocity", Title = "MinTorqueVelocity", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MinTorqueVelocity), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MinTorqueVelocity, v), DefaultFloat = 40000.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MIN_TORQUE_VELOCITY },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fYawTorqueScale", Title = "YawTorqueScale", GetFloat = d => d.YawTorqueScale, SetFloat = (d, v) => d.YawTorqueScale = v, DefaultFloat = -4.0f, Step = STEP_SPECIAL_FLIGHT_YAW_TORQUE_SCALE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fSelfLevelingPitchTorqueScale", Title = "SelfLevelingPitchTorqueScale", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.SelfLevelingPitchTorqueScale), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.SelfLevelingPitchTorqueScale, v), DefaultFloat = -5.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_SELF_LEVELING_PITCH_TORQUE_SCALE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMaxPitchTorque", Title = "MaxPitchTorque", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxPitchTorque), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxPitchTorque, v), DefaultFloat = 500.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MAX_PITCH_TORQUE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMaxSteeringRollTorque", Title = "MaxSteeringRollTorque", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxSteeringRollTorque), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxSteeringRollTorque, v), DefaultFloat = 50.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MAX_STEERING_ROLL_TORQUE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fPitchTorqueScale", Title = "PitchTorqueScale", GetFloat = d => d.PitchTorqueScale, SetFloat = (d, v) => d.PitchTorqueScale = v, DefaultFloat = 8.0f, Step = STEP_SPECIAL_FLIGHT_PITCH_TORQUE_SCALE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fSteeringTorqueScale", Title = "SteeringTorqueScale", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.SteeringTorqueScale), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.SteeringTorqueScale, v), DefaultFloat = 1000.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_STEERING_TORQUE_SCALE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMaxThrust", Title = "MaxThrust", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxThrust), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.MaxThrust, v), DefaultFloat = 20.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_MAX_THRUST },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fTransitionDuration", Title = "TransitionDuration", GetFloat = d => d.TransitionDuration, SetFloat = (d, v) => d.TransitionDuration = v, DefaultFloat = 1.0f, Step = STEP_SPECIAL_FLIGHT_TRANSITION_DURATION },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fHoverVelocityScale", Title = "HoverVelocityScale", GetFloat = d => d.HoverVelocityScale, SetFloat = (d, v) => d.HoverVelocityScale = v, DefaultFloat = 1.0f, Step = STEP_SPECIAL_FLIGHT_HOVER_VELOCITY_SCALE },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fStabilityAssist", Title = "StabilityAssist", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, SpecialFlightLayout.StabilityAssist), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, SpecialFlightLayout.StabilityAssist, v), DefaultFloat = 10.0f, RawOffset = true, Step = STEP_SPECIAL_FLIGHT_STABILITY_ASSIST },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fMinSpeedForThrustFalloff", Title = "MinSpeedForThrustFalloff", GetFloat = d => d.MinSpeedForThrustFalloff, SetFloat = (d, v) => d.MinSpeedForThrustFalloff = v, Step = STEP_SPECIAL_FLIGHT_MIN_SPEED_FOR_THRUST_FALLOFF, CycleMin = 0.0f, CycleMax = 1.0f },
            new HandlingFieldEntry<SpecialFlightHandlingData> { Tag = "fBrakingThrustScale", Title = "BrakingThrustScale", GetFloat = d => d.BrakingThrustScale, SetFloat = (d, v) => d.BrakingThrustScale = v, Step = STEP_SPECIAL_FLIGHT_BRAKING_THRUST_SCALE, CycleMin = 0.0f, CycleMax = 1.0f },
        };

        private static readonly HandlingFieldEntry<VehicleWeaponHandlingData>[] WeaponScalarFields =
        {
            new HandlingFieldEntry<VehicleWeaponHandlingData> { Tag = "fUvAnimationMult", Title = "UvAnimationMult", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, WeaponLayout.UvAnimationMult), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, WeaponLayout.UvAnimationMult, v), RawOffset = true, Step = STEP_WEAPON_UV_ANIMATION_MULT },
            new HandlingFieldEntry<VehicleWeaponHandlingData> { Tag = "fMiscGadgetVar", Title = "MiscGadgetVar", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, WeaponLayout.MiscGadgetVar), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, WeaponLayout.MiscGadgetVar, v), RawOffset = true, Step = STEP_WEAPON_MISC_GADGET_VAR },
            new HandlingFieldEntry<VehicleWeaponHandlingData> { Tag = "fWheelImpactOffset", Title = "WheelImpactOffset", GetFloat = d => RawMemory.ReadRawFloat(d.MemoryAddress, WeaponLayout.WheelImpactOffset), SetFloat = (d, v) => RawMemory.WriteRawFloat(d.MemoryAddress, WeaponLayout.WheelImpactOffset, v), RawOffset = true, RequiresRespawn = true, Step = STEP_WEAPON_WHEEL_IMPACT_OFFSET },
        };

        // The base-level damage scale beside the Weapon category's own scalars, which the game reads from the base handling and not from CVehicleWeaponHandlingData
        private static readonly FieldEntry WeaponDamageScaledField = new FieldEntry { Tag = "fWeaponDamageScaledToVehHealthMult", Title = "WeaponDamageScaledToVehHealthMult", Step = STEP_WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT, RawOffset = true, DefaultFloat = WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT_DEFAULT };
    }
}
