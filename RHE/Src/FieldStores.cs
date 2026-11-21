using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // One wiring function per value type serves every row, whether the live handling, an export-only element, or a pending AdvancedData element holds the value
        // A class of delegates built by the factory methods below, not an interface, so each closure reaches the script's own state without a reference back to it
        private class FieldStore<T> where T : struct
        {
            // The stored value, or null when there is nothing to read
            public Func<T?> Read;
            // Stores the value, and returns False when nothing was written
            public Func<T, bool> Write;
            // The follow-up work of a write that landed: The old value, the new value, and whether a held Left or Right produced it
            public Action<T, T, bool> Commit;
            // The live handling a step resolver and the menu width pass read, null for a store that edits an element
            public Func<HandlingData> Handling;
            // Re-runs the row's follow-up on the owner's vehicle with no write, null when the row has none
            // A typed value equal to the current one skips the setter, while a derived value the follow-up sets can drift from the stored one
            public Action Reapply;
        }

        // Marks the binding as the value to hold against drift
        private void AssertFloatBinding(FloatFieldBinding binding, float value)
        {
            if (!binding.Asserting) assertingFloatBindings.Add(binding);
            binding.Asserting = true;
            binding.AssertedValue = value;
        }

        private void AssertIntBinding(IntFieldBinding binding, int value)
        {
            if (!binding.Asserting) assertingIntBindings.Add(binding);
            binding.Asserting = true;
            binding.AssertedValue = value;
        }

        // The value the store actually holds when it differs from the request by more than conversion noise, else the request itself
        private static float ResolveWrittenFloat(float requested, float stored)
        {
            return Math.Abs(stored - requested) > DRIFT_EPSILON ? stored : requested;
        }

        // Registers a live row's binding, undo focus key, and raw offset or SpecialFlight build marks, and warns on a registry key collision
        // fieldKeyOverride tells apart fields whose title repeats across weapon slots and editor trees, which the category prefix alone cannot
        // Returns the field key, which the caller registers its fieldRegistry closure under
        private string RegisterLiveFieldBinding<TBinding>(NativeItem item, string category, FieldEntry entry, EditorContext owner, string fieldKeyOverride, TBinding binding, Dictionary<NativeItem, TBinding> bindingByItem, Dictionary<EditorContext, List<TBinding>> bindingsByOwner, Dictionary<string, TBinding> bindingByFieldKey)
        {
            if (entry.RawOffset) RawOffsetItem(item);
            if (entry.RequiresSpecialFlightFlagsBuild) specialFlightFlagItems.Add(item);
            bindingByItem[item] = binding;
            if (entry.VisualizedElements != HandlingVisualizationElements.Disabled) visualizedRowElements[item] = entry.VisualizedElements;
            GetOrAdd(bindingsByOwner, owner).Add(binding);
            string fieldKey = BuildFieldKey(category, fieldKeyOverride ?? item.Title);
            bindingByFieldKey[fieldKey] = binding;
            RegisterUndoFocusItem(fieldKey, item);
            if (fieldRegistry.ContainsKey(fieldKey)) logger.Log(Dbg.Level.Warn, Dbg.FieldRegistryKeyCollision(fieldKey));
            return fieldKey;
        }

        // The owner tree's vehicle and handling are resolved at event time, so a handler always edits its own tree's vehicle
        private FieldStore<T> BuildLiveFieldStore<T>(EditorContext owner, Func<HandlingData, T> getter, FieldSetter<T> setter, Action<T, T, bool> commit) where T : struct
        {
            return new FieldStore<T>
            {
                Read = () =>
                {
                    Vehicle vehicle;
                    HandlingData handling;
                    if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return null;
                    return getter(handling);
                },
                Write = value =>
                {
                    Vehicle vehicle;
                    HandlingData handling;
                    return ResolveContextVehicleHandling(owner, out vehicle, out handling) && setter(vehicle, handling, value);
                },
                Commit = commit,
                Handling = () =>
                {
                    Vehicle vehicle;
                    HandlingData handling;
                    return ResolveContextVehicleHandling(owner, out vehicle, out handling) ? handling : null;
                },
            };
        }

        private FieldStore<float> BuildLiveFloatStore(NativeDynamicItem<DisplayFloat> item, string category, FieldEntry entry, Func<HandlingData, float> getter, FieldSetter<float> setter, EditorContext owner, string fieldKeyOverride, string baseDescription)
        {
            var binding = new FloatFieldBinding { Item = item, Getter = getter, Setter = setter, Owner = owner, DescriptionPrefix = entry.DescriptionPrefix, BaseDescription = baseDescription };
            string fieldKey = RegisterLiveFieldBinding(item, category, entry, owner, fieldKeyOverride, binding, floatBindingByItem, floatFieldBindingsByOwner, floatBindingByFieldKey);
            fieldRegistry[fieldKey] = (vehicle, text) =>
            {
                float parsed;
                if (!ParseFiniteFloat(text, out parsed)) return ApplyResult.Rejected();
                HandlingData handling = vehicle.HandlingData;
                parsed = ApplyHardBounds(entry, parsed, 0);
                if (!setter(vehicle, handling, parsed)) return ApplyResult.CategoryAbsent();
                AssertFloatBinding(binding, ResolveWrittenFloat(parsed, getter(handling)));
                return ApplyResult.Applied(ResolveReplayFlags(entry.RefreshesVehicle, entry.RequiresRespawn));
            };
            return BuildLiveFieldStore(owner, getter, setter, (oldValue, newValue, held) =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return;
                AssertFloatBinding(binding, newValue);
                RefreshValueDescription(binding, newValue);
                string oldText = oldValue.ToString("R", CultureInfo.InvariantCulture);
                string newText = newValue.ToString("R", CultureInfo.InvariantCulture);
                HandlingKey handlingKey = ResolveHandlingKey(vehicle);
                if (held)
                {
                    if (entry.RefreshesVehicle) RequestSpawnRefresh(vehicle);
                    if (entry.RequiresRespawn) RequestCycleAutoRespawn(vehicle, fieldKey, oldText, newText);
                    RecordCycleUndo(handlingKey, fieldKey, oldText, newText);
                    return;
                }
                if (entry.RefreshesVehicle) RefreshSpawnCopiedFields(vehicle, false);
                if (entry.RequiresRespawn && Math.Abs(newValue - oldValue) > DRIFT_EPSILON) RequestAutoRespawn(vehicle, fieldKey);
                PushUndo(handlingKey, fieldKey, oldText, newText);
            });
        }

        // An int reads back exactly, so its registry closure needs no read back
        private FieldStore<int> BuildLiveIntStore(NativeDynamicItem<int> item, string category, FieldEntry entry, Func<HandlingData, int> getter, FieldSetter<int> setter, EditorContext owner, string fieldKeyOverride)
        {
            var binding = new IntFieldBinding { Item = item, Getter = getter, Setter = setter, Owner = owner };
            string fieldKey = RegisterLiveFieldBinding(item, category, entry, owner, fieldKeyOverride, binding, intBindingByItem, intFieldBindingsByOwner, intBindingByFieldKey);
            fieldRegistry[fieldKey] = (vehicle, text) =>
            {
                int parsed;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return ApplyResult.Rejected();
                parsed = ApplyHardBounds(entry, parsed);
                if (!setter(vehicle, vehicle.HandlingData, parsed)) return ApplyResult.CategoryAbsent();
                AssertIntBinding(binding, parsed);
                return ApplyResult.Applied(ResolveReplayFlags(entry.RefreshesVehicle, entry.RequiresRespawn));
            };
            return BuildLiveFieldStore(owner, getter, setter, (oldValue, newValue, held) =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return;
                AssertIntBinding(binding, newValue);
                string oldText = oldValue.ToString(CultureInfo.InvariantCulture);
                string newText = newValue.ToString(CultureInfo.InvariantCulture);
                HandlingKey handlingKey = ResolveHandlingKey(vehicle);
                if (held)
                {
                    if (entry.RefreshesVehicle) RequestSpawnRefresh(vehicle);
                    if (entry.RequiresRespawn) RequestCycleAutoRespawn(vehicle, fieldKey, oldText, newText);
                    RecordCycleUndo(handlingKey, fieldKey, oldText, newText);
                    return;
                }
                if (entry.RefreshesVehicle) RefreshSpawnCopiedFields(vehicle, false);
                if (entry.RequiresRespawn && newValue != oldValue) RequestAutoRespawn(vehicle, fieldKey);
                PushUndo(handlingKey, fieldKey, oldText, newText);
            });
        }

        // A held step feeds the one pending batch, and a typed value is its own undo entry
        private void CommitStructuralEdit(EditorContext owner, ValueEdit template, string oldValue, string newValue, bool held)
        {
            ValueEdit edit = template.WithValues(oldValue, newValue);
            if (held) RecordStructuralCycleEdit(edit, owner);
            else PushStructuralEditForContext(owner, edit);
        }

        private static string ReadChildAttribute(XElement parent, string childName, string attribute)
        {
            XElement child = parent != null ? parent.Element(childName) : null;
            XAttribute found = child != null ? child.Attribute(attribute) : null;
            return found != null ? found.Value : null;
        }

        private static bool WriteChildAttribute(XElement parent, string childName, string attribute, string text)
        {
            XElement child = parent != null ? parent.Element(childName) : null;
            if (child == null) return false;
            child.SetAttributeValue(attribute, text);
            return true;
        }

        // resolveParent returns the parent element at event time, childName names the child under it, and attribute is value or the axis of a vector component
        // template carries the identity of the row's undo record, and its focus key is what an undo uses to find the row again
        // The menu's sync action shows the stored value, or 0 when there is nothing to read
        private FieldStore<float> BuildElementFloatStore(NativeMenu menu, NativeDynamicItem<DisplayFloat> item, EditorContext owner, Func<XElement> resolveParent, string childName, string attribute, ValueEdit template)
        {
            RegisterUndoFocusItem(template.FocusKey, item);
            var store = new FieldStore<float>
            {
                Read = () =>
                {
                    string text = ReadChildAttribute(resolveParent(), childName, attribute);
                    float parsed;
                    return text != null && ParseFiniteFloat(text, out parsed) ? (float?)parsed : null;
                },
                Write = value => WriteChildAttribute(resolveParent(), childName, attribute, FormatSaveFloat(value)),
                Commit = (oldValue, newValue, held) => CommitStructuralEdit(owner, template, oldValue.ToString("R", CultureInfo.InvariantCulture), newValue.ToString("R", CultureInfo.InvariantCulture), held),
                Handling = () => null,
            };
            AddPhantomSyncAction(menu, () =>
            {
                float? shown = store.Read();
                item.SelectedItem = shown.HasValue ? shown.Value : 0.0f;
            });
            return store;
        }

        private FieldStore<int> BuildElementIntStore(NativeMenu menu, NativeDynamicItem<int> item, EditorContext owner, Func<XElement> resolveParent, string childName, string attribute, ValueEdit template)
        {
            RegisterUndoFocusItem(template.FocusKey, item);
            var store = new FieldStore<int>
            {
                Read = () =>
                {
                    string text = ReadChildAttribute(resolveParent(), childName, attribute);
                    int parsed;
                    return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? (int?)parsed : null;
                },
                Write = value => WriteChildAttribute(resolveParent(), childName, attribute, value.ToString(CultureInfo.InvariantCulture)),
                Commit = (oldValue, newValue, held) => CommitStructuralEdit(owner, template, oldValue.ToString(CultureInfo.InvariantCulture), newValue.ToString(CultureInfo.InvariantCulture), held),
                Handling = () => null,
            };
            AddPhantomSyncAction(menu, () =>
            {
                int? shown = store.Read();
                item.SelectedItem = shown.HasValue ? shown.Value : 0;
            });
            return store;
        }
    }
}
