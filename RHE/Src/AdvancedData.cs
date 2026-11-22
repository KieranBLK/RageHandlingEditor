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
        // The 3 backing stores of AdvancedData: The real array in game memory, the pending block of a live Car with no entries, and the phantom block of an absent Car
        // The numbers index a tree's AdvancedDataState.Lists and the store table
        private enum AdvancedDataMenuKind { Live, Pending, Phantom }

        // Export-only AdvancedData block of a present Car category whose real entry count is 0, keyed by handling
        // Distinct from pendingSubDataElements, which covers a missing whole SubHandlingData category
        // Held directly instead of inside a Car element, since Car is live here
        // Its shape is a phantom AdvancedData element: An "AdvancedData" element holding 1 or more "Item" children
        private Dictionary<HandlingKey, XElement> pendingAdvancedData = new Dictionary<HandlingKey, XElement>();

        // The entry at index of a block, or null when the block is absent or the index is out of range
        private static XElement GetAdvancedDataItem(XElement block, int index)
        {
            if (block == null) return null;
            XElement[] items = ListItemElements(block);
            return index >= 0 && index < items.Length ? items[index] : null;
        }

        private static string EncodeAdvancedDataEntry(AdvancedDataEntry entry)
        {
            return entry.Slot.ToString(CultureInfo.InvariantCulture) + "," + entry.Index.ToString(CultureInfo.InvariantCulture) + "," + entry.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool DecodeAdvancedDataEntry(string text, out AdvancedDataEntry entry)
        {
            entry = new AdvancedDataEntry();
            string[] parts = text.Split(',');
            return parts.Length == 3 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.Slot) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.Index) && ParseFiniteFloat(parts[2], out entry.Value);
        }

        // One store per backing, so every operation is written once and each store supplies only how it reads and writes its own block
        // Every member takes the script first, like StructuralEdit.Apply, so no store holds a back reference
        // Each member takes the handling's key and its live data, and only the Live store reads the live data
        // A restore reads an empty stored text as the default, and a stored text that fails to decode as not applied with its reason
        private abstract class AdvancedDataStore
        {
            public readonly AdvancedDataMenuKind Kind;
            // The persisted undo kinds of this store's records. The field kinds are null for the Live store, whose rows are ordinary live fields
            public readonly string BlockCreateKind;
            public readonly string BlockDeleteKind;
            public readonly string AddSlotKind;
            public readonly string EntryDeleteKind;
            public readonly string IntFieldKind;
            public readonly string ValueFieldKind;
            // The export-only category that holds the block, or null for the 2 stores that sit on the live Car category
            public readonly string PhantomCategory;
            // The Car menu whose AdvancedData slot this store's list occupies: The live Car's for Live and Pending, the phantom Car's for Phantom
            public readonly bool UsesLiveCarMenu;
            // Said when a block delete lands
            public readonly string BlockDeleteToast;
            // Said when an entry or field operation finds no block
            public readonly string MissingBlockReason;

            protected AdvancedDataStore(AdvancedDataMenuKind kind, string blockCreateKind, string blockDeleteKind, string addSlotKind, string entryDeleteKind, string intFieldKind, string valueFieldKind, string phantomCategory, bool usesLiveCarMenu, string blockDeleteToast, string missingBlockReason)
            {
                Kind = kind;
                BlockCreateKind = blockCreateKind;
                BlockDeleteKind = blockDeleteKind;
                AddSlotKind = addSlotKind;
                EntryDeleteKind = entryDeleteKind;
                IntFieldKind = intFieldKind;
                ValueFieldKind = valueFieldKind;
                PhantomCategory = phantomCategory;
                UsesLiveCarMenu = usesLiveCarMenu;
                BlockDeleteToast = blockDeleteToast;
                MissingBlockReason = missingBlockReason;
            }

            public bool OwnsKind(string kind)
            {
                return kind == BlockCreateKind || kind == BlockDeleteKind || kind == AddSlotKind || kind == EntryDeleteKind || (IntFieldKind != null && (kind == IntFieldKind || kind == ValueFieldKind));
            }

            public AdvancedDataMenus MenusOf(EditorContext context) { return context.AdvancedData.Lists[(int)Kind]; }
            public NativeItem ResolvePlaceholder(EditorContext context) { return UsesLiveCarMenu ? context.AdvancedData.CarPlaceholderItem : context.AdvancedData.PhantomPlaceholderItem; }
            public NativeMenu ResolveCarMenu(RageHandlingEditor script, EditorContext context) { return UsesLiveCarMenu ? script.GetLiveMenuForCategory(context, CATEGORY_CAR) : script.GetPhantomMenuForCategory(context, CATEGORY_CAR); }
            // The category leads a phantom store's names, since tags repeat across categories
            public string LabelScope { get { return PhantomCategory != null ? PhantomCategory + "." : ""; } }
            public string Describe(string kind) { return PhantomCategory != null ? kind + " (" + PhantomCategory + ")" : kind; }

            // False with the reason when the store cannot be edited now: A phantom Car that is gone, or a build with no AdvancedData writes
            public abstract bool CheckUsable(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason);
            public abstract bool HasBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling);
            public abstract int Count(RageHandlingEditor script, HandlingKey key, HandlingData handling);
            // Captures the block as stored text immediately before it is detached, so the opposite direction restores exactly what was taken away
            public abstract bool CaptureBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string encoded, out string reason);
            // Detaching a block that is absent succeeds with nothing to do
            public abstract bool DetachBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason);
            // Attaching while a block is present reports not applied in every store
            public abstract bool AttachBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, string encoded, out string reason);
            public abstract bool InsertEntry(RageHandlingEditor script, HandlingKey key, HandlingData handling, int index, string encoded, out string reason);
            public abstract bool RemoveEntry(RageHandlingEditor script, HandlingKey key, HandlingData handling, int index, out string encoded, out string reason);
            // Refreshes the tree editing the handling after any change, and does nothing when no tree is showing this store's data
            public abstract void Refresh(RageHandlingEditor script, HandlingKey key);
            // How many entry rows the tree's list should show, read the way the tree's own menus read it
            public abstract int CountForTree(RageHandlingEditor script, EditorContext context);
            public abstract bool IsAddSlotOffered(RageHandlingEditor script, EditorContext context);
            public abstract void PressAddSlot(RageHandlingEditor script, EditorContext context);

            // Deleting the only entry deletes the block, so the Car menu returns to its create placeholder
            public void PressEntryDelete(RageHandlingEditor script, EditorContext context, int entryIndex)
            {
                Vehicle vehicle = context.TargetVehicle;
                if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid) return;
                HandlingData handling = vehicle.HandlingData;
                HandlingKey key = ResolveHandlingKey(vehicle);
                int count = Count(script, key, handling);
                if (entryIndex < 0 || entryIndex >= count) return;
                if (count == 1)
                {
                    PressBlockDelete(script, context);
                    return;
                }
                string removed, reason;
                if (!RemoveEntry(script, key, handling, entryIndex, out removed, out reason))
                {
                    script.notifications.PostNotificationOnce(NOTIFICATION_KEY_ADVANCEDDATA_DELETE_ERROR, NOTIF_ADVANCEDDATA_ENTRY_DELETE_FAILED);
                    return;
                }
                Refresh(script, key);
                script.notifications.Notify(NOTIFICATION_KEY_ADVANCEDDATA_ENTRY_DELETE, string.Format(NOTIF_ADVANCEDDATA_ENTRY_DELETED, entryIndex + 1));
                script.PushStructuralEdit(new AdvancedDataEntryEdit(this, EntryDeleteKind, entryIndex, removed) { Handling = key });
            }

            public void PressBlockDelete(RageHandlingEditor script, EditorContext context)
            {
                Vehicle vehicle = context.TargetVehicle;
                if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid) return;
                HandlingData handling = vehicle.HandlingData;
                HandlingKey key = ResolveHandlingKey(vehicle);
                if (!HasBlock(script, key, handling)) return;
                string captured, reason;
                if (!CaptureBlock(script, key, handling, out captured, out reason) || !DetachBlock(script, key, handling, out reason))
                {
                    script.notifications.PostNotificationOnce(NOTIFICATION_KEY_ADVANCEDDATA_DELETE_ERROR, NOTIF_ADVANCEDDATA_DELETE_FAILED);
                    return;
                }
                script.BackOutAdvancedDataMenus(MenusOf(context));
                Refresh(script, key);
                script.notifications.Notify(NOTIFICATION_KEY_ADVANCEDDATA_ENTRY_DELETE, BlockDeleteToast);
                script.PushStructuralEdit(new AdvancedDataBlockEdit(this, BlockDeleteKind, captured) { Handling = key });
            }
        }

        // The pending and phantom stores keep their block as an AdvancedData element, so every operation is written once over the element
        // A subclass supplies only where the block lives and how it is attached and detached
        private abstract class XmlAdvancedDataStore : AdvancedDataStore
        {
            protected XmlAdvancedDataStore(AdvancedDataMenuKind kind, string blockCreateKind, string blockDeleteKind, string addSlotKind, string entryDeleteKind, string intFieldKind, string valueFieldKind, string phantomCategory, bool usesLiveCarMenu, string missingBlockReason)
                : base(kind, blockCreateKind, blockDeleteKind, addSlotKind, entryDeleteKind, intFieldKind, valueFieldKind, phantomCategory, usesLiveCarMenu, NOTIF_ADVANCEDDATA_REMOVED, missingBlockReason) { }

            // The block of a handling, null while absent
            public abstract XElement ResolveBlock(RageHandlingEditor script, HandlingKey key);
            // The block the tree's own menus edit, read at event time
            public abstract XElement ResolveBlockOfTree(RageHandlingEditor script, EditorContext context);
            protected abstract bool Attach(RageHandlingEditor script, HandlingKey key, XElement block, out string reason);
            protected abstract bool Detach(RageHandlingEditor script, HandlingKey key, out string reason);

            public XElement ResolveSlotOfTree(RageHandlingEditor script, EditorContext context, int slotIndex) { return GetAdvancedDataItem(ResolveBlockOfTree(script, context), slotIndex); }
            public override bool HasBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling) { return ResolveBlock(script, key) != null; }
            public override int Count(RageHandlingEditor script, HandlingKey key, HandlingData handling) { return CountItems(ResolveBlock(script, key)); }
            public override int CountForTree(RageHandlingEditor script, EditorContext context) { return Math.Min(CountItems(ResolveBlockOfTree(script, context)), ADVANCEDDATA_COUNT_SANITY_CEILING); }
            public override bool IsAddSlotOffered(RageHandlingEditor script, EditorContext context) { return true; }
            private static int CountItems(XElement block) { return block != null ? CountItemElements(block) : 0; }

            public override bool CaptureBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string encoded, out string reason)
            {
                XElement block = ResolveBlock(script, key);
                encoded = block != null ? EncodeElement(block) : null;
                reason = block != null ? null : MissingBlockReason;
                return block != null;
            }

            public override bool DetachBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason) { return Detach(script, key, out reason); }

            public override bool AttachBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, string encoded, out string reason)
            {
                if (!CheckUsable(script, key, handling, out reason)) return false;
                if (HasBlock(script, key, handling))
                {
                    reason = "AdvancedData already exists";
                    return false;
                }
                XElement block = string.IsNullOrEmpty(encoded) ? BuildDefaultAdvancedDataElement() : script.DecodeElement(encoded);
                if (block == null)
                {
                    reason = "stored AdvancedData block failed to decode";
                    return false;
                }
                return Attach(script, key, block, out reason);
            }

            public override bool InsertEntry(RageHandlingEditor script, HandlingKey key, HandlingData handling, int index, string encoded, out string reason)
            {
                XElement block = ResolveBlock(script, key);
                if (block == null)
                {
                    reason = MissingBlockReason;
                    return false;
                }
                XElement[] items = ListItemElements(block);
                if (items.Length >= ADVANCEDDATA_COUNT_SANITY_CEILING)
                {
                    reason = "AdvancedData slot capacity full (" + ADVANCEDDATA_COUNT_SANITY_CEILING + "/" + ADVANCEDDATA_COUNT_SANITY_CEILING + ")";
                    return false;
                }
                if (index < 0 || index > items.Length)
                {
                    reason = "entry index out of range";
                    return false;
                }
                XElement entry = string.IsNullOrEmpty(encoded) ? BuildDefaultAdvancedDataSlot() : script.DecodeElement(encoded);
                if (entry == null)
                {
                    reason = "stored entry failed to decode";
                    return false;
                }
                if (index == items.Length) block.Add(entry);
                else items[index].AddBeforeSelf(entry);
                reason = null;
                return true;
            }

            public override bool RemoveEntry(RageHandlingEditor script, HandlingKey key, HandlingData handling, int index, out string encoded, out string reason)
            {
                encoded = null;
                XElement block = ResolveBlock(script, key);
                if (block == null)
                {
                    reason = MissingBlockReason;
                    return false;
                }
                XElement[] items = ListItemElements(block);
                if (index < 0 || index >= items.Length)
                {
                    reason = "entry index out of range";
                    return false;
                }
                encoded = EncodeElement(items[index]);
                items[index].Remove();
                reason = null;
                return true;
            }

            public override void PressAddSlot(RageHandlingEditor script, EditorContext context)
            {
                XElement block = ResolveBlockOfTree(script, context);
                if (block == null) return;
                int count = CountItems(block);
                if (count >= ADVANCEDDATA_COUNT_SANITY_CEILING) return;
                block.Add(BuildDefaultAdvancedDataSlot());
                AdvancedDataMenus lists = MenusOf(context);
                script.SyncAdvancedDataVisibility(context, Kind);
                // Keep focus on Add Slot itself instead of letting it land on the newly listed entry row above it
                int addSlotIndex = lists.ListMenu.Items.IndexOf(lists.AddSlotItem);
                if (addSlotIndex >= 0) lists.ListMenu.SelectedIndex = addSlotIndex;
                script.PushStructuralEditForContext(context, new AdvancedDataEntryEdit(this, AddSlotKind, count, null));
            }

            // The placeholder is swapped for the real list entry at once, so pressing Enter shows the list without closing and reopening the Car menu
            public void PressCreate(RageHandlingEditor script, EditorContext context)
            {
                Vehicle vehicle = context.TargetVehicle;
                if (Entity.IsNullOrNotExisting(vehicle)) return;
                HandlingKey key = ResolveHandlingKey(vehicle);
                string reason;
                if (HasBlock(script, key, null) || !AttachBlock(script, key, null, null, out reason)) return;
                Refresh(script, key);
                script.PushStructuralEdit(new AdvancedDataBlockEdit(this, BlockCreateKind, null) { Handling = key });
            }
        }

        private sealed class PendingAdvancedDataStore : XmlAdvancedDataStore
        {
            public PendingAdvancedDataStore() : base(AdvancedDataMenuKind.Pending, StructuralKind.PendingAdvancedCreate, StructuralKind.PendingAdvancedDelete, StructuralKind.PendingAdvancedAddSlot, StructuralKind.PendingAdvancedEntryDelete, StructuralKind.PendingAdvancedInt, StructuralKind.PendingAdvancedValue, null, true, "pending AdvancedData block does not exist") { }
            public override bool CheckUsable(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason)
            {
                reason = null;
                return true;
            }

            public override XElement ResolveBlock(RageHandlingEditor script, HandlingKey key)
            {
                XElement block;
                return script.pendingAdvancedData.TryGetValue(key, out block) ? block : null;
            }

            public override XElement ResolveBlockOfTree(RageHandlingEditor script, EditorContext context)
            {
                Vehicle vehicle = context.TargetVehicle;
                return Entity.IsNullOrNotExisting(vehicle) ? null : ResolveBlock(script, ResolveHandlingKey(vehicle));
            }

            protected override bool Attach(RageHandlingEditor script, HandlingKey key, XElement block, out string reason)
            {
                script.pendingAdvancedData[key] = block;
                reason = null;
                return true;
            }

            protected override bool Detach(RageHandlingEditor script, HandlingKey key, out string reason)
            {
                script.pendingAdvancedData.Remove(key);
                reason = null;
                return true;
            }

            public override void Refresh(RageHandlingEditor script, HandlingKey key) { script.RefreshPendingAdvancedData(key); }
        }

        private sealed class PhantomAdvancedDataStore : XmlAdvancedDataStore
        {
            public PhantomAdvancedDataStore() : base(AdvancedDataMenuKind.Phantom, StructuralKind.PhantomAdvancedCreate, StructuralKind.PhantomAdvancedDelete, StructuralKind.PhantomAdvancedAddSlot, StructuralKind.PhantomAdvancedEntryDelete, StructuralKind.PhantomAdvancedInt, StructuralKind.PhantomAdvancedValue, CATEGORY_CAR, false, "phantom category has no AdvancedData block") { }
            public override bool CheckUsable(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason)
            {
                reason = script.ResolvePhantomElement(key, CATEGORY_CAR) != null ? null : "phantom category does not exist";
                return reason == null;
            }

            public override XElement ResolveBlock(RageHandlingEditor script, HandlingKey key)
            {
                XElement carElement = script.ResolvePhantomElement(key, CATEGORY_CAR);
                return carElement != null ? carElement.Element(ADVANCED_DATA_TAG) : null;
            }

            public override XElement ResolveBlockOfTree(RageHandlingEditor script, EditorContext context)
            {
                XElement carElement = context.Phantom.CurrentPhantomElement;
                return carElement != null ? carElement.Element(ADVANCED_DATA_TAG) : null;
            }

            protected override bool Attach(RageHandlingEditor script, HandlingKey key, XElement block, out string reason)
            {
                XElement carElement = script.ResolvePhantomElement(key, CATEGORY_CAR);
                reason = carElement != null ? null : "phantom category does not exist";
                if (carElement != null) carElement.Add(block);
                return carElement != null;
            }

            protected override bool Detach(RageHandlingEditor script, HandlingKey key, out string reason)
            {
                XElement carElement = script.ResolvePhantomElement(key, CATEGORY_CAR);
                reason = carElement != null ? null : "phantom category does not exist";
                if (carElement == null) return false;
                XElement block = carElement.Element(ADVANCED_DATA_TAG);
                if (block != null) block.Remove();
                return true;
            }

            public override void Refresh(RageHandlingEditor script, HandlingKey key) { script.RefreshPhantomAdvancedData(key); }
        }

        // The real array in game memory, edited in place: A delete shifts every later entry down one and lowers the count
        // A restore raises the count within the array's capacity and shifts them back up, so nothing is ever allocated during a replay
        // Each shift releases the entry fields' drift reassertion, since every binding is fixed to one index and would otherwise write old values into moved entries
        // Its stored text is "slot,index,value", and a block is every entry joined with semicolons
        private sealed class LiveAdvancedDataStore : AdvancedDataStore
        {
            public LiveAdvancedDataStore() : base(AdvancedDataMenuKind.Live, StructuralKind.LiveAdvancedCreate, StructuralKind.LiveAdvancedDelete, StructuralKind.LiveAdvancedAddSlot, StructuralKind.LiveAdvancedEntryDelete, null, null, null, true, NOTIF_ADVANCEDDATA_DELETED, "the vehicle has no AdvancedData") { }
            public override bool CheckUsable(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason)
            {
                reason = null;
                if (!RawMemory.RawOffsetsSupported) reason = LIVE_ADVANCED_DATA_UNSUPPORTED_REASON;
                else if (handling == null || handling.CarHandlingData == null) reason = "vehicle has no car handling data";
                return reason == null;
            }

            public override bool HasBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling) { return Count(script, key, handling) > 0; }
            public override int Count(RageHandlingEditor script, HandlingKey key, HandlingData handling) { return handling != null && handling.CarHandlingData != null ? RawMemory.GetAdvancedDataCount(handling) : 0; }

            public override int CountForTree(RageHandlingEditor script, EditorContext context)
            {
                Vehicle vehicle = context.TargetVehicle;
                return Entity.IsNullOrNotExisting(vehicle) ? 0 : Math.Min(Count(script, default(HandlingKey), vehicle.HandlingData), ADVANCEDDATA_COUNT_SANITY_CEILING);
            }

            public override bool IsAddSlotOffered(RageHandlingEditor script, EditorContext context)
            {
                Vehicle vehicle = context.TargetVehicle;
                return !Entity.IsNullOrNotExisting(vehicle) && script.IsLiveAdvancedDataGrowthOffered(vehicle.HandlingData);
            }

            // Returns every entry as "slot,index,value" joined by a semicolon, or fails when any entry does not read
            public override bool CaptureBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string encoded, out string reason)
            {
                encoded = null;
                reason = "entry read failed";
                int count = Count(script, key, handling);
                var parts = new string[count];
                for (int i = 0; i < count; i++)
                {
                    AdvancedDataEntry entry;
                    if (!RawMemory.ReadAdvancedDataEntry(handling, i, out entry)) return false;
                    parts[i] = EncodeAdvancedDataEntry(entry);
                }
                encoded = string.Join(";", parts);
                reason = null;
                return true;
            }

            public override bool DetachBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, out string reason)
            {
                reason = null;
                if (!HasBlock(script, key, handling)) return true;
                script.ReleaseAdvancedDataAssertions();
                if (RawMemory.SetAdvancedDataCount(handling, 0)) return true;
                reason = "entry count write failed";
                return false;
            }

            // Only restores onto an empty array, and the entries must fit the array's own capacity
            public override bool AttachBlock(RageHandlingEditor script, HandlingKey key, HandlingData handling, string encoded, out string reason)
            {
                if (!CheckUsable(script, key, handling, out reason)) return false;
                if (HasBlock(script, key, handling))
                {
                    reason = "AdvancedData already exists";
                    return false;
                }
                AdvancedDataEntry[] entries;
                if (string.IsNullOrEmpty(encoded)) entries = new[] { ParseAdvancedDataEntry(BuildDefaultAdvancedDataSlot()) };
                else
                {
                    string[] parts = encoded.Split(';');
                    entries = new AdvancedDataEntry[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (DecodeAdvancedDataEntry(parts[i], out entries[i])) continue;
                        reason = "stored entry data malformed";
                        return false;
                    }
                }
                if (entries.Length > RawMemory.GetAdvancedDataCapacity(handling))
                {
                    reason = "the entries no longer fit the vehicle's AdvancedData capacity";
                    return false;
                }
                script.ReleaseAdvancedDataAssertions();
                if (!RawMemory.SetAdvancedDataCount(handling, entries.Length))
                {
                    reason = "entry count write failed";
                    return false;
                }
                for (int i = 0; i < entries.Length; i++)
                {
                    if (RawMemory.WriteAdvancedDataEntry(handling, i, entries[i])) continue;
                    reason = "entry write failed";
                    return false;
                }
                return true;
            }

            public override bool InsertEntry(RageHandlingEditor script, HandlingKey key, HandlingData handling, int index, string encoded, out string reason)
            {
                if (!CheckUsable(script, key, handling, out reason)) return false;
                AdvancedDataEntry entry;
                if (string.IsNullOrEmpty(encoded)) entry = ParseAdvancedDataEntry(BuildDefaultAdvancedDataSlot());
                else if (!DecodeAdvancedDataEntry(encoded, out entry))
                {
                    reason = "stored entry data malformed";
                    return false;
                }
                if (RawMemory.GetAdvancedDataCount(handling) >= RawMemory.GetAdvancedDataCapacity(handling))
                {
                    reason = "the vehicle's AdvancedData capacity is full";
                    return false;
                }
                if (!InsertIntoArray(script, handling, index, entry))
                {
                    reason = "entry index out of range";
                    return false;
                }
                return true;
            }

            public override bool RemoveEntry(RageHandlingEditor script, HandlingKey key, HandlingData handling, int index, out string encoded, out string reason)
            {
                encoded = null;
                if (!CheckUsable(script, key, handling, out reason)) return false;
                AdvancedDataEntry removed;
                if (!RawMemory.ReadAdvancedDataEntry(handling, index, out removed))
                {
                    reason = "entry index out of range";
                    return false;
                }
                encoded = EncodeAdvancedDataEntry(removed);
                if (RemoveFromArray(script, handling, index)) return true;
                reason = "entry removal failed";
                return false;
            }

            public override void Refresh(RageHandlingEditor script, HandlingKey key) { script.RefreshForHandling(key); }

            // Within the array's spare capacity the entry is written at once, and at capacity the plugin grows the array by one first
            public override void PressAddSlot(RageHandlingEditor script, EditorContext context)
            {
                Vehicle vehicle = context.TargetVehicle;
                if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid) return;
                HandlingData handling = vehicle.HandlingData;
                HandlingKey key = ResolveHandlingKey(vehicle);
                if (script.HasPendingLiveRequest(key)) return;
                int count = RawMemory.GetAdvancedDataCount(handling);
                if (count >= ADVANCEDDATA_COUNT_SANITY_CEILING) return;
                var newEntry = new List<AdvancedDataEntry> { ParseAdvancedDataEntry(BuildDefaultAdvancedDataSlot()) };
                if (count >= RawMemory.GetAdvancedDataCapacity(handling))
                {
                    string growthRefusal;
                    if (!script.BeginAdvancedDataGrowth(context, vehicle, handling, count + 1, LiveRequestOrigin.Player, count, newEntry, out growthRefusal)) script.notifications.Notify(NOTIFICATION_KEY_LIVE_ADVANCEDDATA, string.Format(NOTIF_LIVE_ADVANCEDDATA_FAILED, growthRefusal));
                    return;
                }
                int writtenFrom;
                if (!script.WriteAdvancedDataEntries(handling, count, newEntry, out writtenFrom))
                {
                    script.notifications.Notify(NOTIFICATION_KEY_LIVE_ADVANCEDDATA, string.Format(NOTIF_LIVE_ADVANCEDDATA_FAILED, REASON_VALUES_NOT_WRITTEN));
                    return;
                }
                script.MarkAutosavePending(key);
                script.PushStructuralEdit(new AdvancedDataEntryEdit(this, AddSlotKind, count, null) { Handling = key });
                script.SyncEditorContext(context);
            }

            // Reads every entry it will move before writing anything, so a failed read leaves the array exactly as it was
            // A failed write puts the captured entries back before reporting the failure
            private static bool RemoveFromArray(RageHandlingEditor script, HandlingData handling, int index)
            {
                int count = RawMemory.GetAdvancedDataCount(handling);
                if (index < 0 || index >= count) return false;
                var original = new AdvancedDataEntry[count - index];
                for (int i = 0; i < original.Length; i++) { if (!RawMemory.ReadAdvancedDataEntry(handling, index + i, out original[i])) return false; }
                script.ReleaseAdvancedDataAssertions();
                for (int i = 1; i < original.Length; i++)
                {
                    if (RawMemory.WriteAdvancedDataEntry(handling, index + i - 1, original[i])) continue;
                    RestoreEntries(handling, index, original);
                    return false;
                }
                if (RawMemory.SetAdvancedDataCount(handling, count - 1)) return true;
                RestoreEntries(handling, index, original);
                return false;
            }

            private static bool InsertIntoArray(RageHandlingEditor script, HandlingData handling, int index, AdvancedDataEntry entry)
            {
                int count = RawMemory.GetAdvancedDataCount(handling);
                if (index < 0 || index > count || count >= RawMemory.GetAdvancedDataCapacity(handling)) return false;
                var original = new AdvancedDataEntry[count - index];
                for (int i = 0; i < original.Length; i++) { if (!RawMemory.ReadAdvancedDataEntry(handling, index + i, out original[i])) return false; }
                script.ReleaseAdvancedDataAssertions();
                if (!RawMemory.SetAdvancedDataCount(handling, count + 1)) return false;
                bool written = true;
                for (int i = 0; i < original.Length && written; i++) written = RawMemory.WriteAdvancedDataEntry(handling, index + 1 + i, original[i]);
                if (written) written = RawMemory.WriteAdvancedDataEntry(handling, index, entry);
                if (written) return true;
                RestoreEntries(handling, index, original);
                RawMemory.SetAdvancedDataCount(handling, count);
                return false;
            }

            // Each write is independent, so one more failure does not stop the rest
            private static void RestoreEntries(HandlingData handling, int first, AdvancedDataEntry[] original)
            {
                for (int i = 0; i < original.Length; i++) RawMemory.WriteAdvancedDataEntry(handling, first + i, original[i]);
            }
        }

        // One store per kind, indexed by AdvancedDataMenuKind
        private static readonly AdvancedDataStore[] AdvancedDataStores = { new LiveAdvancedDataStore(), new PendingAdvancedDataStore(), new PhantomAdvancedDataStore() };

        private static AdvancedDataStore ResolveAdvancedDataStoreOfKind(string kind)
        {
            for (int i = 0; i < AdvancedDataStores.Length; i++) { if (AdvancedDataStores[i].OwnsKind(kind)) return AdvancedDataStores[i]; }
            return null;
        }

        private static XmlAdvancedDataStore ResolveXmlAdvancedDataStore(AdvancedDataMenuKind kind) { return (XmlAdvancedDataStore)AdvancedDataStores[(int)kind]; }

        // Every apply returns False when the target no longer exists and always sets reason to why
        // A block attached while one is present is not applied, so a replay that finds the state already there says so
        private bool ApplyAdvancedDataBlockEdit(AdvancedDataBlockEdit edit, bool redo, HandlingData handling, out string reason)
        {
            AdvancedDataStore store = edit.Store;
            if (!store.CheckUsable(this, edit.Handling, handling, out reason)) return false;
            if (edit.IsCreate == redo)
            {
                if (!store.AttachBlock(this, edit.Handling, handling, edit.EncodedBlock, out reason)) return false;
            }
            else if (store.HasBlock(this, edit.Handling, handling))
            {
                // Captured immediately before detaching, not at push time, since slot edits made after the block was created belong to it
                string captured;
                if (!store.CaptureBlock(this, edit.Handling, handling, out captured, out reason)) return false;
                edit.EncodedBlock = captured;
                EditorContext ownContext = ResolveEditorContextOfHandling(edit.Handling);
                if (ownContext != null) BackOutAdvancedDataMenus(store.MenusOf(ownContext));
                if (!store.DetachBlock(this, edit.Handling, handling, out reason)) return false;
            }
            store.Refresh(this, edit.Handling);
            return true;
        }

        private bool ApplyAdvancedDataEntryEdit(AdvancedDataEntryEdit edit, bool redo, HandlingData handling, out string reason)
        {
            AdvancedDataStore store = edit.Store;
            if (!store.CheckUsable(this, edit.Handling, handling, out reason)) return false;
            if (edit.IsAddSlot == redo)
            {
                // History replays in strict reverse order, so by the time this entry is replayed every later add or delete has already been reverted
                // The index again names the slot this added or removed
                if (!store.InsertEntry(this, edit.Handling, handling, edit.EntryIndex, edit.EncodedEntry, out reason)) return false;
            }
            else
            {
                string captured;
                if (!store.RemoveEntry(this, edit.Handling, handling, edit.EntryIndex, out captured, out reason)) return false;
                edit.EncodedEntry = captured;
            }
            store.Refresh(this, edit.Handling);
            return true;
        }

        private bool ApplyAdvancedDataFieldEdit(AdvancedDataFieldEdit edit, bool redo, out string reason)
        {
            XmlAdvancedDataStore store = edit.Store;
            if (!store.CheckUsable(this, edit.Handling, null, out reason)) return false;
            XElement block = store.ResolveBlock(this, edit.Handling);
            if (block == null)
            {
                reason = store.MissingBlockReason;
                return false;
            }
            XElement slot = GetAdvancedDataItem(block, edit.EntryIndex);
            if (slot == null)
            {
                reason = "slot index out of range";
                return false;
            }
            if (!ApplyAdvancedDataFieldValue(edit, slot, redo ? edit.NewValue : edit.OldValue, out reason)) return false;
            store.Refresh(this, edit.Handling);
            return true;
        }

        private bool ApplyAdvancedDataFieldValue(AdvancedDataFieldEdit edit, XElement slot, string value, out string reason)
        {
            reason = null;
            if (edit.IsValueRow)
            {
                float parsedFloat;
                if (!ParseFiniteFloat(value, out parsedFloat))
                {
                    reason = "value did not parse as a float";
                    return false;
                }
                SetPhantomFloatAttr(slot, "Value", ApplyHardBounds(AdvancedDataValueField, parsedFloat, 0));
                return true;
            }
            int parsedInt;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
            {
                reason = "value did not parse as an int";
                return false;
            }
            FieldEntry memberRow = ResolveAdvancedDataField(edit.Member);
            SetPhantomIntAttr(slot, edit.Member, memberRow != null ? ApplyHardBounds(memberRow, parsedInt) : parsedInt);
            return true;
        }

        private void RefreshPendingAdvancedData(HandlingKey handling)
        {
            EditorContext context = ResolveEditorContextOfHandling(handling);
            if (context == null) return;
            SyncAdvancedDataVisibility(context, AdvancedDataMenuKind.Pending);
            SyncPhantomFieldValues(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Pending].ListMenu);
            SyncLiveAdvancedDataSlot(context);
        }

        private void RefreshPhantomAdvancedData(HandlingKey handling)
        {
            XElement element = ResolvePhantomElement(handling, CATEGORY_CAR);
            EditorContext context = element != null ? ResolveContextShowingPhantomElement(handling, element) : null;
            if (context == null) return;
            SyncAdvancedDataVisibility(context, AdvancedDataMenuKind.Phantom);
            SyncPhantomFieldValues(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Phantom].ListMenu);
            SyncPhantomCarAdvancedDataSlot(context);
        }

        // With live creation on, the press asks the plugin for room for one entry, and the real list replaces the placeholder once it lands
        // Any case that cannot grow the array live, or an answer that fails, falls back to the pending export-only entry
        private void ActivateLiveAdvancedData(EditorContext context)
        {
            Vehicle vehicle = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            HandlingKey handling = ResolveHandlingKey(vehicle);
            if (pendingAdvancedData.ContainsKey(handling) || HasPendingLiveRequest(handling)) return;
            var firstEntry = new List<AdvancedDataEntry> { ParseAdvancedDataEntry(BuildDefaultAdvancedDataSlot()) };
            string growthRefusal;
            if (BeginAdvancedDataGrowth(context, vehicle, vehicle.HandlingData, 1, LiveRequestOrigin.Player, 0, firstEntry, out growthRefusal)) return;
            ResolveXmlAdvancedDataStore(AdvancedDataMenuKind.Pending).PressCreate(this, context);
        }

        // Live data always wins over a stale pending block for the same handling
        private void SyncLiveAdvancedDataSlot(EditorContext context)
        {
            Vehicle vehicle = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            HandlingData handling = vehicle.HandlingData;
            NativeItem placeholder = context.AdvancedData.CarPlaceholderItem;
            if (placeholder != null) placeholder.Description = IsLiveAdvancedDataGrowthOffered(handling) ? PLACEHOLDER_ADVANCEDDATA_CREATE_LIVE : PLACEHOLDER_CATEGORY_NOT_PRESENT;
            NativeItem desired;
            if (RawMemory.GetAdvancedDataCount(handling) > 0) desired = context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live].OpenerItem;
            else if (pendingAdvancedData.ContainsKey(ResolveHandlingKey(vehicle))) desired = context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Pending].OpenerItem;
            else desired = placeholder;
            SyncMenuSlot(GetLiveMenuForCategory(context, CATEGORY_CAR), ref context.AdvancedData.CarSlotOccupant, desired);
            SyncAdvancedDataVisibility(context, AdvancedDataMenuKind.Pending);
        }

        // SyncMenuSlot (MenuWiring.cs) keeps the scroll window still
        private void SyncPhantomCarAdvancedDataSlot(EditorContext context)
        {
            XElement element = context.Phantom.CurrentPhantomElement;
            bool activated = element != null && element.Element(ADVANCED_DATA_TAG) != null;
            NativeItem desired = activated ? (NativeItem)context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Phantom].OpenerItem : context.AdvancedData.PhantomPlaceholderItem;
            SyncMenuSlot(GetPhantomMenuForCategory(context, CATEGORY_CAR), ref context.AdvancedData.PhantomSlotOccupant, desired);
        }

        // The list grows through Add Slot and shrinks through an entry delete or an undone Add Slot, so this diffs both directions against the listed count
        // Add Slot is removed and re-added after the entry row delta is applied either way, keeping it the trailing item
        // This never dips Items.Count below where a pure growth started
        private void SyncAdvancedDataVisibility(EditorContext context, AdvancedDataMenuKind kind)
        {
            AdvancedDataStore store = AdvancedDataStores[(int)kind];
            AdvancedDataMenus lists = context.AdvancedData.Lists[(int)kind];
            SyncAdvancedDataVisibilityCore(context, kind, lists.AddSlotItem, store.IsAddSlotOffered(this, context), ref lists.ListedCount, store.CountForTree(this, context));
        }

        // The live list's Add Slot row is offered only while live growth is, so turning it off removes the row
        // It diffs against currentCount instead of rebuilding, so Items.Count never dips mid-rebuild
        // A removed entry's submenu is backed out first, so a player standing in it lands on the list
        private void SyncAdvancedDataVisibilityCore(EditorContext context, AdvancedDataMenuKind kind, NativeItem addSlotItem, bool addSlotOffered, ref int currentCount, int desired)
        {
            AdvancedDataMenus lists = context.AdvancedData.Lists[(int)kind];
            NativeMenu menu = lists.ListMenu;
            AdvancedDataItemSet[] slots = lists.Entries;
            // Captured before any mutation, since removing trailing rows drops Items.Count below the menu's own index, at which point its getter reads -1
            // The next Add then forces the selection (and the scroll window) back to the top
            int previousIndex = menu.SelectedIndex;
            bool countChanged = desired != currentCount;
            // Add Slot is only the right landing spot when *this* call grew a list that already existed
            // Opening the menu re-runs this with nothing to do, creation grows from nothing, and a shrink keeps the highlighted row, so none of them moves it there
            bool preferAddSlot = desired > currentCount && currentCount > 0;
            if (desired > currentCount)
            {
                for (int i = currentCount; i < desired; i++) menu.Add(EnsureAdvancedDataEntryMenu(context, kind, i).SubmenuItem);
            }
            else if (desired < currentCount)
            {
                for (int i = currentCount - 1; i >= desired; i--)
                {
                    BackOut(slots[i].Menu);
                    menu.Items.Remove(slots[i].SubmenuItem);
                }
            }
            currentCount = desired;
            // With no Add Slot item nothing moves unless the count changed, so an unchanged list keeps its selection with no reassignment
            if (addSlotItem == null)
            {
                if (countChanged) RestoreAdvancedDataSelection(menu, null, previousIndex, false);
                return;
            }
            bool wantsAddSlot = addSlotOffered && desired > 0 && desired < ADVANCEDDATA_COUNT_SANITY_CEILING;
            // The live list syncs on every refresh, so an unchanged list with its Add Slot row already as wanted keeps its selection untouched
            if (!countChanged && wantsAddSlot == menu.Items.Contains(addSlotItem)) return;
            menu.Items.Remove(addSlotItem);
            if (wantsAddSlot) menu.Add(addSlotItem);
            RestoreAdvancedDataSelection(menu, addSlotItem, previousIndex, preferAddSlot);
        }

        // Add Slot is the landing spot when an existing list grew, since the player just pressed it or undid a delete
        // A shrink keeps the highlighted row index, clamped, which is the next row after a delete, or Add Slot after the last one
        // On creation there is no such position to return to, so the first slot is the sensible start
        // The explicit assignment is also what forces the one repositioning pass the raw Items.Remove calls above skip
        private void RestoreAdvancedDataSelection(NativeMenu menu, NativeItem addSlotItem, int previousIndex, bool preferAddSlot)
        {
            if (menu.Items.Count == 0) return;
            ClampMenuScrollWindow(menu);
            int addSlotIndex = preferAddSlot ? menu.Items.IndexOf(addSlotItem) : -1;
            menu.SelectedIndex = addSlotIndex >= 0 ? addSlotIndex : Math.Max(0, Math.Min(previousIndex, menu.Items.Count - 1));
        }

        // The title comes from the entry's own row, and every row is wired against the store's block, which is resolved from the owning tree at event time
        // So a row stays correct even if the block were rebuilt between an edit and a later undo
        private NativeDynamicItem<int> WireAdvancedDataInt(NativeMenu menu, EditorContext owner, XmlAdvancedDataStore store, string title, string descriptorKey, int slotIndex, string subElementName)
        {
            var item = new NativeDynamicItem<int>(title, ResolveDescriptor(CATEGORY_CAR, descriptorKey));
            var template = new AdvancedDataFieldEdit(store, slotIndex, subElementName, null, null);
            WireIntField(item, AdvancedDataIndexField, BuildElementIntStore(menu, item, owner, () => store.ResolveSlotOfTree(this, owner, slotIndex), subElementName, "value", template));
            return item;
        }

        // Cycles AdvancedDataSlotTable (MenuBase.cs) instead of the bare int
        // Undo and Redo text stays a plain int, since ApplyAdvancedDataFieldValue expects a bare int and never a string-encoded value
        private NativeDynamicItem<string> WireAdvancedDataSlotStringField(NativeMenu menu, EditorContext owner, XmlAdvancedDataStore store, string title, int slotIndex)
        {
            var item = new NativeDynamicItem<string>(title, ResolveDescriptor(CATEGORY_CAR, "AdvancedDataSlot"));
            ShowRowArrows(item);
            stringCyclerItems.Add(item);
            // Not added to any menu here, like its Index and Value siblings
            // EnsureAdvancedDataEntryMenu adds all 3 to the entry's own submenu the first time SyncAdvancedDataVisibilityCore lists that entry
            RegisterUndoFocusItem(AdvancedDataFieldEdit.BuildFocusKey(store.IntFieldKind, slotIndex, "Slot"), item);
            Func<int> getter = () =>
            {
                int n;
                XElement slot = store.ResolveSlotOfTree(this, owner, slotIndex);
                return slot != null && ReadIntAttr(slot, "Slot", out n) ? n : 0;
            };
            Action<int> setter = v =>
            {
                XElement slot = store.ResolveSlotOfTree(this, owner, slotIndex);
                if (slot != null) SetPhantomIntAttr(slot, "Slot", v);
            };
            Action<int, int> pushUndo = (oldValue, newValue) => PushStructuralEditForContext(owner, new AdvancedDataFieldEdit(store, slotIndex, "Slot", oldValue.ToString(CultureInfo.InvariantCulture), newValue.ToString(CultureInfo.InvariantCulture)));
            SubscribeItemChanged(item, "WireAdvancedDataSlotStringField.ItemChanged", e =>
            {
                int current = getter();
                int next = AdvancedDataSlotCycleStep(current, e.Direction);
                setter(next);
                if (DisplayLengthChanged(FormatAdvancedDataSlotDisplay(current), FormatAdvancedDataSlotDisplay(next))) pendingWidthRecalc = true;
                e.Object = FormatAdvancedDataSlotDisplay(next);
                RecordStructuralCycleEdit(new AdvancedDataFieldEdit(store, slotIndex, "Slot", current.ToString(CultureInfo.InvariantCulture), next.ToString(CultureInfo.InvariantCulture)), owner);
            });
            SubscribeActivated(item, "WireAdvancedDataSlotStringField.Activated", () =>
            {
                int current = getter();
                int parsed = 0;
                string input = GetValidatedUserInput(current.ToString(CultureInfo.InvariantCulture), INPUT_MAX_LENGTH, candidate => int.TryParse(StripInvalidNumericCharacters(candidate, false), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed));
                if (string.IsNullOrEmpty(input))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                parsed = ApplyHardBounds(AdvancedDataSlotField, parsed);
                setter(parsed);
                item.SelectedItem = FormatAdvancedDataSlotDisplay(parsed);
                if (DisplayLengthChanged(FormatAdvancedDataSlotDisplay(current), FormatAdvancedDataSlotDisplay(parsed))) RecalculateVisibleMenuWidth(null);
                pushUndo(current, parsed);
            });
            AddPhantomSyncAction(menu, () => item.SelectedItem = FormatAdvancedDataSlotDisplay(getter()));
            return item;
        }

        private NativeDynamicItem<DisplayFloat> WireAdvancedDataValue(NativeMenu menu, EditorContext owner, XmlAdvancedDataStore store, string title, int slotIndex)
        {
            var item = new NativeDynamicItem<DisplayFloat>(title, ResolveDescriptor(CATEGORY_CAR, "AdvancedDataValue"));
            var template = new AdvancedDataFieldEdit(store, slotIndex, "Value", null, null);
            WireFloatField(item, AdvancedDataValueField, BuildElementFloatStore(menu, item, owner, () => store.ResolveSlotOfTree(this, owner, slotIndex), AdvancedDataValueField.Tag, "value", template));
            return item;
        }

        // Builds the list menu's own wiring and every entry's Slot, Index, and Value rows for an export-only store, which the pending and phantom builders share
        // Every row exists up front so its undo focus keys are registered before the entry is ever shown
        // The entry's own submenu is created when it is first listed
        // The opener is constructed directly instead of through AddStyledSubmenu, which would add it to parentMenu immediately
        private IEnumerable<bool> BuildXmlAdvancedDataEntrySets(EditorContext owner, XmlAdvancedDataStore store, NativeMenu parentMenu)
        {
            AdvancedDataMenus lists = store.MenusOf(owner);
            lists.ListMenu.Description = ResolveDescriptor(CATEGORY_CAR, "AdvancedData");
            var sets = new AdvancedDataItemSet[ADVANCEDDATA_COUNT_SANITY_CEILING];
            for (int i = 0; i < ADVANCEDDATA_COUNT_SANITY_CEILING; i++)
            {
                var set = new AdvancedDataItemSet();
                set.SlotItem = WireAdvancedDataSlotStringField(lists.ListMenu, owner, store, AdvancedDataRowTitle(i, "Slot"), i);
                set.IndexItem = WireAdvancedDataInt(lists.ListMenu, owner, store, AdvancedDataRowTitle(i, "Index"), "AdvancedDataIndex", i, "Index");
                set.ValueItem = WireAdvancedDataValue(lists.ListMenu, owner, store, AdvancedDataRowTitle(i, "Value"), i);
                set.FocusKeys = new[] { AdvancedDataFieldEdit.BuildFocusKey(store.IntFieldKind, i, "Slot"), AdvancedDataFieldEdit.BuildFocusKey(store.IntFieldKind, i, "Index"), AdvancedDataFieldEdit.BuildFocusKey(store.ValueFieldKind, i, "Value") };
                sets[i] = set;
                yield return true;
            }
            lists.Entries = sets;
            lists.AddSlotItem = new NativeItem(TITLE_ADD_SLOT, DESC_ADD_ADVANCEDDATA_ENTRY);
            SubscribeActivated(lists.AddSlotItem, "BuildXmlAdvancedDataEntrySets.AddSlotItem.Activated", () => store.PressAddSlot(this, owner));
            SubscribeShown(lists.ListMenu, "BuildXmlAdvancedDataEntrySets.ListMenu.Shown", () =>
            {
                SyncAdvancedDataVisibility(owner, store.Kind);
                SyncPhantomFieldValues(lists.ListMenu);
            });
            lists.OpenerItem = new StyledSubmenuItem(lists.ListMenu, parentMenu);
            lists.OpenerItem.Title = "AdvancedData";
        }

        // An entry's own Slot, Index, and Value rows never match, so the Delete control acts from the list only
        private static int FindAdvancedDataEntryIndex(NativeItem item, AdvancedDataItemSet[] slots)
        {
            if (item == null || slots == null) return -1;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null && slots[i].SubmenuItem != null && item == slots[i].SubmenuItem) return i;
            }
            return -1;
        }

        private static bool IsMenuVisible(NativeMenu menu) { return menu != null && menu.Visible; }

        // The store whose list is on screen, and the entry row highlighted in it
        private bool ResolveHighlightedAdvancedDataEntry(EditorContext context, out AdvancedDataStore store, out NativeMenu menu, out int entryIndex)
        {
            store = null;
            menu = null;
            entryIndex = -1;
            for (int i = 0; i < AdvancedDataStores.Length; i++)
            {
                AdvancedDataMenus lists = context.AdvancedData.Lists[i];
                if (!IsMenuVisible(lists.ListMenu)) continue;
                store = AdvancedDataStores[i];
                menu = lists.ListMenu;
                entryIndex = FindAdvancedDataEntryIndex(menu.SelectedItem, lists.Entries);
                return entryIndex >= 0;
            }
            return false;
        }

        // Fires while a list row is highlighted in a Car menu: The real list's on a live Car with entries, its pending list, or the phantom Car's
        // The confirm acts on the tree that was armed, even when another tree is on screen by then
        private void TriggerAdvancedDataBlockDelete(EditorContext context)
        {
            for (int i = 0; i < AdvancedDataStores.Length; i++)
            {
                AdvancedDataStore store = AdvancedDataStores[i];
                NativeMenu carMenu = store.ResolveCarMenu(this, context);
                NativeItem opener = store.MenusOf(context).OpenerItem;
                if (carMenu == null || opener == null || carMenu.SelectedItem != opener) continue;
                if (store.Kind == AdvancedDataMenuKind.Live && !RawMemory.RawOffsetsSupported) continue;
                ArmOrConfirm(opener, carMenu, "AdvancedData" + store.Kind + "BlockDelete", () => store.PressBlockDelete(this, context));
                return;
            }
        }

        private void TriggerAdvancedDataEntryDelete(EditorContext context)
        {
            AdvancedDataStore store;
            NativeMenu menu;
            int entryIndex;
            if (!ResolveHighlightedAdvancedDataEntry(context, out store, out menu, out entryIndex)) return;
            if (store.Kind == AdvancedDataMenuKind.Live && !RawMemory.RawOffsetsSupported) return;
            ArmOrConfirm(menu.SelectedItem, menu, "AdvancedDataEntryDelete", () => store.PressEntryDelete(this, context, entryIndex));
        }

        // Points a live entry's 3 field bindings at the menu that shows them, so a scoped field sync of that menu refreshes them and no other menu's does
        private void SetAdvancedDataRowOwner(AdvancedDataItemSet set, NativeMenu owner)
        {
            floatBindingByItem[set.ValueItem].Menu = owner;
            intBindingByItem[set.IndexItem].Menu = owner;
            foreach (List<StringFieldBinding> ownerBindings in stringFieldBindingsByOwner.Values)
            {
                for (int i = 0; i < ownerBindings.Count; i++)
                {
                    if (ownerBindings[i].Item != set.SlotItem) continue;
                    ownerBindings[i].Menu = owner;
                    return;
                }
            }
        }

        // Created the first time the entry is listed, then kept for reuse
        // The entry's 3 rows already exist from startup, so only the menu, the row, and their registration are new here
        private AdvancedDataItemSet EnsureAdvancedDataEntryMenu(EditorContext context, AdvancedDataMenuKind kind, int index)
        {
            AdvancedDataMenus lists = context.AdvancedData.Lists[(int)kind];
            AdvancedDataItemSet set = lists.Entries[index];
            if (set.Menu != null) return set;
            NativeMenu listMenu = lists.ListMenu;
            string entryTitle = string.Format(TITLE_ENTRY, (index + 1));
            NativeMenu entryMenu = MakeMenu(TITLE_EDIT_HANDLING, entryTitle);
            entryMenu.Description = ResolveDescriptor(CATEGORY_CAR, "AdvancedData");
            entryMenu.Add(set.SlotItem);
            entryMenu.Add(set.IndexItem);
            entryMenu.Add(set.ValueItem);
            set.Menu = entryMenu;
            set.SubmenuItem = new StyledSubmenuItem(entryMenu, listMenu);
            set.SubmenuItem.Title = entryTitle;
            set.SubmenuItem.Description = ResolveDescriptor(CATEGORY_CAR, "AdvancedData");
            for (int i = 0; i < set.FocusKeys.Length; i++) RegisterUndoFocusItem(set.FocusKeys[i], set.SubmenuItem);
            RegisterRuntimeMenu(entryMenu, context);
            // An export-only entry's rows keep their sync actions registered under the list menu they were wired against
            if (kind == AdvancedDataMenuKind.Live) SetAdvancedDataRowOwner(set, entryMenu);
            else SubscribeShown(entryMenu, "EnsureAdvancedDataEntryMenu.Shown", () => SyncPhantomFieldValues(listMenu));
            if (logger.IsInfoEnabled()) logger.Log(Dbg.Level.Info, Dbg.AdvancedDataEntryMenuBuilt(index, context.Index, kind));
            return set;
        }

        // Closes every visible entry submenu of one AdvancedData list, then the list itself, so a player inside an entry never stays on a removed one
        private void BackOutAdvancedDataMenus(AdvancedDataMenus lists)
        {
            AdvancedDataItemSet[] slots = lists.Entries;
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; i++) if (slots[i] != null) BackOut(slots[i].Menu);
            }
            BackOut(lists.ListMenu);
        }
    }
}
