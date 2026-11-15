using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Display only, the stored value is untouched. FormatDisplayFloat shows up to 6 decimals when 2 would lose real precision, so 0.061 never reads as 0.06
        private struct DisplayFloat
        {
            private readonly float value;
            private DisplayFloat(float value) { this.value = value; }
            public static implicit operator DisplayFloat(float value) { return new DisplayFloat(value); }
            public static implicit operator float(DisplayFloat display) { return display.value; }
            public override string ToString() { return FormatDisplayFloat(value); }
        }

        private static string FormatDisplayFloat(float value)
        {
            if (!IsExtendedDisplayFloat(value)) return value.ToString("F2", CultureInfo.InvariantCulture);
            return value.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        // The single F2/F6 decision shared by FormatDisplayFloat and every menu-width check, so what the menu measures always matches what it displays
        private static bool IsExtendedDisplayFloat(float value)
        {
            float rounded = (float)Math.Round(value, 2);
            float threshold = Math.Max(DISPLAY_FLOAT_THRESHOLD_MIN, Math.Min(DISPLAY_FLOAT_THRESHOLD_MAX, Math.Abs(value) * DISPLAY_FLOAT_THRESHOLD_RELATIVE));
            return Math.Abs(value - rounded) >= threshold;
        }

        private static bool NeedsWidthRecalc(float oldValue, float newValue)
        {
            if (IsExtendedDisplayFloat(oldValue) != IsExtendedDisplayFloat(newValue)) return true;
            return FormatDisplayFloat(oldValue).Length != FormatDisplayFloat(newValue).Length;
        }

        private static bool DisplayLengthChanged(object oldValue, object newValue) { return oldValue.ToString().Length != newValue.ToString().Length; }

        // The step starts from the value cleaned of conversion noise, so noise never carries from one press to the next
        // The addition runs in double because a float addition at a larger magnitude (EngineResistance near 30) bakes in noise that Math.Round cannot recover afterward
        private static float StepRoundedInDouble(float current, double step, LemonUI.Menus.Direction direction)
        {
            double start = SnapToCleanValue(current);
            double next = direction == LemonUI.Menus.Direction.Left ? start - step : start + step;
            return (float)Math.Round(next, 6);
        }

        // Holding the modifier is the player's own choice of precision, and only a value within one step of 0 shrinks the step, in either direction
        // A press away from 0 shrinks only while the value is under one step, so the shrink holds on the way back out
        // A press at 0 keeps the field's step, so a run through 0 stays at full speed
        private bool IsPrecisionCyclingPress(float current, double fieldStep, int stepDirection)
        {
            if (current == 0.0f) return false;
            double size = Math.Abs(SnapToCleanValue(current));
            bool towardZero = Math.Sign(current) != stepDirection;
            if (towardZero ? size > fieldStep : size >= fieldStep * PRECISION_CYCLING_EQUAL_RATIO) return false;
            return ModifierHeld();
        }

        // The largest of the field step's tenths, hundredths, and so on that is smaller than the value's size
        // Each press then lands one decimal finer as the value nears 0. A step within division noise of the size counts as equal to it
        private static double ResolvePrecisionCyclingStep(float current, double fieldStep)
        {
            double size = Math.Abs(SnapToCleanValue(current));
            double step = fieldStep / PRECISION_CYCLING_DIVISOR;
            while (step >= size * PRECISION_CYCLING_EQUAL_RATIO && step / PRECISION_CYCLING_DIVISOR >= PRECISION_CYCLING_MIN) step /= PRECISION_CYCLING_DIVISOR;
            return Math.Max(step, PRECISION_CYCLING_MIN);
        }

        private static double SnapToCleanValue(float current)
        {
            double snapped = Math.Round((double)current, 4);
            return Math.Abs(snapped - current) <= Math.Abs((double)current) * STEP_SNAP_RELATIVE_TOLERANCE + STEP_SNAP_ABSOLUTE_TOLERANCE ? snapped : current;
        }

        // The vehicle is there for writes that land on the vehicle itself (HandlingFieldEntry.Recompute)
        // Returns False when the field's category is absent on that handling, so nothing was written
        private delegate bool FieldSetter<T>(Vehicle vehicle, HandlingData handling, T value);

        // Bindings let SyncFieldValues refresh every item without re-enumerating them
        // Asserting and AssertedValue back ReassertDriftedFields, and every write site updates them, Undo and Redo included
        // Menu is resolved once, when its tree's build finishes (ResolveBindingMenus). A null Menu syncs on every call as the fallback
        // Synced ensures the first sync always assigns, even when the live value equals the type default
        private class FloatFieldBinding
        {
            public NativeDynamicItem<DisplayFloat> Item;
            public NativeMenu Menu;
            public Func<HandlingData, float> Getter;
            public FieldSetter<float> Setter;
            public float AssertedValue;
            public bool Asserting;
            public bool Synced;
            // A field whose description leads with its current value: The text builder, and the tracked description entry that carries it through a descriptor reload
            public Func<float, string> DescriptionPrefix;
            public TrackedItemDescription TrackedDescription;
            // Scopes ReassertDriftedFields and the vehicle-changed Asserting reset to the tree this binding belongs to
            // An edit made in one tree can then never reassert onto another tree's identically-named field
            public EditorContext Owner;
        }

        private class IntFieldBinding
        {
            public NativeDynamicItem<int> Item;
            public NativeMenu Menu;
            public Func<HandlingData, int> Getter;
            public FieldSetter<int> Setter;
            public int AssertedValue;
            public bool Asserting;
            public bool Synced;
            public EditorContext Owner;
        }

        // Refreshes a live string cycler's displayed text like the float and int bindings do
        // It carries no Setter or Asserting fields, since no live string cycler needs drift reassertion
        private class StringFieldBinding
        {
            public NativeDynamicItem<string> Item;
            public NativeMenu Menu;
            public Func<HandlingData, string> Getter;
            public bool Synced;
            public EditorContext Owner;
        }

        // Bindings are grouped by owning tree, so a sync scans only the resolved owner's list and its cost tracks one tree's field count
        private Dictionary<EditorContext, List<FloatFieldBinding>> floatFieldBindingsByOwner = new Dictionary<EditorContext, List<FloatFieldBinding>>();
        private Dictionary<EditorContext, List<IntFieldBinding>> intFieldBindingsByOwner = new Dictionary<EditorContext, List<IntFieldBinding>>();
        private Dictionary<EditorContext, List<StringFieldBinding>> stringFieldBindingsByOwner = new Dictionary<EditorContext, List<StringFieldBinding>>();
        // Holds every binding currently Asserting, so ReassertDriftedFields scans only these instead of every registered binding across all 4 editor trees every tick
        private List<FloatFieldBinding> assertingFloatBindings = new List<FloatFieldBinding>();
        private List<IntFieldBinding> assertingIntBindings = new List<IntFieldBinding>();
        // Every per-slot turret and pitch-limit binding. Their getters and setters are fixed to one slot index, so a slot move must release them
        private List<FloatFieldBinding> weaponSlotFloatBindings = new List<FloatFieldBinding>();
        // Reverse lookup from a wired item to its binding, so the width check does not linear-scan the whole list once per measured item on every value step
        private Dictionary<NativeItem, FloatFieldBinding> floatBindingByItem = new Dictionary<NativeItem, FloatFieldBinding>();
        private Dictionary<NativeItem, IntFieldBinding> intBindingByItem = new Dictionary<NativeItem, IntFieldBinding>();
        // Each live row whose value shapes a visualization element, mapped to those elements, which Isolated Drawing reads off the highlighted row
        private Dictionary<NativeItem, HandlingVisualizationElements> visualizedRowElements = new Dictionary<NativeItem, HandlingVisualizationElements>();
        // Every live AdvancedData Index and Value binding. Each is fixed to one entry index, so an entry delete or restore must release them
        private List<FloatFieldBinding> advancedDataFloatBindings = new List<FloatFieldBinding>();
        private List<IntFieldBinding> advancedDataIntBindings = new List<IntFieldBinding>();
        // Reverse lookup from a fieldRegistry key to its binding, so Selective Copy reads a field's current value through its Getter
        // The alternative is rebuilding the handling document
        private Dictionary<string, FloatFieldBinding> floatBindingByFieldKey = new Dictionary<string, FloatFieldBinding>();
        private Dictionary<string, IntFieldBinding> intBindingByFieldKey = new Dictionary<string, IntFieldBinding>();

        // The center of gravity offset is per-entity and derived from CenterOfMassOffset once at vehicle creation
        // A write to CenterOfMassOffset on a spawned vehicle must also push through here, or it has no effect until Respawn recreates the instance
        // Every write path runs this with the vehicle it already resolved, so the offset never lands on a different vehicle than the one whose handling was written
        private static void SyncCenterOfGravityOffset(HandlingData handling, Vehicle vehicle)
        {
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            vehicle.CenterOfGravityOffset = handling.CenterOfMassOffset;
        }

        // Removes characters that could never appear in a valid number before a numeric field's validate delegate parses it
        // A stray typo then does not force the whole entry to be retyped. Only the first decimal point is kept
        // A result that still fails to parse (a misplaced minus sign) falls through to GetValidatedUserInput's retry prompt unchanged
        // A lone comma with no period in the text is the decimal point and is written as a period
        private static string StripInvalidNumericCharacters(string text, bool allowDecimalPoint)
        {
            StringBuilder stripped = new StringBuilder(text.Length);
            bool decimalPointKept = false;
            bool commaIsDecimalPoint = allowDecimalPoint && text.IndexOf('.') < 0 && text.IndexOf(',') >= 0 && text.IndexOf(',') == text.LastIndexOf(',');
            foreach (char character in text)
            {
                if (char.IsDigit(character) || character == '-') stripped.Append(character);
                else if (allowDecimalPoint && (character == '.' || (character == ',' && commaIsDecimalPoint)) && !decimalPointKept)
                {
                    stripped.Append('.');
                    decimalPointKept = true;
                }
            }
            return stripped.ToString();
        }

        // The input dialog closes on each submission, so invalid text reopens it with the prior value
        private static string GetValidatedUserInput(string initialText, int maxLength, Func<string, bool> validate, WindowTitle firstTitle = WindowTitle.EnterMessage20)
        {
            string input = Game.GetUserInput(firstTitle, initialText, maxLength);
            if (string.IsNullOrEmpty(input)) return input;
            while (!validate(input))
            {
                input = Game.GetUserInput(WindowTitle.InvalidMessage, input, maxLength);
                if (string.IsNullOrEmpty(input)) return input;
            }
            return input;
        }

        // Base CHandlingData fields (null category, collision-free) get no prefix, matching how they read in the log
        // Every sub-handling and weapon category keeps its prefix to disambiguate shared names
        private static string BuildFieldKey(string category, string title) { return category == null ? title : category + "." + title; }

        // The vehicle a handler writes to is the one its own tree edits, never whichever tree is on screen when the event fires
        private bool ResolveContextVehicleHandling(EditorContext context, out Vehicle vehicle, out HandlingData handling)
        {
            vehicle = context.TargetVehicle;
            handling = null;
            if (Entity.IsNullOrNotExisting(vehicle)) return false;
            handling = vehicle.HandlingData;
            return handling != null && handling.IsValid;
        }

        // Every write path reads the value back, so a store that snaps or clamps (DriveBiasFront) shows, asserts, and records what it actually wrote
        // A press that cannot move the value, or a write the store refuses, shows the current value and records nothing
        // The hard bounds hold every path, and the cycle bounds hold Left/Right only, so typed entry may go past them
        private void WireFloatField(NativeDynamicItem<DisplayFloat> item, FieldEntry entry, FieldStore<float> store)
        {
            ShowRowArrows(item);
            SubscribeItemChanged(item, "WireFloatField.ItemChanged", e =>
            {
                float? read = store.Read();
                if (!read.HasValue) return;
                float current = read.Value;
                HandlingData handling = store.Handling();
                float fieldStep = entry.StepResolver != null && handling != null ? entry.StepResolver(handling) : entry.Step;
                int stepDirection = e.Direction == LemonUI.Menus.Direction.Left ? -1 : 1;
                bool precisionCycling = !entry.FixedStep && IsPrecisionCyclingPress(current, fieldStep, stepDirection);
                double effectiveStep = precisionCycling ? ResolvePrecisionCyclingStep(current, fieldStep) : fieldStep;
                float stepped = entry.StepOverride != null ? entry.StepOverride(current, effectiveStep, e.Direction) : StepRoundedInDouble(current, effectiveStep, e.Direction);
                // A precision step never carries the value past 0 or past one field step, so both are stops on the way through
                if (precisionCycling && Math.Sign(stepped) == -Math.Sign(current)) stepped = 0.0f;
                else if (precisionCycling && Math.Abs(stepped) > fieldStep) stepped = (float)(Math.Sign(stepped) * fieldStep);
                float next = ApplyCycleBounds(entry, ApplyHardBounds(entry, stepped, stepDirection));
                if (next == current)
                {
                    e.Object = current;
                    PlayBoundSound(item);
                    return;
                }
                if (!store.Write(next))
                {
                    e.Object = current;
                    return;
                }
                float? stored = store.Read();
                if (stored.HasValue) next = ResolveWrittenFloat(next, stored.Value);
                // A store that snaps the step straight back (DriveBiasFront) leaves the value where it was, which would record an undo entry that changes nothing
                if (next == current)
                {
                    e.Object = current;
                    return;
                }
                // A value edit moves the menu's width only when the displayed text changes length or crosses into the extended tier
                // Recalculating on every step would re-measure every item through a native text call, once per keypress repeat
                // It is deferred to after pool.Process(), because GoLeft/GoRight assigns SelectedItem only after this handler returns
                if (NeedsWidthRecalc(current, next))
                {
                    pendingWidthRecalc = true;
                    pendingWidthRecalcHandling = handling;
                }
                e.Object = next;
                store.Commit(current, next, true);
            });
            SubscribeActivated(item, "WireFloatField.Activated", () =>
            {
                float? read = store.Read();
                if (!read.HasValue) return;
                float current = read.Value;
                float parsed = 0.0f;
                string input = GetValidatedUserInput(FormatDisplayFloat(current), INPUT_MAX_LENGTH, candidate => ParseFiniteFloat(StripInvalidNumericCharacters(candidate, true), out parsed));
                if (string.IsNullOrEmpty(input))
                {
                    // The input box returns nothing both for Escape and for the prefilled value submitted unedited, so a row with a follow-up reads either as a retype
                    // Reapplying writes nothing and records no undo entry, so an Escape that reapplies loses nothing
                    if (store.Reapply != null)
                    {
                        store.Reapply();
                        return;
                    }
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                float bounded = ApplyHardBounds(entry, parsed, 0);
                if (bounded != parsed) notifications.Notify(NOTIFICATION_KEY_FIELD_ENTRY_HARD_BOUNDED, string.Format(NOTIF_VALUE_BOUNDED, FormatDisplayFloat(bounded)));
                parsed = bounded;
                // The same value, or a write the store snaps back to it, changes nothing, so it records no undo entry the next Undo press would spend on a no-op
                // Compared at the file's resolution, since a fixed tolerance would swallow a real change on a field with a very small step (the Flying multipliers)
                // The follow-up still runs, since retyping the value is how a player re-pushes a derived value the game reset
                if (SameAtSaveResolution(parsed, current))
                {
                    item.SelectedItem = current;
                    if (store.Reapply != null) store.Reapply();
                    return;
                }
                if (!store.Write(parsed)) return;
                float? stored = store.Read();
                if (stored.HasValue) parsed = ResolveWrittenFloat(parsed, stored.Value);
                if (SameAtSaveResolution(parsed, current))
                {
                    item.SelectedItem = current;
                    return;
                }
                item.SelectedItem = parsed;
                if (NeedsWidthRecalc(current, parsed)) RecalculateVisibleMenuWidth(store.Handling());
                store.Commit(current, parsed, false);
            });
        }

        // The menu library plays the left/right sound right after GoLeft or GoRight returns
        // So a press at a bound mutes that sound on its menu and plays the disabled sound instead
        // ProcessMenus restores it around the pool pass, so a mute never outlives the frame
        private NativeMenu leftRightSoundMutedMenu;
        private LemonUI.Sound mutedLeftRightSound;

        private void PlayBoundSound(NativeItem item)
        {
            NativeMenu menu = ResolveMenuShowingItem(item);
            if (menu == null) return;
            RestoreMutedLeftRightSound();
            leftRightSoundMutedMenu = menu;
            mutedLeftRightSound = menu.SoundLeftRight;
            menu.SoundLeftRight = null;
            if (menu.SoundDisabled != null) menu.SoundDisabled.PlayFrontend();
        }

        private void RestoreMutedLeftRightSound()
        {
            if (leftRightSoundMutedMenu == null) return;
            leftRightSoundMutedMenu.SoundLeftRight = mutedLeftRightSound;
            leftRightSoundMutedMenu = null;
            mutedLeftRightSound = null;
        }

        // The tracked entry carries the lead across a descriptor reload
        private void RefreshValueDescription(FloatFieldBinding binding, float value)
        {
            if (binding.DescriptionPrefix == null || binding.TrackedDescription == null) return;
            TrackedItemDescription tracked = binding.TrackedDescription;
            tracked.Prefix = binding.DescriptionPrefix(value);
            binding.Item.Description = tracked.Prefix + ResolveDescriptor(tracked.Category, tracked.FieldName);
        }

        // Same contract as WireFloatField, for int rows: The int bounds, an exact read back, and the display text's length for the width check
        private void WireIntField(NativeDynamicItem<int> item, FieldEntry entry, FieldStore<int> store)
        {
            ShowRowArrows(item);
            SubscribeItemChanged(item, "WireIntField.ItemChanged", e =>
            {
                int? read = store.Read();
                if (!read.HasValue) return;
                int current = read.Value;
                int next = ApplyHardBounds(entry, e.Direction == LemonUI.Menus.Direction.Left ? current - entry.StepInt : current + entry.StepInt);
                if (next == current)
                {
                    e.Object = current;
                    PlayBoundSound(item);
                    return;
                }
                if (!store.Write(next))
                {
                    e.Object = current;
                    return;
                }
                int? stored = store.Read();
                if (stored.HasValue) next = stored.Value;
                if (next == current)
                {
                    e.Object = current;
                    return;
                }
                if (DisplayLengthChanged(current, next))
                {
                    pendingWidthRecalc = true;
                    pendingWidthRecalcHandling = store.Handling();
                }
                e.Object = next;
                store.Commit(current, next, true);
            });
            SubscribeActivated(item, "WireIntField.Activated", () =>
            {
                int? read = store.Read();
                if (!read.HasValue) return;
                int current = read.Value;
                int parsed = 0;
                string input = GetValidatedUserInput(current.ToString(CultureInfo.InvariantCulture), INPUT_MAX_LENGTH, candidate => int.TryParse(StripInvalidNumericCharacters(candidate, false), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed));
                if (string.IsNullOrEmpty(input))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                int bounded = ApplyHardBounds(entry, parsed);
                if (bounded != parsed) notifications.Notify(NOTIFICATION_KEY_FIELD_ENTRY_HARD_BOUNDED, string.Format(NOTIF_VALUE_BOUNDED, bounded.ToString(CultureInfo.InvariantCulture)));
                parsed = bounded;
                if (parsed == current)
                {
                    item.SelectedItem = current;
                    return;
                }
                if (!store.Write(parsed)) return;
                int? stored = store.Read();
                if (stored.HasValue) parsed = stored.Value;
                if (parsed == current)
                {
                    item.SelectedItem = current;
                    return;
                }
                item.SelectedItem = parsed;
                if (DisplayLengthChanged(current, parsed)) RecalculateVisibleMenuWidth(store.Handling());
                store.Commit(current, parsed, false);
            });
        }

        // Shared by the live, phantom, and pending AdvancedData Slot cyclers so all 3 cycle and display identically
        // The raw value indexes AdvancedDataSlotTable directly, with no hash indirection, so both functions depend only on the current raw value
        // No remembered custom value is needed the way the WeaponHash cycler carries one, since the getter always returns the stored value
        private static string FormatAdvancedDataSlotDisplay(int rawValue)
        {
            return rawValue >= 0 && rawValue < AdvancedDataSlotTable.Length ? AdvancedDataSlotTable[rawValue].DisplayName : "Custom";
        }

        // Custom (-1, the schema's "no slot", and 50-255) is reachable only through Activated's direct entry
        // Cycling from Custom lands on the first or last named entry, so no remembered custom value is needed
        private static int AdvancedDataSlotCycleStep(int current, LemonUI.Menus.Direction direction)
        {
            bool isCustom = current < 0 || current >= AdvancedDataSlotTable.Length;
            if (isCustom) return direction == LemonUI.Menus.Direction.Left ? AdvancedDataSlotTable.Length - 1 : 0;
            return direction == LemonUI.Menus.Direction.Left ? (current - 1 + AdvancedDataSlotTable.Length) % AdvancedDataSlotTable.Length : (current + 1) % AdvancedDataSlotTable.Length;
        }

        // Live raw-offset AdvancedDataSlot cycler: It shows a name instead of the bare int, so it stays a string cycler beside the int rows
        // A typed value and a replayed one are held to AdvancedDataSlotField's range, like the export-only and pending Slot rows
        // Undo/Redo text stays plain-int, since the underlying storage is a raw int either way and no string-encoding scheme is needed
        private void WireAdvancedDataSlotCycler(NativeDynamicItem<string> item, string category, Func<HandlingData, int> getter, Action<HandlingData, int> setter, string fieldKeyOverride, EditorContext owner)
        {
            ShowRowArrows(item);
            var binding = new StringFieldBinding { Item = item, Getter = h => FormatAdvancedDataSlotDisplay(getter(h)), Owner = owner };
            List<StringFieldBinding> stringOwnerList = GetOrAdd(stringFieldBindingsByOwner, owner);
            stringOwnerList.Add(binding);
            string fieldKey = BuildFieldKey(category, fieldKeyOverride ?? item.Title);
            RegisterUndoFocusItem(fieldKey, item);
            if (fieldRegistry.ContainsKey(fieldKey)) logger.Log(Dbg.Level.Warn, Dbg.FieldRegistryKeyCollision(fieldKey));
            fieldRegistry[fieldKey] = (vehicle, text) =>
            {
                int parsed;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return ApplyResult.Rejected();
                setter(vehicle.HandlingData, ApplyHardBounds(AdvancedDataSlotField, parsed));
                return ApplyResult.Applied(ReplayFlags.None);
            };
            SubscribeItemChanged(item, "WireAdvancedDataSlotCycler.ItemChanged", e =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return;
                int current = getter(handling);
                int next = AdvancedDataSlotCycleStep(current, e.Direction);
                setter(handling, next);
                if (DisplayLengthChanged(FormatAdvancedDataSlotDisplay(current), FormatAdvancedDataSlotDisplay(next)))
                {
                    pendingWidthRecalc = true;
                    pendingWidthRecalcHandling = handling;
                }
                e.Object = FormatAdvancedDataSlotDisplay(next);
                RecordCycleUndo(ResolveHandlingKey(vehicle), fieldKey, current.ToString(CultureInfo.InvariantCulture), next.ToString(CultureInfo.InvariantCulture));
            });
            SubscribeActivated(item, "WireAdvancedDataSlotCycler.Activated", () =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return;
                int current = getter(handling);
                int parsed = 0;
                string input = GetValidatedUserInput(current.ToString(CultureInfo.InvariantCulture), INPUT_MAX_LENGTH, candidate => int.TryParse(StripInvalidNumericCharacters(candidate, false), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed));
                if (string.IsNullOrEmpty(input))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                parsed = ApplyHardBounds(AdvancedDataSlotField, parsed);
                setter(handling, parsed);
                item.SelectedItem = FormatAdvancedDataSlotDisplay(parsed);
                if (DisplayLengthChanged(FormatAdvancedDataSlotDisplay(current), FormatAdvancedDataSlotDisplay(parsed))) RecalculateVisibleMenuWidth(handling);
                PushUndo(ResolveHandlingKey(vehicle), fieldKey, current.ToString(CultureInfo.InvariantCulture), parsed.ToString(CultureInfo.InvariantCulture));
            });
        }

        // onFlagsChanged refreshes the submenu's subtitle immediately after a live toggle
        // flag.Name keys the field and its undo entries, since item.Title has the flag's MF_, HF_, DF_, CF_, or SF_ prefix stripped
        private void WireFlagCheckbox(NativeCheckboxItem item, string category, FlagNameEntry flag, EditorContext owner, Func<HandlingData, IntPtr> addressGetter, int offset, Action<uint> onFlagsChanged, string fieldKeyOverride = null)
        {
            WireFlagCheckbox(item, category, flag, owner, addressGetter, offset, onFlagsChanged, RawMemory.ReadFlagBits, RawMemory.WriteFlagBit, fieldKeyOverride);
        }

        // SpecialFlightHandlingFlags is version-gated separately from the other 4 flag categories, so it passes ReadRawUInt and WriteRawUIntBit here
        // The other categories use the always-available primitives
        // afterWrite runs right after each bit write, ahead of the subtitle refresh, so it can correct what the write left behind
        private void WireFlagCheckbox(NativeCheckboxItem item, string category, FlagNameEntry flag, EditorContext owner, Func<HandlingData, IntPtr> addressGetter, int offset, Action<uint> onFlagsChanged, Func<IntPtr, int, uint> reader, Action<IntPtr, int, int, bool> writer, string fieldKeyOverride = null, Func<IntPtr, Vehicle, bool> afterWrite = null)
        {
            string fieldKey = BuildFieldKey(category, fieldKeyOverride ?? flag.Name);
            int bitIndex = flag.BitIndex;
            bool refreshesVehicle = flag.RefreshesVehicle;
            bool requiresRespawn = flag.RequiresRespawn;
            RegisterUndoFocusItem(fieldKey, item);
            if (fieldRegistry.ContainsKey(fieldKey)) logger.Log(Dbg.Level.Warn, Dbg.FieldRegistryKeyCollision(fieldKey));
            fieldRegistry[fieldKey] = (vehicle, text) =>
            {
                bool parsed;
                if (!bool.TryParse(text, out parsed)) return ApplyResult.Rejected();
                IntPtr address = addressGetter(vehicle.HandlingData);
                if (address == IntPtr.Zero) return ApplyResult.CategoryAbsent();
                writer(address, offset, bitIndex, parsed);
                if (afterWrite != null) afterWrite(address, vehicle);
                if (onFlagsChanged != null) onFlagsChanged(reader(address, offset));
                return ApplyResult.Applied(ResolveReplayFlags(refreshesVehicle, requiresRespawn));
            };
            SubscribeCheckboxChanged(item, "WireFlagCheckbox.CheckboxChanged", () =>
            {
                // NativeCheckboxItem.Checked fires this on *any* assignment, including SyncFlagCheckboxes reading live state back into the display
                // During a sync there is nothing to do: The bit already holds this value, and the category subtitle is assigned directly by SyncFieldValues in the same pass
                // Writing it back anyway would cost a memory read-modify-write and a rebuilt subtitle string per bit, for every bit of all 4 categories, on every sync
                if (syncingFlagCheckboxes) return;
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return;
                IntPtr address = addressGetter(handling);
                bool previous = !item.Checked;
                writer(address, offset, bitIndex, item.Checked);
                bool corrected = afterWrite != null && afterWrite(address, vehicle);
                if (onFlagsChanged != null) onFlagsChanged(reader(address, offset));
                if (refreshesVehicle) RefreshSpawnCopiedFields(vehicle, false);
                if (requiresRespawn) RequestAutoRespawn(vehicle, fieldKey);
                PushUndo(ResolveHandlingKey(vehicle), fieldKey, previous.ToString(), item.Checked.ToString());
                // The corrected bit sits on another row, which only a sync redraws
                if (corrected) SyncEditorContext(owner);
            });
        }

        // Guards WireFlagConflicts from treating a display refresh as a user action
        private bool syncingFlagCheckboxes;

        // Two different entries of one table conflict when they share a non-negative group and, in a group with a hub, one of the 2 is that hub
        // The editor's checkbox cascade and Selective Copy both ask this, so the hub rule lives in one place
        private static bool FlagEntriesConflict(FlagNameEntry[] entries, int first, int second)
        {
            if (first == second) return false;
            int group = entries[first].FlagConflictGroup;
            if (group < 0 || entries[second].FlagConflictGroup != group) return false;
            if (entries[first].FlagConflictHub || entries[second].FlagConflictHub) return true;
            for (int i = 0; i < entries.Length; i++) if (entries[i].FlagConflictGroup == group && entries[i].FlagConflictHub) return false;
            return true;
        }

        // Ticking a flag unticks every flag it conflicts with
        private void WireFlagConflicts(List<FlagCheckboxBinding> bindings, FlagNameEntry[] entries)
        {
            if (bindings.Count != entries.Length)
            {
                logger.Log(Dbg.Level.Error, Dbg.FlagConflictGroupLengthMismatch(bindings.Count, entries.Length));
                return;
            }
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].FlagConflictGroup < 0) continue;
                int index = i;
                NativeCheckboxItem item = bindings[i].Item;
                SubscribeCheckboxChanged(item, "WireFlagConflicts.CheckboxChanged", () =>
                {
                    if (syncingFlagCheckboxes || syncingPhantomFlagCheckboxes || !item.Checked) return;
                    // The clicked flag's own undo entry and every sibling entry this loop pushes undo and redo as one event
                    BeginUndoGroup(entries[index].Name);
                    try
                    {
                        for (int j = 0; j < entries.Length; j++)
                        {
                            if (!FlagEntriesConflict(entries, index, j)) continue;
                            bindings[j].Item.Checked = false;
                        }
                    }
                    finally { EndUndoGroup(); }
                });
            }
        }

        // WeaponHashTable's cycle order is Custom when a live slot's raw hash isn't in the table (virtual 0)
        // After that comes every real weapon in the table's own alphabetical order from table index 1
        // None (table index 0) is never cycled to, since slot delete clears a slot. These translate a table index to and from its position in that sequence
        private static int WeaponCycleTableIndexToVirtual(int tableIndex, bool hasCustom) { return hasCustom ? tableIndex : tableIndex - 1; }
        private static int WeaponCycleVirtualIndexToTable(int virtualIndex, bool hasCustom) { return hasCustom ? virtualIndex : virtualIndex + 1; }

        // These weapon properties only expose whole-array setters, so unlike a numeric row's store a cycler replaces one element and writes the full array back
        private void WireWeaponHashCycler(NativeDynamicItem<string> item, string category, int slot, string keySuffix, EditorContext owner)
        {
            ShowRowArrows(item);
            string fieldKey = BuildFieldKey(category, "WeaponHash[" + slot + "]" + keySuffix);
            RegisterUndoFocusItem(fieldKey, item);
            if (fieldRegistry.ContainsKey(fieldKey)) logger.Log(Dbg.Level.Warn, Dbg.FieldRegistryKeyCollision(fieldKey));
            fieldRegistry[fieldKey] = (vehicle, text) =>
            {
                HandlingData handling = vehicle.HandlingData;
                if (handling.VehicleWeaponHandlingData == null) return ApplyResult.CategoryAbsent();
                VehicleWeaponHash[] hashes = handling.VehicleWeaponHandlingData.WeaponHash;
                if (slot >= hashes.Length) return ApplyResult.Rejected();
                if (text.StartsWith(CUSTOM_WEAPON_HASH_REGISTRY_PREFIX, StringComparison.Ordinal))
                {
                    uint customHash;
                    if (!uint.TryParse(text.Substring(CUSTOM_WEAPON_HASH_REGISTRY_PREFIX.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out customHash)) return ApplyResult.Rejected();
                    hashes[slot] = (VehicleWeaponHash)customHash;
                    handling.VehicleWeaponHandlingData.WeaponHash = hashes;
                    return ApplyResult.Applied(ReplayFlags.Respawn);
                }
                int index = Array.FindIndex(WeaponHashTable, entry => entry.DisplayName == text);
                if (index < 0) return ApplyResult.Rejected();
                hashes[slot] = (VehicleWeaponHash)WeaponHashTable[index].Hash;
                handling.VehicleWeaponHandlingData.WeaponHash = hashes;
                return ApplyResult.Applied(ReplayFlags.Respawn);
            };
            SubscribeItemChanged(item, "WireWeaponHashCycler.ItemChanged", e =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling) || handling.VehicleWeaponHandlingData == null) return;
                VehicleWeaponHash[] hashes = handling.VehicleWeaponHandlingData.WeaponHash;
                if (slot >= hashes.Length) return;
                uint[] cyclerCustomHashes = owner.Weapon.CustomWeaponHashes;
                string previousDisplay = (string)e.Object;
                bool hasCustom = cyclerCustomHashes[slot] != 0;
                bool wasCustom = previousDisplay == CUSTOM_WEAPON_HASH_LABEL;
                // Undo and Redo persist across script reloads, but cyclerCustomHashes is rebuilt live per session
                // Encoding the raw hash here means replay never depends on that cache still being warm
                string previousUndoText = wasCustom ? CUSTOM_WEAPON_HASH_REGISTRY_PREFIX + cyclerCustomHashes[slot].ToString("X8", CultureInfo.InvariantCulture) : previousDisplay;
                int tableLen = WeaponHashTable.Length;
                int virtualLen = hasCustom ? tableLen : tableLen - 1;
                int virtualIndex;
                if (wasCustom) virtualIndex = 0;
                else
                {
                    int tableIndex = Array.FindIndex(WeaponHashTable, entry => entry.Hash == (uint)hashes[slot]);
                    virtualIndex = tableIndex > 0 ? WeaponCycleTableIndexToVirtual(tableIndex, hasCustom) : -1;
                }
                // An unset slot shows None, which sits outside the cycle: Right lands on the cycle's first entry and Left on its last
                if (virtualIndex < 0) virtualIndex = e.Direction == LemonUI.Menus.Direction.Left ? virtualLen - 1 : 0;
                else virtualIndex = e.Direction == LemonUI.Menus.Direction.Left ? (virtualIndex - 1 + virtualLen) % virtualLen : (virtualIndex + 1) % virtualLen;
                uint newHash;
                string displayText;
                string cycledUndoText;
                if (hasCustom && virtualIndex == 0)
                {
                    newHash = cyclerCustomHashes[slot];
                    displayText = CUSTOM_WEAPON_HASH_LABEL;
                    cycledUndoText = CUSTOM_WEAPON_HASH_REGISTRY_PREFIX + newHash.ToString("X8", CultureInfo.InvariantCulture);
                }
                else
                {
                    int index = WeaponCycleVirtualIndexToTable(virtualIndex, hasCustom);
                    newHash = WeaponHashTable[index].Hash;
                    displayText = WeaponHashTable[index].DisplayName;
                    cycledUndoText = displayText;
                }
                WriteLiveWeaponHash(item, vehicle, handling, owner, slot, fieldKey, newHash, displayText, previousUndoText, cycledUndoText, false);
                e.Object = displayText;
                // The game sets up vehicle weapons at spawn
                RequestCycleAutoRespawn(vehicle, fieldKey, previousUndoText, cycledUndoText);
            });
            // Enter opens a text prompt for a weapon name, a native name, the start of either, or a raw hash
            SubscribeActivated(item, "WireWeaponHashCycler.Activated", () =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling) || handling.VehicleWeaponHandlingData == null) return;
                VehicleWeaponHash[] hashes = handling.VehicleWeaponHandlingData.WeaponHash;
                if (slot >= hashes.Length) return;
                uint[] typedCustomHashes = owner.Weapon.CustomWeaponHashes;
                string currentDisplay = item.SelectedItem;
                bool isCustom = currentDisplay == CUSTOM_WEAPON_HASH_LABEL;
                string previousUndoText = isCustom ? CUSTOM_WEAPON_HASH_REGISTRY_PREFIX + typedCustomHashes[slot].ToString("X8", CultureInfo.InvariantCulture) : currentDisplay;
                TypedWeaponHashResult typed = PromptForWeaponHash(isCustom ? "0x" + typedCustomHashes[slot].ToString("X8", CultureInfo.InvariantCulture) : currentDisplay);
                if (typed == null)
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                uint newHash;
                string displayText;
                string newUndoText;
                if (typed.Kind == TypedWeaponHashKind.Table)
                {
                    newHash = WeaponHashTable[typed.TableIndex].Hash;
                    displayText = WeaponHashTable[typed.TableIndex].DisplayName;
                    newUndoText = displayText;
                }
                else
                {
                    newHash = typed.RawHash;
                    displayText = CUSTOM_WEAPON_HASH_LABEL;
                    newUndoText = CUSTOM_WEAPON_HASH_REGISTRY_PREFIX + newHash.ToString("X8", CultureInfo.InvariantCulture);
                    typedCustomHashes[slot] = newHash;
                }
                if (newHash == (uint)hashes[slot]) return;
                WriteLiveWeaponHash(item, vehicle, handling, owner, slot, fieldKey, newHash, displayText, previousUndoText, newUndoText, true);
                item.SelectedItem = displayText;
                RequestAutoRespawn(vehicle, fieldKey);
            });
        }

        // A change between unset and a real weapon crosses the boundary: Every other per-slot field follows, and the crossing records its own undo entry
        // Any other change records a batched cycle step for Left and Right, or its own entry for typed text
        // The caller sets the row's displayed text and requests the respawn, since the game sets up vehicle weapons at spawn
        private void WriteLiveWeaponHash(NativeDynamicItem<string> item, Vehicle vehicle, HandlingData handling, EditorContext context, int slot, string fieldKey, uint newHash, string displayText, string previousUndoText, string newUndoText, bool typed)
        {
            VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
            VehicleWeaponHash[] hashes = weaponData.WeaponHash;
            FollowSelectedVehicleWeapon(vehicle, hashes[slot], (VehicleWeaponHash)newHash);
            bool newUnset = IsUnsetWeaponHash((VehicleWeaponHash)newHash);
            if (IsUnsetWeaponHash(hashes[slot]) != newUnset)
            {
                ApplyWeaponHashBoundaryCross(weaponData, slot, newHash, ResolveHandlingKey(vehicle));
                hashes = weaponData.WeaponHash;
            }
            else
            {
                hashes[slot] = (VehicleWeaponHash)newHash;
                weaponData.WeaponHash = hashes;
                if (typed) PushUndo(ResolveHandlingKey(vehicle), fieldKey, previousUndoText, newUndoText);
                else RecordCycleUndo(ResolveHandlingKey(vehicle), fieldKey, previousUndoText, newUndoText);
            }
            item.Description = WeaponHashDescription(newHash);
            // The top-level list's title needs updating here too, not just on reorder, or it goes stale the moment the weapon actually changes
            WeaponSlotItems[] slots = context.Weapon.WeaponSlots;
            slots[slot].SubmenuItem.Title = displayText == CUSTOM_WEAPON_HASH_LABEL ? string.Format(TITLE_WEAPON_SLOT_CUSTOM, (slot + 1)) : newUnset ? string.Format(TITLE_WEAPON_SLOT, (slot + 1)) : string.Format(TITLE_WEAPON_SLOT_NAMED, (slot + 1), displayText);
            // Undo and Redo resync the slot list on their own, so only the live path needs this call
            SyncWeaponSlotVisibility(GetLiveMenuForCategory(context, CATEGORY_WEAPON), context.Weapon.CurrentWeaponSlotItems, slots, context.Weapon.WeaponDamageScaledToVehHealthMultItem, hashes);
            pendingWidthRecalc = true;
        }

        // An ambiguous text posts one toast naming up to WEAPON_HASH_CANDIDATES_SHOWN matches and reopens the prompt with the text kept, like any other rejected entry
        private TypedWeaponHashResult PromptForWeaponHash(string initialText)
        {
            TypedWeaponHashResult accepted = null;
            string input = GetValidatedUserInput(initialText, WEAPON_HASH_INPUT_MAX_LENGTH, candidate =>
            {
                TypedWeaponHashResult resolved = ResolveTypedWeaponHash(candidate);
                if (resolved.Kind == TypedWeaponHashKind.Ambiguous) notifications.Notify(NOTIFICATION_KEY_WEAPON_HASH_AMBIGUOUS, string.Format(NOTIF_WEAPON_HASH_AMBIGUOUS, FormatWeaponHashCandidates(resolved.Candidates)));
                if (resolved.Kind != TypedWeaponHashKind.Table && resolved.Kind != TypedWeaponHashKind.Raw) return false;
                accepted = resolved;
                return true;
            }, WindowTitle.EnterMessage60);
            return string.IsNullOrEmpty(input) ? null : accepted;
        }

        private static string FormatWeaponHashCandidates(List<string> candidates)
        {
            int shown = Math.Min(candidates.Count, WEAPON_HASH_CANDIDATES_SHOWN);
            string text = string.Join(", ", candidates.GetRange(0, shown).ToArray());
            return candidates.Count > shown ? text + string.Format(NOTIF_WEAPON_HASH_MORE, candidates.Count - shown) : text;
        }

        // Cycles through a fixed display-name table and writes the whole array back, since these weapon properties only expose whole-array setters
        // getArray and setArray pick the array property. WireWeaponHashCycler keeps its own body, because its Custom entry and boundary cross have no equivalent here
        // The game sets up vehicle weapons at spawn, so a cycle requests an auto respawn and a replay returns the respawn flag
        private void WireLiveNameTableCycler<TEnum>(NativeDynamicItem<string> item, string fieldKey, int slot, string[] names, Func<HandlingData, TEnum[]> getArray, Action<HandlingData, TEnum[]> setArray, Func<string, TEnum?> tryParse, EditorContext owner) where TEnum : struct
        {
            ShowRowArrows(item);
            RegisterUndoFocusItem(fieldKey, item);
            if (fieldRegistry.ContainsKey(fieldKey)) logger.Log(Dbg.Level.Warn, Dbg.FieldRegistryKeyCollision(fieldKey));
            fieldRegistry[fieldKey] = (vehicle, text) =>
            {
                HandlingData handling = vehicle.HandlingData;
                if (handling.VehicleWeaponHandlingData == null) return ApplyResult.CategoryAbsent();
                TEnum[] values = getArray(handling);
                if (slot >= values.Length) return ApplyResult.Rejected();
                TEnum? parsed = tryParse(text);
                if (!parsed.HasValue) return ApplyResult.Rejected();
                values[slot] = parsed.Value;
                setArray(handling, values);
                return ApplyResult.Applied(ReplayFlags.Respawn);
            };
            SubscribeItemChanged(item, "WireLiveNameTableCycler.ItemChanged", e =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling) || handling.VehicleWeaponHandlingData == null) return;
                TEnum[] values = getArray(handling);
                if (slot >= values.Length) return;
                string previous = (string)e.Object;
                int index = Array.IndexOf(names, previous);
                if (index < 0) index = 0;
                index = e.Direction == LemonUI.Menus.Direction.Left ? (index - 1 + names.Length) % names.Length : (index + 1) % names.Length;
                values[slot] = tryParse(names[index]).Value;
                setArray(handling, values);
                e.Object = names[index];
                RecordCycleUndo(ResolveHandlingKey(vehicle), fieldKey, previous, names[index]);
                pendingWidthRecalc = true;
                RequestCycleAutoRespawn(vehicle, fieldKey, previous, names[index]);
            });
        }

        private void WireWeaponSeatCycler(NativeDynamicItem<string> item, string category, int slot, string keySuffix, EditorContext owner)
        {
            WireLiveNameTableCycler(item, BuildFieldKey(category, "WeaponSeats[" + slot + "]" + keySuffix), slot, VehicleSeatNames, h => h.VehicleWeaponHandlingData.WeaponSeats, (h, v) => h.VehicleWeaponHandlingData.WeaponSeats = v, text =>
            {
                VehicleSeat parsed;
                return Enum.TryParse(text, out parsed) ? (VehicleSeat?)parsed : null;
            }, owner);
        }

        private void WireWeaponModTypeCycler(NativeDynamicItem<string> item, string category, int slot, string keySuffix, EditorContext owner)
        {
            WireLiveNameTableCycler(item, BuildFieldKey(category, "WeaponVehicleModType[" + slot + "]" + keySuffix), slot, VehicleModTypeNames, h => h.VehicleWeaponHandlingData.WeaponVehicleModType, (h, v) => h.VehicleWeaponHandlingData.WeaponVehicleModType = v, text =>
            {
                VehicleModType parsed;
                return Enum.TryParse(text, out parsed) ? (VehicleModType?)parsed : null;
            }, owner);
        }

        // Resolves which of a tree's menus owns each of its wired items, once that tree's every menu exists. A binding is only ever owned by its own tree's menus
        private void ResolveBindingMenus(EditorContext context)
        {
            var owners = new Dictionary<NativeItem, NativeMenu>();
            NativeMenu[] menus = context.AllOwnMenus;
            for (int i = 0; i < menus.Length; i++)
            {
                for (int j = 0; j < menus[i].Items.Count; j++) owners[menus[i].Items[j]] = menus[i];
            }
            List<FloatFieldBinding> floatBindings;
            if (floatFieldBindingsByOwner.TryGetValue(context, out floatBindings))
            {
                for (int i = 0; i < floatBindings.Count; i++)
                {
                    NativeMenu owner;
                    if (owners.TryGetValue(floatBindings[i].Item, out owner)) floatBindings[i].Menu = owner;
                }
            }
            List<IntFieldBinding> intBindings;
            if (intFieldBindingsByOwner.TryGetValue(context, out intBindings))
            {
                for (int i = 0; i < intBindings.Count; i++)
                {
                    NativeMenu owner;
                    if (owners.TryGetValue(intBindings[i].Item, out owner)) intBindings[i].Menu = owner;
                }
            }
            List<StringFieldBinding> stringBindings;
            if (stringFieldBindingsByOwner.TryGetValue(context, out stringBindings))
            {
                for (int i = 0; i < stringBindings.Count; i++)
                {
                    NativeMenu owner;
                    if (owners.TryGetValue(stringBindings[i].Item, out owner)) stringBindings[i].Menu = owner;
                }
            }
        }

        private void RefreshCurrentEditorTitle()
        {
            bool hasVehicle = !Entity.IsNullOrNotExisting(currentContext.LastSyncedVehicle);
            currentContext.MainMenuItem.Title = hasVehicle && AnyAdditionalEditorMounted() ? string.Format(TITLE_EDIT_CURRENT_NAMED, modelNames.ResolveModelName(currentContext.LastSyncedVehicle)) : TITLE_EDIT_CURRENT_HANDLING;
        }

        // A null scope refreshes every menu, which a vehicle change or descriptor reload needs
        private void SyncFieldValues(NativeMenu scope = null)
        {
            SyncFieldValues(scope, currentContext.TargetVehicle);
        }

        // The global concerns a mounted extra editor has no equivalent for (main menu item enablement, Settings) stay here
        // They are not part of the shared SyncEditorContextFieldValues body
        private void SyncFieldValues(NativeMenu scope, Vehicle resolvedVehicle)
        {
            Vehicle vehicle = resolvedVehicle;
            bool vehicleChanged = vehicle != currentContext.LastSyncedVehicle;
            // Each slot's custom hash belongs to the vehicle it was read from. The next sync re-captures it only if the new vehicle's own slot is also custom
            if (vehicleChanged) Array.Clear(currentContext.Weapon.CustomWeaponHashes, 0, currentContext.Weapon.CustomWeaponHashes.Length);
            // Asserting flags are per-field, not per-vehicle
            // Without this, a stale AssertedValue from a previously edited vehicle would force itself onto a different vehicle's handling the moment this menu reopens
            // Only Current's own bindings are cleared (Owner == currentContext), so a mounted editor's asserted fields are untouched by Current's vehicle changing
            if (vehicleChanged) ClearAssertingBindings(currentContext);
            currentContext.LastSyncedVehicle = vehicle;
            bool hasVehicle = !Entity.IsNullOrNotExisting(vehicle);
            RefreshCurrentEditorTitle();
            currentContext.MainMenuItem.Enabled = hasVehicle;
            // Unload, Respawn, Exports, and Save are shared main menu actions that act on the Active editor, not necessarily Current's vehicle
            // Current's own row above stays Current-specific, since it only ever navigates into Current's tree
            SyncActiveActionEnablement();
            SyncSettingsMenuItems();
            if (!hasVehicle && IsEditMenuVisible(currentContext)) notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_EDIT, NOTIF_NO_VEHICLE_EDIT);
            SyncEditorContextFieldValues(currentContext, scope, vehicle);
        }

        // Current's pass also runs the global concerns SyncFieldValues owns, while every other tree gets the shared field sync alone
        private void SyncEditorContext(EditorContext context, NativeMenu scope = null)
        {
            if (context == currentContext) SyncFieldValues(scope);
            else SyncEditorContextFieldValues(context, scope, context.TargetVehicle);
        }

        // Current's state and a mounted editor's state are both plain EditorContext instances, so this function has no Current-specific branch
        private void SyncEditorContextFieldValues(EditorContext context, NativeMenu scope, Vehicle vehicle)
        {
            bool hasVehicle = !Entity.IsNullOrNotExisting(vehicle);
            HandlingData handling = hasVehicle ? vehicle.HandlingData : null;
            if (!hasVehicle || !handling.IsValid) return;
            List<FloatFieldBinding> ownerFloatBindings;
            if (floatFieldBindingsByOwner.TryGetValue(context, out ownerFloatBindings))
            {
                for (int i = 0; i < ownerFloatBindings.Count; i++)
                {
                    FloatFieldBinding binding = ownerFloatBindings[i];
                    if (scope != null && binding.Menu != null && binding.Menu != scope) continue;
                    float value = binding.Getter(handling);
                    // Before the unchanged-value skip, so a description prefix still follows a value written outside this binding
                    RefreshValueDescription(binding, value);
                    if (binding.Synced && (float)binding.Item.SelectedItem == value) continue;
                    binding.Item.SelectedItem = value;
                    binding.Synced = true;
                }
            }
            List<IntFieldBinding> ownerIntBindings;
            if (intFieldBindingsByOwner.TryGetValue(context, out ownerIntBindings))
            {
                for (int i = 0; i < ownerIntBindings.Count; i++)
                {
                    IntFieldBinding binding = ownerIntBindings[i];
                    if (scope != null && binding.Menu != null && binding.Menu != scope) continue;
                    int value = binding.Getter(handling);
                    if (binding.Synced && binding.Item.SelectedItem == value) continue;
                    binding.Item.SelectedItem = value;
                    binding.Synced = true;
                }
            }
            List<StringFieldBinding> ownerStringBindings;
            if (stringFieldBindingsByOwner.TryGetValue(context, out ownerStringBindings))
            {
                for (int i = 0; i < ownerStringBindings.Count; i++)
                {
                    StringFieldBinding binding = ownerStringBindings[i];
                    if (scope != null && binding.Menu != null && binding.Menu != scope) continue;
                    string value = binding.Getter(handling);
                    if (binding.Synced && binding.Item.SelectedItem == value) continue;
                    binding.Item.SelectedItem = value;
                    binding.Synced = true;
                }
            }
            context.RootMenu.Name = modelNames.ResolveModelName(vehicle);
            context.Flags.ModelFlagsMenu.Name = FlagsMenuName(SUBTITLE_MODEL_FLAGS_VALUE, (uint)handling.ModelFlags, ModelFlagEntries);
            context.Flags.HandlingFlagsMenu.Name = FlagsMenuName(SUBTITLE_HANDLING_FLAGS_VALUE, (uint)handling.HandlingFlags, HandlingFlagEntries);
            context.Flags.DamageFlagsMenu.Name = FlagsMenuName(SUBTITLE_DAMAGE_FLAGS_VALUE, (uint)handling.DamageFlags, DamageFlagEntries);
            int aiIndex = 0;
            for (int i = 0; i < AIHandlingNames.Length; i++)
            {
                if (AIHandlingHashes[i] == (uint)handling.AIHandlingHash)
                {
                    aiIndex = i;
                    break;
                }
            }
            context.AiHandlingItem.SelectedItem = AIHandlingNames[aiIndex];
            context.AiHandlingItem.Description = AIHandlingDescription(AIHandlingNames[aiIndex]);
            try
            {
                syncingFlagCheckboxes = true;
                SyncFlagCheckboxes(context.Flags.ModelFlagBindings, (uint)handling.ModelFlags);
                SyncFlagCheckboxes(context.Flags.HandlingFlagBindings, (uint)handling.HandlingFlags);
                SyncFlagCheckboxes(context.Flags.DamageFlagBindings, (uint)handling.DamageFlags);
                if (handling.CarHandlingData != null)
                {
                    FindSubHandlingCategory(context, CATEGORY_CAR).LiveFlagsMenu.Name = FlagsMenuName(SUBTITLE_ADVANCED_FLAGS_VALUE, (uint)handling.CarHandlingData.AdvancedFlags, AdvancedFlagEntries);
                    SyncFlagCheckboxes(context.Flags.AdvancedFlagBindings, (uint)handling.CarHandlingData.AdvancedFlags);
                }
                if (handling.SpecialFlightHandlingData != null)
                {
                    uint specialFlightFlags = RawMemory.ReadRawUInt(handling.SpecialFlightHandlingData.MemoryAddress, SpecialFlightLayout.Flags);
                    FindSubHandlingCategory(context, CATEGORY_SPECIAL_FLIGHT).LiveFlagsMenu.Name = FlagsMenuName(SUBTITLE_SPECIAL_FLIGHT_FLAGS_VALUE, specialFlightFlags, SpecialFlightFlagEntries);
                    SyncFlagCheckboxes(context.Flags.SpecialFlightFlagBindings, specialFlightFlags);
                    SyncWorksUpsideDownLock(context.Flags.SpecialFlightFlagBindings, specialFlightFlags, vehicle);
                }
            }
            finally { syncingFlagCheckboxes = false; }
            if (handling.CarHandlingData != null)
            {
                // An emptied array (a delete, applying a handling, or an undo of either) backs the player out before the rows it would show are removed
                AdvancedDataMenus liveLists = context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live];
                if (RawMemory.GetAdvancedDataCount(handling) == 0) BackOutAdvancedDataMenus(liveLists);
                SyncAdvancedDataVisibility(context, AdvancedDataMenuKind.Live);
                dirtyWidthMenus.Add(liveLists.ListMenu);
                AdvancedDataItemSet[] liveEntries = liveLists.Entries;
                for (int i = 0; i < liveEntries.Length; i++) if (liveEntries[i].Menu != null) dirtyWidthMenus.Add(liveEntries[i].Menu);
                SyncLiveAdvancedDataSlot(context);
            }
            if (handling.VehicleWeaponHandlingData != null)
            {
                VehicleWeaponHash[] weaponHashes = handling.VehicleWeaponHandlingData.WeaponHash;
                VehicleSeat[] weaponSeats = handling.VehicleWeaponHandlingData.WeaponSeats;
                VehicleModType[] weaponModTypes = weaponModTypeSupported ? handling.VehicleWeaponHandlingData.WeaponVehicleModType : null;
                for (int i = 0; i < context.Weapon.WeaponSlots.Length; i++)
                {
                    if (i < weaponHashes.Length)
                    {
                        uint rawHash = (uint)weaponHashes[i];
                        int hashIndex = Array.FindIndex(WeaponHashTable, entry => entry.Hash == rawHash);
                        string displayName;
                        if (hashIndex >= 0) displayName = WeaponHashTable[hashIndex].DisplayName;
                        else if (IsUnsetWeaponHash(weaponHashes[i])) displayName = WeaponHashTable[0].DisplayName;
                        else
                        {
                            displayName = CUSTOM_WEAPON_HASH_LABEL;
                            context.Weapon.CustomWeaponHashes[i] = rawHash;
                        }
                        context.Weapon.WeaponSlots[i].WeaponHashItem.SelectedItem = displayName;
                        context.Weapon.WeaponSlots[i].WeaponHashItem.Description = WeaponHashDescription(rawHash);
                        // Without a content-aware title, the list row stays a static "Weapon Slot N" container that never shows which weapon a reorder swapped into it
                        context.Weapon.WeaponSlots[i].SubmenuItem.Title = IsUnsetWeaponHash(weaponHashes[i]) ? string.Format(TITLE_WEAPON_SLOT, (i + 1)) : string.Format(TITLE_WEAPON_SLOT_NAMED, (i + 1), displayName);
                    }
                    if (i < weaponSeats.Length) context.Weapon.WeaponSlots[i].WeaponSeatItem.SelectedItem = weaponSeats[i].ToString();
                    if (weaponModTypes != null && context.Weapon.WeaponSlots[i].WeaponModTypeItem != null && i < weaponModTypes.Length) context.Weapon.WeaponSlots[i].WeaponModTypeItem.SelectedItem = weaponModTypes[i].ToString();
                }
                NativeMenu weaponMenu = GetLiveMenuForCategory(context, CATEGORY_WEAPON);
                SyncWeaponSlotVisibility(weaponMenu, context.Weapon.CurrentWeaponSlotItems, context.Weapon.WeaponSlots, context.Weapon.WeaponDamageScaledToVehHealthMultItem, weaponHashes);
                SyncTurretPitchLimitVisibility(context, RawMemory.HasRearMountedTurret(vehicle));
                dirtyWidthMenus.Add(weaponMenu);
            }
            SyncRocketBoostVisibility(context, vehicle.HasRocketBoost);
            SyncSubHandlingVisibility(context, vehicle, handling);
            dirtyWidthMenus.Add(context.RootMenu);
        }

        // Every write to the SpecialFlight flags ends here, since holding the roof control enters hover mode on any vehicle with SpecialFlight data
        // Returns True when it had to turn the flag back on
        private bool EnforceWorksUpsideDown(IntPtr specialFlightAddress, Vehicle vehicle)
        {
            if (!RawMemory.ForceWorksUpsideDownWithoutHoverBone(specialFlightAddress, vehicle)) return false;
            ReportWorksUpsideDownForced(vehicle);
            return true;
        }

        private void ReportWorksUpsideDownForced(Vehicle vehicle)
        {
            logger.Log(Dbg.Level.Warn, Dbg.WorksUpsideDownForced(modelNames.ResolveModelName(vehicle), vehicle.Model.Hash));
            notifications.Notify(NOTIFICATION_KEY_WORKS_UPSIDE_DOWN_FORCED, NOTIF_WORKS_UPSIDE_DOWN_FORCED);
        }

        // With SF_WORKS_UPSIDE_DOWN off, hover mode moves the misc_e bone every frame, and a vehicle without that bone crashes the game
        // While the flag is set on such a vehicle its row is disabled, so it cannot be turned off from the editor
        // A row the build gate already disabled is never enabled here
        private void SyncWorksUpsideDownLock(List<FlagCheckboxBinding> bindings, uint flags, Vehicle vehicle)
        {
            if (!RawMemory.RawOffsetsSupported || !RawMemory.SpecialFlightFlagsSupported) return;
            ApplyWorksUpsideDownLock(bindings, flags, vehicle);
        }

        // The export-only flag rows have no build gate, so they lock through this directly
        private void ApplyWorksUpsideDownLock(List<FlagCheckboxBinding> bindings, uint flags, Vehicle vehicle)
        {
            for (int i = 0; i < bindings.Count; i++)
            {
                FlagCheckboxBinding binding = bindings[i];
                if (binding.BitIndex != SF_WORKS_UPSIDE_DOWN_BIT) continue;
                bool locked = (flags & (1u << SF_WORKS_UPSIDE_DOWN_BIT)) != 0 && !Entity.IsNullOrNotExisting(vehicle) && !vehicle.Bones.Contains(HOVER_SLIDE_BONE_NAME);
                bool alreadyInState = binding.Item.Enabled == !locked;
                if (alreadyInState) return;
                binding.Item.Enabled = !locked;
                if (binding.Tracked == null) return;
                string hexPrefix = FlagHashPrefix(SF_WORKS_UPSIDE_DOWN_BIT);
                binding.Tracked.Prefix = locked ? DESC_WORKS_UPSIDE_DOWN_LOCK_PREFIX + hexPrefix : hexPrefix;
                binding.Item.Description = binding.Tracked.Prefix + ResolveDescriptor(binding.Tracked.Category, binding.Tracked.FieldName);
                return;
            }
        }

        // Refreshes Current's tree plus every mounted extra editor, for an event that affects every live tree at once (a descriptor reload)
        private void SyncAllMountedEditors()
        {
            for (int i = 0; i < editorContexts.Length; i++)
            {
                if (editorContexts[i] == currentContext || editorContexts[i].TargetVehicle != null) SyncEditorContext(editorContexts[i]);
            }
        }

        // After a slot move, add, or delete, the old asserted values belong to the slot's previous contents
        private void ReleaseWeaponSlotAssertions()
        {
            foreach (FloatFieldBinding binding in weaponSlotFloatBindings)
            {
                if (!binding.Asserting) continue;
                binding.Asserting = false;
                assertingFloatBindings.Remove(binding);
            }
        }

        // After entries move in memory, the old asserted values belong to the previous entry at each index
        private void ReleaseAdvancedDataAssertions()
        {
            foreach (FloatFieldBinding binding in advancedDataFloatBindings)
            {
                if (!binding.Asserting) continue;
                binding.Asserting = false;
                assertingFloatBindings.Remove(binding);
            }
            foreach (IntFieldBinding binding in advancedDataIntBindings)
            {
                if (!binding.Asserting) continue;
                binding.Asserting = false;
                assertingIntBindings.Remove(binding);
            }
        }

        // Applying a handling, and its undo and redo, write every field without going through the bindings, so every tree editing that handling would otherwise
        // reassert its last menu edits over the applied values on the next tick
        private void ReleaseAssertionsForHandling(HandlingKey handling)
        {
            if (!handling.IsValid) return;
            for (int i = 0; i < editorContexts.Length; i++)
            {
                if (ResolveHandlingKey(editorContexts[i].TargetVehicle).Equals(handling)) ClearAssertingBindings(editorContexts[i]);
            }
        }

        private void ClearAssertingBindings(EditorContext owner)
        {
            for (int i = assertingFloatBindings.Count - 1; i >= 0; i--)
            {
                if (assertingFloatBindings[i].Owner != owner) continue;
                assertingFloatBindings[i].Asserting = false;
                assertingFloatBindings.RemoveAt(i);
            }
            for (int i = assertingIntBindings.Count - 1; i >= 0; i--)
            {
                if (assertingIntBindings[i].Owner != owner) continue;
                assertingIntBindings[i].Asserting = false;
                assertingIntBindings.RemoveAt(i);
            }
        }

        // The API never moves its scroll window when items are removed, so a window scrolled to the bottom keeps its old first row
        // It then draws fewer rows than the description box below it is spaced for, leaving a gap
        // This pulls the first row back so the window ends on the last item, and callers reassign SelectedIndex right after, which redraws
        // The API has no setter for the first row, so its private field is reached by reflection
        private static readonly FieldInfo MenuFirstItemField = typeof(NativeMenu).GetField("firstItem", BindingFlags.NonPublic | BindingFlags.Instance);
        private bool menuFirstItemFieldMissingLogged;
        private bool IsMenuFirstItemFieldUsable()
        {
            if (MenuFirstItemField != null && MenuFirstItemField.FieldType == typeof(int)) return true;
            if (!menuFirstItemFieldMissingLogged)
            {
                menuFirstItemFieldMissingLogged = true;
                logger.Log(Dbg.Level.Warn, Dbg.MenuScrollWindowFieldMissing());
            }
            return false;
        }

        private void ClampMenuScrollWindow(NativeMenu menu)
        {
            if (!IsMenuFirstItemFieldUsable()) return;
            int firstItem = (int)MenuFirstItemField.GetValue(menu);
            int lastFullFirstItem = Math.Max(0, menu.Items.Count - menu.MaxItems);
            if (firstItem > lastFullFirstItem) MenuFirstItemField.SetValue(menu, lastFullFirstItem);
        }

        // Fits the scroll window to a new row count before it applies: Full at the bottom of the list, and still holding the highlighted row
        private void FitMenuScrollWindow(NativeMenu menu, int maxItems)
        {
            if (!IsMenuFirstItemFieldUsable()) return;
            int firstItem = (int)MenuFirstItemField.GetValue(menu);
            int selected = menu.SelectedIndex;
            if (selected >= firstItem + maxItems) firstItem = selected - maxItems + 1;
            firstItem = Math.Min(firstItem, Math.Max(0, menu.Items.Count - maxItems));
            MenuFirstItemField.SetValue(menu, firstItem);
        }

        // 0 with the field missing, which leaves the library's own window behavior in place
        private int ReadMenuScrollWindow(NativeMenu menu) { return IsMenuFirstItemFieldUsable() ? (int)MenuFirstItemField.GetValue(menu) : 0; }

        // Puts a rebuilt list's view back: Full at the bottom of the list, and still holding the row about to be selected
        // Callers assign SelectedIndex right after, which redraws
        private void RestoreMenuScrollWindow(NativeMenu menu, int firstItem, int row)
        {
            if (!IsMenuFirstItemFieldUsable()) return;
            firstItem = Math.Min(firstItem, Math.Max(0, menu.Items.Count - menu.MaxItems));
            if (row < firstItem) firstItem = row;
            else if (row >= firstItem + menu.MaxItems) firstItem = row - menu.MaxItems + 1;
            MenuFirstItemField.SetValue(menu, firstItem);
        }

        // The highlight bar behind a menu's selected row is a private texture with no public color, so a result flash reaches it by reflection
        private static readonly FieldInfo MenuSelectedRectField = typeof(NativeMenu).GetField("selectedRect", BindingFlags.NonPublic | BindingFlags.Instance);
        private bool menuSelectedRectFieldMissingLogged;
        private LemonUI.Elements.ScaledTexture ReadMenuHighlight(NativeMenu menu)
        {
            if (MenuSelectedRectField == null || MenuSelectedRectField.FieldType != typeof(LemonUI.Elements.ScaledTexture))
            {
                if (!menuSelectedRectFieldMissingLogged)
                {
                    menuSelectedRectFieldMissingLogged = true;
                    logger.Log(Dbg.Level.Warn, Dbg.MenuSelectedRectFieldMissing());
                }
                return null;
            }
            return (LemonUI.Elements.ScaledTexture)MenuSelectedRectField.GetValue(menu);
        }

        // A row's text elements are private and painted from its ColorSet, so a result flash paints them directly and the ColorSet keeps the row's state colors
        // The row's next UpdateColors repaints them from the ColorSet, which is how a flash hands the text back
        private static readonly FieldInfo ItemTitleField = typeof(NativeItem).GetField("title", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo ItemAltTitleField = typeof(NativeItem).GetField("altTitle", BindingFlags.NonPublic | BindingFlags.Instance);
        // The value text of a list or cycler row is a field of that row's own class, so its lookup is cached per class
        private readonly Dictionary<Type, FieldInfo> itemValueTextFields = new Dictionary<Type, FieldInfo>();
        private bool itemTextFieldsMissingLogged;
        private void PaintItemText(NativeItem item, System.Drawing.Color color)
        {
            if (ItemTitleField == null || ItemAltTitleField == null)
            {
                if (!itemTextFieldsMissingLogged)
                {
                    itemTextFieldsMissingLogged = true;
                    logger.Log(Dbg.Level.Warn, Dbg.MenuItemTextFieldsMissing());
                }
                return;
            }
            PaintScaledText(ItemTitleField.GetValue(item), color);
            PaintScaledText(ItemAltTitleField.GetValue(item), color);
            FieldInfo valueTextField = ResolveItemValueTextField(item.GetType());
            if (valueTextField != null) PaintScaledText(valueTextField.GetValue(item), color);
        }

        private static void PaintScaledText(object element, System.Drawing.Color color)
        {
            LemonUI.Elements.ScaledText text = element as LemonUI.Elements.ScaledText;
            if (text != null) text.Color = color;
        }

        // Climbs the class chain, since a private field declared on a base class is not returned for the derived class
        private FieldInfo ResolveItemValueTextField(Type itemType)
        {
            FieldInfo field;
            if (itemValueTextFields.TryGetValue(itemType, out field)) return field;
            for (Type level = itemType; level != null && level != typeof(NativeItem) && field == null; level = level.BaseType) field = level.GetField("text", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null && field.FieldType != typeof(LemonUI.Elements.ScaledText)) field = null;
            itemValueTextFields[itemType] = field;
            return field;
        }

        // Adds *before* removing so Items.Count never dips, avoiding a SelectedIndex scroll-reset bug in the API
        private void SyncMenuSlot(NativeMenu menu, ref NativeItem currentSlotItem, NativeItem desiredItem, int insertPosition = -1)
        {
            if (currentSlotItem == desiredItem) return;
            if (desiredItem != null)
            {
                if (insertPosition < 0) menu.Add(desiredItem);
                else menu.Add(insertPosition, desiredItem);
            }
            if (currentSlotItem != null) menu.Items.Remove(currentSlotItem);
            currentSlotItem = desiredItem;
            ClampMenuScrollWindow(menu);
            // Raw list Remove does not trigger a layout refresh, so reassigning SelectedIndex forces one
            // Math.Max(0, ...) guards a stale negative internal index left over from a prior empty-list state, which SelectedIndex's own getter never clamps
            if (menu.Items.Count > 0) menu.SelectedIndex = Math.Max(0, menu.SelectedIndex);
        }

        // One row per definition holds that category's menus and items in one tree, at the definition's index
        // Everything about the category that does not depend on a tree is read off Definition
        private class SubHandlingCategory
        {
            public CategoryDefinition Definition;
            public NativeMenu LiveMenu;
            public NativeSubmenuItem LiveSubmenu;
            // Null for a category with no flags submenu
            public NativeMenu LiveFlagsMenu;
            public NativeItem Placeholder;
            public NativeMenu PhantomMenu;
            public NativeSubmenuItem PhantomSubmenu;
            public NativeMenu PhantomFlagsMenu;
        }

        // The context of the extra editor whose tree is visible, else Current's, which includes the case where no editor tree is visible at all
        private EditorContext ResolveVisibleEditorContext()
        {
            for (int i = 1; i < editorContexts.Length; i++)
            {
                NativeMenu[] ownMenus = editorContexts[i].AllOwnMenus;
                if (ownMenus == null) continue;
                for (int j = 0; j < ownMenus.Length; j++) { if (ownMenus[j] != null && ownMenus[j].Visible) return editorContexts[i]; }
            }
            return currentContext;
        }

        // The row named name in context's own category table, since each tree's rows hold that tree's own menus and items
        // Null for a name no definition carries, and for a tree that has not finished building its rows
        private SubHandlingCategory FindSubHandlingCategory(EditorContext context, string name)
        {
            CategoryDefinition definition = ResolveCategoryDefinition(name);
            if (definition == null || context.Categories == null) return null;
            return context.Categories[definition.Index];
        }

        private NativeMenu GetLiveMenuForCategory(EditorContext context, string category)
        {
            SubHandlingCategory row = FindSubHandlingCategory(context, category);
            return row != null ? row.LiveMenu : null;
        }

        // Reused across every SyncSubHandlingVisibility call for every tree
        // Safe because each call fully overwrites indices 0 to categories.Length - 1 before reading any of them back, and the function is never re-entrant
        private NativeItem[] resolvedSubHandlingScratch;

        // The category block always sits immediately after context.Flags.DamageFlagsItem in rootMenu's item list
        // Every menu it reads or backs out of comes from context, so a sync of one tree never closes another tree's submenus
        private void SyncSubHandlingVisibility(EditorContext context, Vehicle vehicle, HandlingData handling)
        {
            NativeMenu rootMenu = context.RootMenu;
            SubHandlingCategory[] categories = context.Categories;
            NativeItem[] currentSlotItems = context.CurrentSubDataSlotItems;
            string[] creationCandidates = GetSubDataCreationCandidates(vehicle.Type, vehicle.ClassType);
            HashSet<string> toggled;
            pendingSubDataToggle.TryGetValue(ResolveHandlingKey(vehicle), out toggled);
            int insertPosition = rootMenu.Items.IndexOf(context.Flags.DamageFlagsItem) + 1;
            if (resolvedSubHandlingScratch == null || resolvedSubHandlingScratch.Length < categories.Length) resolvedSubHandlingScratch = new NativeItem[categories.Length];
            NativeItem[] resolved = resolvedSubHandlingScratch;
            for (int i = 0; i < categories.Length; i++)
            {
                SubHandlingCategory category = categories[i];
                string categoryName = category.Definition.Name;
                bool present = category.Definition.IsPresent(handling);
                bool isToggled = toggled != null && toggled.Contains(categoryName);
                if (present)
                {
                    if (isToggled)
                    {
                        category.Placeholder.Description = PLACEHOLDER_CATEGORY_EXCLUDED;
                        resolved[i] = category.Placeholder;
                    }
                    else
                    {
                        // A submenu row copies its menu's description every frame, so the required-category text goes on the menu, and it is the row's whole description
                        string liveDescription = IsLiveCreationOffered(categoryName, vehicle.Type) && IsLiveDeletionRefused(categoryName, vehicle) ? DESC_CATEGORY_REQUIRED_LIVE : string.Empty;
                        if (category.LiveMenu.Description != liveDescription) category.LiveMenu.Description = liveDescription;
                        resolved[i] = category.LiveSubmenu;
                    }
                }
                else if (Array.IndexOf(creationCandidates, categoryName) < 0) resolved[i] = null;
                else if (isToggled) resolved[i] = category.PhantomSubmenu;
                else
                {
                    bool liveOnThisType = IsLiveCreationOffered(categoryName, vehicle.Type);
                    // With the setting, the offsets, and the plugin all in place, the allow list is the only thing keeping the category export-only
                    bool onlyAllowListRefuses = !liveOnThisType && liveSubHandlingData && IsLiveBackendAvailable();
                    category.Placeholder.Description = liveOnThisType ? PLACEHOLDER_SUBDATA_CREATE_LIVE : onlyAllowListRefuses ? PLACEHOLDER_SUBDATA_NOT_LIVE_ON_TYPE : PLACEHOLDER_SUBDATA_NOT_PRESENT;
                    resolved[i] = category.Placeholder;
                }
                if (!present) BackOutLiveCategoryMenus(context, category);
                if (resolved[i] != category.PhantomSubmenu) BackOutPhantomCategoryMenus(context, category);
            }
            for (int i = 0; i < categories.Length; i++)
            {
                SyncMenuSlot(rootMenu, ref currentSlotItems[i], resolved[i], insertPosition);
                if (resolved[i] != null) insertPosition++;
            }
            if (rootMenu.SelectedIndex < 0 && rootMenu.Items.Count > 0) rootMenu.SelectedIndex = rootMenu.Items.Count - 1;
        }

        // Children are backed out first so each lands on a parent that is itself about to close, ending at Edit Handling
        // Every menu a category owns is found through its row and its definition's facts, so no second category-to-menu mapping can go stale
        private void BackOutLiveCategoryMenus(EditorContext context, SubHandlingCategory row)
        {
            if (row.LiveFlagsMenu != null) BackOut(row.LiveFlagsMenu);
            if (row.Definition.HasAdvancedData)
            {
                BackOutAdvancedDataMenus(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live]);
                BackOutAdvancedDataMenus(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Pending]);
            }
            if (row.Definition.HasWeaponSlots)
            {
                WeaponSlotItems[] slots = context.Weapon.WeaponSlots;
                for (int i = 0; i < slots.Length; i++) BackOut(slots[i].Menu);
            }
            BackOut(row.LiveMenu);
        }

        private void BackOutPhantomCategoryMenus(EditorContext context, SubHandlingCategory row)
        {
            if (row.PhantomFlagsMenu != null) BackOut(row.PhantomFlagsMenu);
            if (row.Definition.HasAdvancedData) BackOutAdvancedDataMenus(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Phantom]);
            if (row.Definition.HasWeaponSlots)
            {
                PhantomWeaponSlotItems[] slots = context.Phantom.PhantomWeaponSlots;
                for (int i = 0; i < slots.Length; i++) BackOut(slots[i].Menu);
            }
            BackOut(row.PhantomMenu);
        }

        // Copying the Disabled-tier colors onto the Normal and Hovered tiers makes an item read as unavailable in every state, including while selected
        // Enabled stays True so Activated still fires on Enter, which a genuinely disabled item cannot do
        // The library paints a disabled checkbox's box with this color, white by default, which stays bright beside the row's dimmed title
        private static void DimCheckboxWhileDisabled(NativeCheckboxItem item) { item.Colors.BadgeRightDisabled = DIMMED_TEXT_COLOR; }

        private static void DimItemAsDisabled(NativeItem item)
        {
            item.Colors.TitleNormal = item.Colors.TitleDisabled;
            item.Colors.TitleHovered = item.Colors.TitleDisabled;
            item.Colors.AltTitleNormal = item.Colors.AltTitleDisabled;
            item.Colors.AltTitleHovered = item.Colors.AltTitleDisabled;
            item.UpdateColors();
        }

        // Shows only the weapon slot submenu entries the vehicle uses, in ascending slot order, diffing per slot because used slots are not necessarily contiguous
        // One tracked occupant per weapon slot (mirroring EditorContext.CurrentSubDataSlotItems) means only a slot whose used state changed touches the menu
        // Insertion anchors at anchorItem, because rootMenu's category-wide scalars always precede the submenu entries, so a swapped-in slot keeps ascending order
        private void SyncWeaponSlotVisibility(NativeMenu rootMenu, NativeItem[] currentSlotItems, WeaponSlotItems[] slots, NativeItem anchorItem, VehicleWeaponHash[] weaponHashes)
        {
            int insertPosition = rootMenu.Items.IndexOf(anchorItem) + 1;
            for (int i = 0; i < slots.Length; i++)
            {
                bool used = i < weaponHashes.Length && !IsUnsetWeaponHash(weaponHashes[i]);
                // A slot losing its row (weapon cleared) closes its own submenu if the player is standing inside it
                // Nothing else would back them out of a menu whose parent row is about to disappear
                if (!used && currentSlotItems[i] != null) BackOut(slots[i].Menu);
                SyncMenuSlot(rootMenu, ref currentSlotItems[i], used ? slots[i].SubmenuItem : null, insertPosition);
                if (used) insertPosition++;
            }
            if (rootMenu.Items.Count > 0 && rootMenu.SelectedIndex < 0) rootMenu.SelectedIndex = Math.Max(0, rootMenu.Items.Count - 1);
        }

        // The rocket boost rows sit directly above AIHandling in the base field order
        private void SyncRocketBoostVisibility(EditorContext context, bool hasRocketBoost)
        {
            int insertPosition = context.RootMenu.Items.IndexOf(context.AiHandlingItem);
            for (int i = 0; i < context.RocketBoostItems.Count; i++)
            {
                SyncMenuSlot(context.RootMenu, ref context.CurrentRocketBoostSlotItems[i], hasRocketBoost ? context.RocketBoostItems[i] : null, insertPosition);
                if (hasRocketBoost) insertPosition++;
            }
        }

        // The game reads a turret slot's pitch limits only on a model with FLAG_HAS_REAR_MOUNTED_TURRET
        // They are the last rows of the slot menu, so they are appended back in order
        private void SyncTurretPitchLimitVisibility(EditorContext context, bool hasRearMountedTurret)
        {
            WeaponSlotItems[] slots = context.Weapon.WeaponSlots;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].PitchLimitItems == null) continue;
                for (int p = 0; p < slots[i].PitchLimitItems.Length; p++) SyncMenuSlot(slots[i].Menu, ref slots[i].CurrentPitchLimitSlotItems[p], hasRearMountedTurret ? slots[i].PitchLimitItems[p] : null);
            }
        }
    }
}
