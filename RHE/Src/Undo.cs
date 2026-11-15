using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // ==================
        // Undo/redo/undo all
        // ==================
        // Every entry is data only, with targets resolved live at fire time, because a delegate or an object reference cannot be written to disk and survive a reload
        // Every kind of change is one StructuralEdit, so the stacks, the group walk, and the persisted lines all see one shape
        // GroupId ties together the entries one user action pushed (a conflict group cascade), which Undo and Redo move as one event. 0 means not grouped
        private class UndoEntry
        {
            public StructuralEdit Edit;
            public int GroupId;
        }

        // Kind names are persisted verbatim, so renaming one invalidates history files from older builds
        // The AdvancedData kinds are named for their store: Live is the real array, Pending is the pending block of a live Car with no entries
        // Phantom is the block of an absent Car
        private static class StructuralKind
        {
            public const string Field                      = "Field";
            public const string HandlingApply              = "HandlingApply";
            public const string SubDataToggle              = "SubDataToggle";
            public const string PhantomCreate              = "PhantomCreate";
            public const string PhantomFloat               = "PhantomFloat";
            public const string PhantomInt                 = "PhantomInt";
            public const string PhantomVector              = "PhantomVector";
            public const string PhantomFlag                = "PhantomFlag";
            public const string PhantomAdvancedInt         = "PhantomAdvancedInt";
            public const string PhantomAdvancedValue       = "PhantomAdvancedValue";
            public const string PhantomAdvancedCreate      = "PhantomAdvancedCreate";
            public const string PhantomAdvancedAddSlot     = "PhantomAdvancedAddSlot";
            public const string PhantomAdvancedDelete      = "PhantomAdvancedDelete";
            public const string PhantomAdvancedEntryDelete = "PhantomAdvancedEntryDelete";
            public const string PendingAdvancedInt         = "PendingAdvancedInt";
            public const string PendingAdvancedValue       = "PendingAdvancedValue";
            public const string PendingAdvancedCreate      = "PendingAdvancedCreate";
            public const string PendingAdvancedAddSlot     = "PendingAdvancedAddSlot";
            public const string PendingAdvancedDelete      = "PendingAdvancedDelete";
            public const string PendingAdvancedEntryDelete = "PendingAdvancedEntryDelete";
            public const string LiveAdvancedCreate         = "LiveAdvancedCreate";
            public const string LiveAdvancedAddSlot        = "LiveAdvancedAddSlot";
            public const string LiveAdvancedDelete         = "LiveAdvancedDelete";
            public const string LiveAdvancedEntryDelete    = "LiveAdvancedEntryDelete";
            public const string WeaponSlotAdd              = "WeaponSlotAdd";
            public const string WeaponSlotDelete           = "WeaponSlotDelete";
            public const string WeaponSlotReorder          = "WeaponSlotReorder";
            public const string PhantomWeaponSlotAdd       = "PhantomWeaponSlotAdd";
            public const string PhantomWeaponSlotDelete    = "PhantomWeaponSlotDelete";
            public const string PhantomWeaponSlotReorder   = "PhantomWeaponSlotReorder";
            public const string PhantomWeaponField         = "PhantomWeaponField";
            public const string AiHandlingOverride         = "AiHandlingOverride";
            public const string LiveCategoryCreate         = "LiveCategoryCreate";
            public const string LiveCategoryDelete         = "LiveCategoryDelete";
        }

        // One undo record for every kind of change: A field text edit, an applied handling, an export-only element edit, a lifecycle change, or a live array edit
        // Kind is the persisted kind string, so a family that covers a create and its delete holds the one it was built for
        // No field can contain the pipe the history file delimits on
        private abstract class StructuralEdit
        {
            public readonly string Kind;
            public HandlingKey Handling;
            protected StructuralEdit(string kind) { Kind = kind; }
            // Built identically at registration and at resolution, and Handling is left out, since the same row serves every vehicle its tree edits
            // Null for an edit with no row of its own, which AddLifecycleFocusItems names instead
            public abstract string FocusKey { get; }
            // Names the edit in the log and nothing else, and is never parsed back or used to tell edits apart
            // The category leads the target, since tags repeat across categories
            public abstract string Label { get; }
            public virtual string Description { get { return Kind; } }
            // The category an export-only edit belongs to, or null for an edit of a live category or of the vehicle itself
            // Once that category exists live the edit is obsolete, since replaying it would write into an element the game no longer reads
            public virtual string ExportOnlyCategory { get { return null; } }
            // True when Apply only queues the work and the outcome arrives on a later frame
            // Undo and Redo then post no done toast, since the outcome's own toast is the feedback
            public virtual bool FinishesLater { get { return false; } }
            // vehicle and handling are the live ones the replay resolved, which only the families that mutate the vehicle itself read. reason is set on every False return
            public abstract bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason);
            public abstract void WritePayload(StringBuilder sb);
            // True when this edit is the tick or untick of the named flag, which is how a conflict group cascade finds the entry the clicked flag pushed
            public virtual bool IsForFlag(string flagName) { return false; }
            // Posts the edit's own result log and toasts in place of the generic done toast, and returns True when it did
            public virtual bool ReportApplyResult(RageHandlingEditor script, UndoStepMessages messages) { return false; }
            // Rows an edit with no field item of its own affects: A category's submenu entry, a weapon slot's row, or an AdvancedData block's entry
            public virtual void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items) { }
            protected static void AppendField(StringBuilder sb, string text) { sb.Append('|').Append(text); }
            protected static void AppendField(StringBuilder sb, int value) { sb.Append('|').Append(value.ToString(CultureInfo.InvariantCulture)); }
            protected static void AppendField(StringBuilder sb, bool value) { sb.Append('|').Append(value.ToString()); }
            protected static bool ParseInt(string text, out int value) { return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value); }
            protected static string ParseOptionalText(string text) { return text.Length == 0 ? null : text; }
        }

        // A family whose edit moves one stored value, the only kind a held Left or Right can batch: The pending entry takes the latest NewValue
        private abstract class ValueEdit : StructuralEdit
        {
            public string OldValue;
            public string NewValue;
            protected ValueEdit(string kind, string oldValue, string newValue) : base(kind) { OldValue = oldValue; NewValue = newValue; }
            // A copy that keeps every identity field, since a record is mutated once it is pushed
            public ValueEdit WithValues(string oldValue, string newValue)
            {
                var copy = (ValueEdit)MemberwiseClone();
                copy.OldValue = oldValue;
                copy.NewValue = newValue;
                return copy;
            }
        }

        // A text edit of a field some tree registered in fieldRegistry, so its replay needs no element of its own
        // The key is the registry key, which also names the row the edit focuses
        private class FieldEdit : ValueEdit
        {
            public static readonly string[] Kinds = { StructuralKind.Field };
            public readonly string Key;
            public FieldEdit(string key, string oldValue, string newValue) : base(StructuralKind.Field, oldValue, newValue) { Key = key; }
            public override string FocusKey { get { return Key; } }
            public override string Label { get { return Key; } }
            public override string Description { get { return Key; } }
            // A live flag's key is its category, a dot, its name, and an optional bracketed editor suffix
            public override bool IsForFlag(string flagName)
            {
                int nameStart = Key.IndexOf("." + flagName, StringComparison.Ordinal) + 1;
                if (nameStart == 0) return false;
                int nameEnd = nameStart + flagName.Length;
                return nameEnd == Key.Length || Key[nameEnd] == '[';
            }

            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyUndoEntry(vehicle, Key, redo ? NewValue : OldValue, ref replayFlags, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, Key); AppendField(sb, OldValue); AppendField(sb, NewValue); }
            public static StructuralEdit Parse(string[] parts)
            {
                return parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 3 ? null : new FieldEdit(parts[STRUCTURAL_EDIT_PAYLOAD_START], parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], parts[STRUCTURAL_EDIT_PAYLOAD_START + 2]);
            }
        }

        // An applied handling, carrying the whole handling as XML: UndoSnapshot is the handling before the apply, and RedoSnapshot is captured when it is undone
        private class HandlingApplyEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.HandlingApply };
            public XDocument UndoSnapshot;
            public XDocument RedoSnapshot;
            // How many fields the latest apply of this edit skipped, which its result toast reports. Never persisted
            public int FieldsSkipped;
            public HandlingApplyEdit(XDocument undoSnapshot, XDocument redoSnapshot) : base(StructuralKind.HandlingApply) { UndoSnapshot = undoSnapshot; RedoSnapshot = redoSnapshot; }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyHandlingApplyEntry(this, redo, handling, vehicle, ref replayFlags, out reason); }
            public override bool ReportApplyResult(RageHandlingEditor script, UndoStepMessages messages)
            {
                script.logger.Log(Dbg.Level.Info, messages.AppliedHandlingLog());
                script.notifications.Notify(NOTIFICATION_KEY_HANDLING_APPLY, FieldsSkipped > 0 ? string.Format(messages.ApplySkippedToast, FieldsSkipped) : messages.ApplyToast);
                return true;
            }

            // Base64 text sits on a single line, and its alphabet never contains the pipe the history file delimits on
            public override void WritePayload(StringBuilder sb) { AppendField(sb, EncodeSnapshot(UndoSnapshot)); AppendField(sb, EncodeSnapshot(RedoSnapshot)); }
            public static StructuralEdit Parse(string[] parts)
            {
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 2) return null;
                try { return new HandlingApplyEdit(DecodeSnapshot(parts[STRUCTURAL_EDIT_PAYLOAD_START]), DecodeSnapshot(parts[STRUCTURAL_EDIT_PAYLOAD_START + 1])); }
                catch (Exception) { return null; }
            }
        }

        private class CategoryToggleEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.SubDataToggle, StructuralKind.PhantomCreate };
            public readonly string Category;
            public readonly bool ToggledBefore;
            public readonly bool ToggledAfter;
            public CategoryToggleEdit(string kind, string category, bool toggledBefore, bool toggledAfter) : base(kind) { Category = category; ToggledBefore = toggledBefore; ToggledAfter = toggledAfter; }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":" + Category; } }
            public override string Description { get { return Kind + " (" + Category + ")"; } }
            // An exclusion toggles a category that is present, so only a creation is export-only
            public override string ExportOnlyCategory { get { return Kind == StructuralKind.PhantomCreate ? Category : null; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplySubDataToggle(this, redo, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, Category); AppendField(sb, ToggledBefore); AppendField(sb, ToggledAfter); }
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                if (context.Categories == null) return;
                foreach (SubHandlingCategory row in context.Categories)
                {
                    if (row.Definition.Name == Category)
                    {
                        items.Add(row.LiveSubmenu);
                        items.Add(row.PhantomSubmenu);
                    }
                }
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                bool toggledBefore, toggledAfter;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 3 || !bool.TryParse(parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], out toggledBefore) || !bool.TryParse(parts[STRUCTURAL_EDIT_PAYLOAD_START + 2], out toggledAfter)) return null;
                return new CategoryToggleEdit(kind, parts[STRUCTURAL_EDIT_PAYLOAD_START], toggledBefore, toggledAfter);
            }
        }

        private class PhantomFieldEdit : ValueEdit
        {
            public static readonly string[] Kinds = { StructuralKind.PhantomFloat, StructuralKind.PhantomInt, StructuralKind.PhantomVector };
            public readonly string Category;
            public readonly string Tag;
            // x, y, or z for a vector axis, null for a scalar
            public readonly string Component;
            public PhantomFieldEdit(string kind, string category, string tag, string component, string oldValue, string newValue) : base(kind, oldValue, newValue) { Category = category; Tag = tag; Component = component; }
            public override string FocusKey { get { return Kind + "|" + Category + "|" + Tag + "|" + Component; } }
            public override string Label { get { return Kind + ":" + Category + "." + Tag + (Component != null ? "." + Component : ""); } }
            public override string Description { get { return Kind + " (" + Category + ")"; } }
            public override string ExportOnlyCategory { get { return Category; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyPhantomFieldEdit(this, redo, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, Category); AppendField(sb, Tag); AppendField(sb, Component); AppendField(sb, OldValue); AppendField(sb, NewValue); }
            public static StructuralEdit Parse(string kind, string[] parts)
            {
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 5) return null;
                return new PhantomFieldEdit(kind, parts[STRUCTURAL_EDIT_PAYLOAD_START], parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], ParseOptionalText(parts[STRUCTURAL_EDIT_PAYLOAD_START + 2]), parts[STRUCTURAL_EDIT_PAYLOAD_START + 3], parts[STRUCTURAL_EDIT_PAYLOAD_START + 4]);
            }
        }

        private class PhantomFlagEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.PhantomFlag };
            // Car for AdvancedFlags and SpecialFlight for its Flags, the names the export-only elements are filed under
            public readonly string Category;
            public readonly string FlagName;
            public readonly int BitIndex;
            public readonly bool CheckedBefore;
            public readonly bool CheckedAfter;
            public PhantomFlagEdit(string category, string flagName, int bitIndex, bool checkedBefore, bool checkedAfter) : base(StructuralKind.PhantomFlag)
            {
                Category = category;
                FlagName = flagName;
                BitIndex = bitIndex;
                CheckedBefore = checkedBefore;
                CheckedAfter = checkedAfter;
            }

            public static string BuildFocusKey(string category, string flagName, int bitIndex) { return StructuralKind.PhantomFlag + "|" + category + "|" + flagName + "|" + bitIndex; }
            public override string FocusKey { get { return BuildFocusKey(Category, FlagName, BitIndex); } }
            public override string Label { get { return Kind + ":" + Category + "." + FlagName; } }
            public override string Description { get { return Kind + " (" + Category + ")"; } }
            public override string ExportOnlyCategory { get { return Category; } }
            public override bool IsForFlag(string flagName) { return FlagName == flagName; }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyPhantomFlagEdit(this, redo, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, Category); AppendField(sb, FlagName); AppendField(sb, BitIndex); AppendField(sb, CheckedBefore); AppendField(sb, CheckedAfter); }
            public static StructuralEdit Parse(string[] parts)
            {
                int bitIndex;
                bool checkedBefore, checkedAfter;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 5 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START + 2], out bitIndex) || !bool.TryParse(parts[STRUCTURAL_EDIT_PAYLOAD_START + 3], out checkedBefore) || !bool.TryParse(parts[STRUCTURAL_EDIT_PAYLOAD_START + 4], out checkedAfter)) return null;
                return new PhantomFlagEdit(parts[STRUCTURAL_EDIT_PAYLOAD_START], parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], bitIndex, checkedBefore, checkedAfter);
            }
        }

        // An edit of one Slot, Index, or Value row of a pending or phantom AdvancedData entry. The real array's rows are ordinary live fields, which FieldEdit records
        private class AdvancedDataFieldEdit : ValueEdit
        {
            public static readonly string[] Kinds = { StructuralKind.PhantomAdvancedInt, StructuralKind.PhantomAdvancedValue, StructuralKind.PendingAdvancedInt, StructuralKind.PendingAdvancedValue };
            public readonly XmlAdvancedDataStore Store;
            public readonly int EntryIndex;
            // Slot, Index, or Value
            public readonly string Member;
            public AdvancedDataFieldEdit(XmlAdvancedDataStore store, int entryIndex, string member, string oldValue, string newValue) : base(member == "Value" ? store.ValueFieldKind : store.IntFieldKind, oldValue, newValue) { Store = store; EntryIndex = entryIndex; Member = member; }
            public bool IsValueRow { get { return Member == "Value"; } }
            public static string BuildFocusKey(string kind, int entryIndex, string member) { return kind + "|" + entryIndex + "|" + member; }
            public override string FocusKey { get { return BuildFocusKey(Kind, EntryIndex, Member); } }
            public override string Label { get { return Kind + ":" + Store.LabelScope + "AdvancedData[" + EntryIndex + "]." + Member; } }
            public override string Description { get { return Store.Describe(Kind); } }
            public override string ExportOnlyCategory { get { return Store.PhantomCategory; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyAdvancedDataFieldEdit(this, redo, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, EntryIndex); AppendField(sb, Member); AppendField(sb, OldValue); AppendField(sb, NewValue); }
            public static StructuralEdit Parse(string kind, string[] parts)
            {
                int entryIndex;
                var store = ResolveAdvancedDataStoreOfKind(kind) as XmlAdvancedDataStore;
                if (store == null || parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 4 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out entryIndex)) return null;
                string member = parts[STRUCTURAL_EDIT_PAYLOAD_START + 1];
                bool validMember = kind == store.ValueFieldKind ? member == "Value" : member == "Slot" || member == "Index";
                if (!validMember) return null;
                return new AdvancedDataFieldEdit(store, entryIndex, member, parts[STRUCTURAL_EDIT_PAYLOAD_START + 2], parts[STRUCTURAL_EDIT_PAYLOAD_START + 3]);
            }
        }

        // The creation or deletion of a whole AdvancedData block in any store, which a create undoes as a delete and a delete undoes as a create
        private class AdvancedDataBlockEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.LiveAdvancedCreate, StructuralKind.LiveAdvancedDelete, StructuralKind.PendingAdvancedCreate, StructuralKind.PendingAdvancedDelete, StructuralKind.PhantomAdvancedCreate, StructuralKind.PhantomAdvancedDelete };
            public readonly AdvancedDataStore Store;
            // The block as the store encodes it, captured immediately before it is detached, so the opposite direction restores exactly what was taken away
            // Null until a create is first undone
            public string EncodedBlock;
            public AdvancedDataBlockEdit(AdvancedDataStore store, string kind, string encodedBlock) : base(kind) { Store = store; EncodedBlock = encodedBlock; }
            public bool IsCreate { get { return Kind == Store.BlockCreateKind; } }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":" + (Store.PhantomCategory ?? ""); } }
            public override string Description { get { return Store.Describe(Kind); } }
            public override string ExportOnlyCategory { get { return Store.PhantomCategory; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyAdvancedDataBlockEdit(this, redo, handling, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, EncodedBlock); }
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                items.Add(Store.MenusOf(context).OpenerItem);
                items.Add(Store.ResolvePlaceholder(context));
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                AdvancedDataStore store = ResolveAdvancedDataStoreOfKind(kind);
                return store == null || parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 1 ? null : new AdvancedDataBlockEdit(store, kind, ParseOptionalText(parts[STRUCTURAL_EDIT_PAYLOAD_START]));
            }
        }

        // The addition or deletion of one AdvancedData entry in any store, which an addition undoes as a deletion and a deletion undoes as an addition
        private class AdvancedDataEntryEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.LiveAdvancedAddSlot, StructuralKind.LiveAdvancedEntryDelete, StructuralKind.PendingAdvancedAddSlot, StructuralKind.PendingAdvancedEntryDelete, StructuralKind.PhantomAdvancedAddSlot, StructuralKind.PhantomAdvancedEntryDelete };
            public readonly AdvancedDataStore Store;
            public readonly int EntryIndex;
            // The entry as the store encodes it, captured immediately before it is detached. Null until an added entry is first undone
            public string EncodedEntry;
            public AdvancedDataEntryEdit(AdvancedDataStore store, string kind, int entryIndex, string encodedEntry) : base(kind) { Store = store; EntryIndex = entryIndex; EncodedEntry = encodedEntry; }
            public bool IsAddSlot { get { return Kind == Store.AddSlotKind; } }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":" + Store.LabelScope + "AdvancedData[" + EntryIndex + "]"; } }
            public override string Description { get { return Store.Describe(Kind); } }
            public override string ExportOnlyCategory { get { return Store.PhantomCategory; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyAdvancedDataEntryEdit(this, redo, handling, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, EntryIndex); AppendField(sb, EncodedEntry); }
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                AddAdvancedDataEntryFocusItem(items, Store.MenusOf(context).Entries, EntryIndex);
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                int entryIndex;
                AdvancedDataStore store = ResolveAdvancedDataStoreOfKind(kind);
                if (store == null || parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 2 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out entryIndex)) return null;
                return new AdvancedDataEntryEdit(store, kind, entryIndex, ParseOptionalText(parts[STRUCTURAL_EDIT_PAYLOAD_START + 1]));
            }
        }

        private class WeaponSlotStateEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.WeaponSlotAdd, StructuralKind.WeaponSlotDelete };
            public readonly int SlotIndex;
            public readonly string StateBefore;
            public readonly string StateAfter;
            public WeaponSlotStateEdit(string kind, int slotIndex, string stateBefore, string stateAfter) : base(kind) { SlotIndex = slotIndex; StateBefore = stateBefore; StateAfter = stateAfter; }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":WeaponSlot[" + SlotIndex + "]"; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyWeaponSlotAdd(this, redo, handling, ref replayFlags, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, SlotIndex); AppendField(sb, StateBefore); AppendField(sb, StateAfter); }
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                if (context.Weapon.WeaponSlots != null && SlotIndex >= 0 && SlotIndex < context.Weapon.WeaponSlots.Length) items.Add(context.Weapon.WeaponSlots[SlotIndex].SubmenuItem);
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                int slotIndex;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 3 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out slotIndex)) return null;
                return new WeaponSlotStateEdit(kind, slotIndex, parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], parts[STRUCTURAL_EDIT_PAYLOAD_START + 2]);
            }
        }

        // Self-inverse, so undo and redo swap the same pair again
        private class WeaponSlotReorderEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.WeaponSlotReorder, StructuralKind.PhantomWeaponSlotReorder };
            public readonly int FromIndex;
            public readonly int ToIndex;
            public WeaponSlotReorderEdit(string kind, int fromIndex, int toIndex) : base(kind) { FromIndex = fromIndex; ToIndex = toIndex; }
            public bool IsPhantom { get { return Kind == StructuralKind.PhantomWeaponSlotReorder; } }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":" + (IsPhantom ? CATEGORY_WEAPON + "." : "") + "WeaponSlot[" + FromIndex + "]"; } }
            public override string Description { get { return IsPhantom ? Kind + " (" + CATEGORY_WEAPON + ")" : Kind; } }
            public override string ExportOnlyCategory { get { return IsPhantom ? CATEGORY_WEAPON : null; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason)
            {
                return IsPhantom ? script.ApplyPhantomWeaponSlotReorder(this, out reason) : script.ApplyWeaponSlotReorder(this, handling, ref replayFlags, out reason);
            }

            public override void WritePayload(StringBuilder sb) { AppendField(sb, FromIndex); AppendField(sb, ToIndex); }
            // A reorder lands the moved weapon at ToIndex on redo and back at FromIndex on undo
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                int landedIndex = redo ? ToIndex : FromIndex;
                if (IsPhantom)
                {
                    if (context.Phantom.PhantomWeaponSlots != null && landedIndex >= 0 && landedIndex < context.Phantom.PhantomWeaponSlots.Length) items.Add(context.Phantom.PhantomWeaponSlots[landedIndex].SubmenuItem);
                    return;
                }
                if (context.Weapon.WeaponSlots != null && landedIndex >= 0 && landedIndex < context.Weapon.WeaponSlots.Length) items.Add(context.Weapon.WeaponSlots[landedIndex].SubmenuItem);
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                int fromIndex, toIndex;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 2 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out fromIndex) || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], out toIndex)) return null;
                return new WeaponSlotReorderEdit(kind, fromIndex, toIndex);
            }
        }

        private class PhantomWeaponSlotEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.PhantomWeaponSlotAdd, StructuralKind.PhantomWeaponSlotDelete };
            public readonly int SlotIndex;
            // An add holds the slot's hash text. A delete holds the slot's full data, since the array shifts and a delete is never a tail-only operation
            // That data is comma-separated parts: Hash text, seat, mod type, one value per TurretFields row, and the pitch limits
            // Recaptured when an add is undone
            public string SlotData;
            public PhantomWeaponSlotEdit(string kind, int slotIndex, string slotData) : base(kind) { SlotIndex = slotIndex; SlotData = slotData; }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":" + CATEGORY_WEAPON + ".WeaponSlot[" + SlotIndex + "]"; } }
            public override string Description { get { return Kind + " (" + CATEGORY_WEAPON + ")"; } }
            public override string ExportOnlyCategory { get { return CATEGORY_WEAPON; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason)
            {
                return Kind == StructuralKind.PhantomWeaponSlotAdd ? script.ApplyPhantomWeaponSlotAdd(this, redo, out reason) : script.ApplyPhantomWeaponSlotDelete(this, redo, out reason);
            }

            public override void WritePayload(StringBuilder sb) { AppendField(sb, SlotIndex); AppendField(sb, SlotData); }
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                if (context.Phantom.PhantomWeaponSlots != null && SlotIndex >= 0 && SlotIndex < context.Phantom.PhantomWeaponSlots.Length) items.Add(context.Phantom.PhantomWeaponSlots[SlotIndex].SubmenuItem);
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                int slotIndex;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 2 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out slotIndex)) return null;
                return new PhantomWeaponSlotEdit(kind, slotIndex, ParseOptionalText(parts[STRUCTURAL_EDIT_PAYLOAD_START + 1]));
            }
        }

        private class PhantomWeaponFieldEdit : ValueEdit
        {
            public static readonly string[] Kinds = { StructuralKind.PhantomWeaponField };
            public readonly int SlotIndex;
            // Hash, Seat, or ModType
            public readonly string Member;
            public PhantomWeaponFieldEdit(int slotIndex, string member, string oldValue, string newValue) : base(StructuralKind.PhantomWeaponField, oldValue, newValue) { SlotIndex = slotIndex; Member = member; }
            public static string BuildFocusKey(int slotIndex, string member) { return StructuralKind.PhantomWeaponField + "|" + slotIndex + "|" + member; }
            public override string FocusKey { get { return BuildFocusKey(SlotIndex, Member); } }
            public override string Label { get { return Kind + ":" + CATEGORY_WEAPON + ".WeaponSlot[" + SlotIndex + "]." + Member; } }
            public override string Description { get { return Kind + " (" + CATEGORY_WEAPON + ")"; } }
            public override string ExportOnlyCategory { get { return CATEGORY_WEAPON; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyPhantomWeaponField(this, redo, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, SlotIndex); AppendField(sb, Member); AppendField(sb, OldValue); AppendField(sb, NewValue); }
            public static StructuralEdit Parse(string[] parts)
            {
                int slotIndex;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 4 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out slotIndex)) return null;
                string member = parts[STRUCTURAL_EDIT_PAYLOAD_START + 1];
                if (member != "Hash" && member != "Seat" && member != "ModType") return null;
                return new PhantomWeaponFieldEdit(slotIndex, member, parts[STRUCTURAL_EDIT_PAYLOAD_START + 2], parts[STRUCTURAL_EDIT_PAYLOAD_START + 3]);
            }
        }

        private class AiHandlingEdit : ValueEdit
        {
            public static readonly string[] Kinds = { StructuralKind.AiHandlingOverride };
            // The tree whose row cycled it: 0 is Current, 1 to MAX_ADDITIONAL_EDITORS is that extra editor
            // Only the focus key and the label read it, since the apply repoints the handling it is given
            public readonly int EditorIndex;
            public AiHandlingEdit(int editorIndex, string oldValue, string newValue) : base(StructuralKind.AiHandlingOverride, oldValue, newValue) { EditorIndex = editorIndex; }
            public static string BuildFocusKey(int editorIndex) { return StructuralKind.AiHandlingOverride + "|" + editorIndex; }
            public override string FocusKey { get { return BuildFocusKey(EditorIndex); } }
            public override string Label { get { return Kind + ":" + (EditorIndex == 0 ? "Current" : "Editor " + EditorIndex); } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyAiHandlingOverride(this, redo, handling, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, EditorIndex); AppendField(sb, OldValue); AppendField(sb, NewValue); }
            public static StructuralEdit Parse(string[] parts)
            {
                int editorIndex;
                if (parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 3 || !ParseInt(parts[STRUCTURAL_EDIT_PAYLOAD_START], out editorIndex)) return null;
                return new AiHandlingEdit(editorIndex, parts[STRUCTURAL_EDIT_PAYLOAD_START + 1], parts[STRUCTURAL_EDIT_PAYLOAD_START + 2]);
            }
        }

        // A category created or deleted in the running game, never an export-only one, so its undo is never ignored by the export-only rule
        // A delete entry's undo creates the category again and its redo deletes it, and a create entry runs the other way round
        // EncodedBlock is the category's element captured immediately before each removal, which the creation direction restores
        // A create entry holds none until its first undo removes the category
        private class LiveCategoryEdit : StructuralEdit
        {
            public static readonly string[] Kinds = { StructuralKind.LiveCategoryCreate, StructuralKind.LiveCategoryDelete };
            public readonly string Category;
            public string EncodedBlock;
            public LiveCategoryEdit(string kind, string category, string encodedBlock) : base(kind) { Category = category; EncodedBlock = encodedBlock; }
            public override string FocusKey { get { return null; } }
            public override string Label { get { return Kind + ":" + Category; } }
            public override string Description { get { return Kind + " (" + Category + ")"; } }
            public override bool FinishesLater { get { return true; } }
            public override bool Apply(RageHandlingEditor script, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out string reason) { return script.ApplyLiveCategoryEdit(this, redo, handling, out reason); }
            public override void WritePayload(StringBuilder sb) { AppendField(sb, Category); AppendField(sb, EncodedBlock); }
            public override void AddLifecycleFocusItems(EditorContext context, bool redo, List<NativeItem> items)
            {
                if (context.Categories == null) return;
                foreach (SubHandlingCategory row in context.Categories)
                {
                    if (row.Definition.Name != Category) continue;
                    items.Add(row.LiveSubmenu);
                    items.Add(row.Placeholder);
                }
            }

            public static StructuralEdit Parse(string kind, string[] parts)
            {
                return parts.Length != STRUCTURAL_EDIT_PAYLOAD_START + 2 ? null : new LiveCategoryEdit(kind, parts[STRUCTURAL_EDIT_PAYLOAD_START], ParseOptionalText(parts[STRUCTURAL_EDIT_PAYLOAD_START + 1]));
            }
        }

        // Every family lists the kinds it writes in Kinds, so a kind on disk always has a parser
        private static readonly Dictionary<string, Func<string, string[], StructuralEdit>> StructuralEditParsers = BuildStructuralEditParsers();
        private static Dictionary<string, Func<string, string[], StructuralEdit>> BuildStructuralEditParsers()
        {
            var parsers = new Dictionary<string, Func<string, string[], StructuralEdit>>();
            RegisterStructuralEditParser(parsers, FieldEdit.Kinds, (kind, parts) => FieldEdit.Parse(parts));
            RegisterStructuralEditParser(parsers, HandlingApplyEdit.Kinds, (kind, parts) => HandlingApplyEdit.Parse(parts));
            RegisterStructuralEditParser(parsers, CategoryToggleEdit.Kinds, CategoryToggleEdit.Parse);
            RegisterStructuralEditParser(parsers, PhantomFieldEdit.Kinds, PhantomFieldEdit.Parse);
            RegisterStructuralEditParser(parsers, PhantomFlagEdit.Kinds, (kind, parts) => PhantomFlagEdit.Parse(parts));
            RegisterStructuralEditParser(parsers, AdvancedDataFieldEdit.Kinds, AdvancedDataFieldEdit.Parse);
            RegisterStructuralEditParser(parsers, AdvancedDataBlockEdit.Kinds, AdvancedDataBlockEdit.Parse);
            RegisterStructuralEditParser(parsers, AdvancedDataEntryEdit.Kinds, AdvancedDataEntryEdit.Parse);
            RegisterStructuralEditParser(parsers, WeaponSlotStateEdit.Kinds, WeaponSlotStateEdit.Parse);
            RegisterStructuralEditParser(parsers, WeaponSlotReorderEdit.Kinds, WeaponSlotReorderEdit.Parse);
            RegisterStructuralEditParser(parsers, PhantomWeaponSlotEdit.Kinds, PhantomWeaponSlotEdit.Parse);
            RegisterStructuralEditParser(parsers, PhantomWeaponFieldEdit.Kinds, (kind, parts) => PhantomWeaponFieldEdit.Parse(parts));
            RegisterStructuralEditParser(parsers, AiHandlingEdit.Kinds, (kind, parts) => AiHandlingEdit.Parse(parts));
            RegisterStructuralEditParser(parsers, LiveCategoryEdit.Kinds, LiveCategoryEdit.Parse);
            return parsers;
        }

        private static void RegisterStructuralEditParser(Dictionary<string, Func<string, string[], StructuralEdit>> parsers, string[] kinds, Func<string, string[], StructuralEdit> parse)
        {
            for (int i = 0; i < kinds.Length; i++) parsers[kinds[i]] = parse;
        }

        // One time-ordered history per handling, shared by every vehicle model on it
        // Field edits, structural edits, and applied handlings share the same stacks, so every Undo steps back in true order
        // Undo All walks every entry back to the handling at session start
        private class UndoHistory
        {
            public List<UndoEntry> UndoStack = new List<UndoEntry>();
            public List<UndoEntry> RedoStack = new List<UndoEntry>();
            public Vehicle LastKnownVehicle;
            // The whole live handling as it stood before the history's first entry, which Undo All restores in one step
            public XDocument Baseline;
            public bool BaselineMissingLogged;
        }

        // Rejected means the text does not fit the field. CategoryAbsent means the vehicle lacks the field's category, so nothing was written
        private enum ApplyOutcome { Applied, Rejected, CategoryAbsent }
        // What a fieldRegistry closure returns: The outcome, and for an applied value the follow-up work it asks of its caller
        private struct ApplyResult
        {
            public ApplyOutcome Outcome;
            public ReplayFlags Flags;
            private ApplyResult(ApplyOutcome outcome, ReplayFlags flags) { Outcome = outcome; Flags = flags; }
            public static ApplyResult Applied(ReplayFlags flags) { return new ApplyResult(ApplyOutcome.Applied, flags); }
            public static ApplyResult Rejected() { return new ApplyResult(ApplyOutcome.Rejected, ReplayFlags.None); }
            public static ApplyResult CategoryAbsent() { return new ApplyResult(ApplyOutcome.CategoryAbsent, ReplayFlags.None); }
        }

        // A closure writes the text onto the vehicle it is given, so a replay can never reach a vehicle other than the one its caller resolved
        private Dictionary<string, Func<Vehicle, string, ApplyResult>> fieldRegistry = new Dictionary<string, Func<Vehicle, string, ApplyResult>>();

        private Dictionary<HandlingKey, UndoHistory> undoHistories = new Dictionary<HandlingKey, UndoHistory>();
        private bool hasActiveHandling;
        private HandlingKey activeHandlingKey;

        private UndoHistory GetOrCreateHistory(HandlingKey handling) { return GetOrAdd(undoHistories, handling); }

        // Only ever a live vehicle on the history's own handling, so the on-foot undo fallback can never apply one handling's entries to another
        private void StampLastKnownVehicle(UndoHistory history, HandlingKey handling)
        {
            Vehicle vehicle = ResolveLiveVehicleOfHandling(handling);
            if (vehicle != null) history.LastKnownVehicle = vehicle;
        }

        private void PushUndo(HandlingKey handling, string fieldKey, string oldValueText, string newValueText)
        {
            PushStructuralEdit(new FieldEdit(fieldKey, oldValueText, newValueText) { Handling = handling });
        }

        // The mutation itself lives in the record's Apply, so push and reload always agree
        private void PushStructuralEdit(StructuralEdit edit)
        {
            if (!edit.Handling.IsValid) return;
            UndoHistory history = GetOrCreateHistory(edit.Handling);
            history.UndoStack.Add(new UndoEntry { Edit = edit, GroupId = activeUndoGroupId });
            history.RedoStack.Clear();
            StampLastKnownVehicle(history, edit.Handling);
            activeHandlingKey = edit.Handling;
            hasActiveHandling = true;
            MarkAutosavePending(edit.Handling);
        }

        private void PushStructuralEditForContext(EditorContext context, StructuralEdit edit)
        {
            Vehicle vehicle = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            edit.Handling = ResolveHandlingKey(vehicle);
            PushStructuralEdit(edit);
        }

        // The last group id issued, and the group the entries pushed right now belong to (0 outside a conflict group cascade)
        private int lastUndoGroupId;
        private int activeUndoGroupId;

        // The clicked flag's own entry is already on the stack when its conflict group handler runs, so it joins the group
        // Every sibling entry the cascade pushes after it joins too
        private void BeginUndoGroup(string clickedFlagName)
        {
            if (!hasActiveHandling) return;
            List<UndoEntry> undoStack = GetOrCreateHistory(activeHandlingKey).UndoStack;
            if (undoStack.Count == 0) return;
            UndoEntry clickedEntry = undoStack[undoStack.Count - 1];
            if (clickedEntry.GroupId != 0 || !clickedEntry.Edit.IsForFlag(clickedFlagName)) return;
            activeUndoGroupId = ++lastUndoGroupId;
            clickedEntry.GroupId = activeUndoGroupId;
        }

        // For a run that pushes every one of its entries itself, so there is no clicked entry to stamp
        private void BeginUndoGroup() { activeUndoGroupId = ++lastUndoGroupId; }
        private void EndUndoGroup() { activeUndoGroupId = 0; }

        // =========================
        // Undo/redo focus and flash
        // =========================
        // Maps every undoable item to the key its own undo entries carry, so a successful Undo or Redo can highlight and flash the row it just changed
        // Rebuilt on every load, since registration runs at wire time as each tree builds
        // The keys come from each entry's own persisted data, so an entry restored from disk after a reload still finds its freshly built row
        // One key can hold several items, one per editor tree
        private Dictionary<string, List<NativeItem>> undoFocusItems = new Dictionary<string, List<NativeItem>>();

        private void RegisterUndoFocusItem(string focusKey, NativeItem item)
        {
            List<NativeItem> items = GetOrAdd(undoFocusItems, focusKey);
            items.Add(item);
        }

        private List<NativeItem> ResolveLifecycleFocusItems(StructuralEdit edit, bool redo)
        {
            var items = new List<NativeItem>();
            // A tree not built yet has no rows to highlight
            foreach (EditorContext context in editorContexts) { if (context.BuildState == EditorBuildState.Built) edit.AddLifecycleFocusItems(context, redo, items); }
            return items;
        }

        // Skipped while the entry has never been listed, since it has no row yet
        private static void AddAdvancedDataEntryFocusItem(List<NativeItem> items, AdvancedDataItemSet[] slots, int slotIndex)
        {
            if (slots != null && slotIndex >= 0 && slotIndex < slots.Length && slots[slotIndex] != null && slots[slotIndex].SubmenuItem != null) items.Add(slots[slotIndex].SubmenuItem);
        }

        private List<NativeItem> ResolveUndoFocusItems(UndoEntry entry, bool redo)
        {
            List<NativeItem> items;
            StructuralEdit edit = entry.Edit;
            string focusKey = edit.FocusKey;
            if (focusKey != null && undoFocusItems.TryGetValue(focusKey, out items)) return items;
            return ResolveLifecycleFocusItems(edit, redo);
        }

        // No candidate on screen means no jump, so an undo fired from a different menu never pulls the player somewhere else
        private void FocusUndoTarget(UndoEntry entry, bool redo)
        {
            foreach (NativeItem item in ResolveUndoFocusItems(entry, redo))
            {
                if (item == null) continue;
                for (int i = 0; i < allMenus.Length; i++)
                {
                    NativeMenu menu = allMenus[i];
                    if (!menu.Visible || !menu.Items.Contains(item)) continue;
                    if (menu.SelectedItem != item) menu.SelectedIndex = menu.Items.IndexOf(item);
                    StartResultFlash(item, Color.LightGreen);
                    return;
                }
            }
        }

        // The apply becomes one entry in the same ordered history as every edit, so edits before it stay individually undoable
        private void PushHandlingApply(HandlingKey handling, XDocument beforeSnapshot)
        {
            // The handling is applied just before this push, within the same tick, so the autosave on the next tick's flush holds the applied values
            PushStructuralEdit(new HandlingApplyEdit(beforeSnapshot, null) { Handling = handling });
        }

        // ==========================================
        // Held Left/Right cycle batching (Undo/Redo)
        // ==========================================
        // A held Left or Right batches into one edit holding the value before the hold and the value after, instead of one entry per repeat step
        // Activated (direct keyboard entry) is never batched, only the ItemChanged repeat path
        // One edit is pending at a time, a field edit or an element edit alike, so entries reach a history in the order their edits happened
        private ValueEdit pendingCycleEdit;
        private string pendingCycleKey;

        // key is a field key, or an element edit's focus key, which carries the category so 2 categories that share a tag never share a batch
        private void RecordCycleEntry(string key, ValueEdit edit)
        {
            if (pendingCycleEdit != null && (!pendingCycleEdit.Handling.Equals(edit.Handling) || pendingCycleKey != key)) FlushPendingCycleEdit();
            if (pendingCycleEdit == null)
            {
                pendingCycleEdit = edit;
                pendingCycleKey = key;
                return;
            }
            pendingCycleEdit.NewValue = edit.NewValue;
        }

        private void RecordCycleUndo(HandlingKey handling, string fieldKey, string oldValueText, string newValueText)
        {
            RecordCycleEntry(fieldKey, new FieldEdit(fieldKey, oldValueText, newValueText) { Handling = handling });
        }

        // A non-null owner stamps the handling of the vehicle that tree edits when the step is recorded
        // A held cycle then stays filed under the handling whose element it edited even if the seat changes mid-hold
        // A null owner means the caller already stamped the record's Handling
        private void RecordStructuralCycleEdit(ValueEdit edit, EditorContext owner)
        {
            if (owner != null)
            {
                if (Entity.IsNullOrNotExisting(owner.TargetVehicle)) return;
                edit.Handling = ResolveHandlingKey(owner.TargetVehicle);
            }
            RecordCycleEntry(edit.FocusKey, edit);
        }

        private void FlushPendingCycleEdit()
        {
            ValueEdit edit = pendingCycleEdit;
            if (edit == null) return;
            pendingCycleEdit = null;
            PushStructuralEdit(edit);
        }

        // Every reader of the history flushes first, so a batch pending at menu close is pushed before anything reads it
        private void FlushAllPendingCycleBatches()
        {
            FlushPendingCycleEdit();
            FlushPendingSpawnRefresh();
        }

        // The vehicle is returned too, so the caller can refresh the acting tree's display without resolving it a second time
        // Exiting to on-foot keeps the active handling, and only entering a vehicle on a different handling changes it. Returns null when there is nothing to act on
        // context is the Active editor when Saved Handlings' hotkeys fire, since no tree is visible there, and the visible tree for every other entry point
        private HandlingData ResolveUndoTargetHandling(EditorContext context, out Vehicle resolvedVehicle)
        {
            Vehicle current = context.TargetVehicle;
            HandlingKey currentKey = ResolveHandlingKey(current);
            bool currentExists = currentKey.IsValid;
            if (currentExists)
            {
                activeHandlingKey = currentKey;
                hasActiveHandling = true;
            }
            if (!hasActiveHandling)
            {
                resolvedVehicle = null;
                return null;
            }
            // activeHandlingKey was just set to current's handling key above whenever currentExists is True, so this is the same live vehicle, not a cross-check
            if (currentExists)
            {
                resolvedVehicle = current;
                return current.HandlingData;
            }
            UndoHistory history;
            if (undoHistories.TryGetValue(activeHandlingKey, out history) && !Entity.IsNullOrNotExisting(history.LastKnownVehicle) && ResolveHandlingKey(history.LastKnownVehicle).Equals(activeHandlingKey))
            {
                resolvedVehicle = history.LastKnownVehicle;
                HandlingData cachedHandling = history.LastKnownVehicle.HandlingData;
                return cachedHandling.IsValid ? cachedHandling : null;
            }
            resolvedVehicle = null;
            return null;
        }

        // Returns False when the key resolves to nothing, the text does not fit the field, or the vehicle lacks the field's category
        // reason is set on every False return, and replayFlags collects the follow-up work the applied value asks for
        private bool ApplyUndoEntry(Vehicle vehicle, string fieldKey, string valueText, ref ReplayFlags replayFlags, out string reason)
        {
            Func<Vehicle, string, ApplyResult> apply;
            if (!fieldRegistry.TryGetValue(fieldKey, out apply))
            {
                // An extra editor registers its keys only once its tree is built, and a history restored after a reload can hold them before the warm-up reaches it
                EditorContext owner = ResolveEditorContextOfKeySuffix(fieldKey);
                if (owner == null || !EnsureEditorBuilt(owner) || !fieldRegistry.TryGetValue(fieldKey, out apply))
                {
                    reason = "field not registered";
                    return false;
                }
            }
            ApplyResult result = apply(vehicle, valueText);
            if (result.Outcome != ApplyOutcome.Applied)
            {
                reason = result.Outcome == ApplyOutcome.CategoryAbsent ? "category absent on this vehicle" : "value does not fit this field";
                return false;
            }
            // Putting a still-pending cycled field back to its starting value needs no respawn
            if (RevertsPendingCycleRespawn(fieldKey, valueText)) result.Flags &= ~ReplayFlags.Respawn;
            replayFlags |= result.Flags;
            reason = null;
            return true;
        }

        // The extra editor whose key suffix ends fieldKey, or null for one of Current's bare keys
        private EditorContext ResolveEditorContextOfKeySuffix(string fieldKey)
        {
            for (int i = 1; i < editorContexts.Length; i++) { if (fieldKey.EndsWith(editorContexts[i].KeySuffix, StringComparison.Ordinal)) return editorContexts[i]; }
            return null;
        }

        // The capture is exact: Every later entry has already been undone before an apply entry is reached, so the live state is the state right after that apply
        // A failed apply leaves both slots as they were, so a retry still has the same snapshot to apply. The caller logs the returned reason once
        private bool ApplyHandlingApplyEntry(HandlingApplyEdit edit, bool redo, HandlingData handling, Vehicle vehicle, ref ReplayFlags replayFlags, out string reason)
        {
            edit.FieldsSkipped = 0;
            string label = redo ? "(redo snapshot)" : "(pre-apply snapshot)";
            XDocument target = redo ? edit.RedoSnapshot : edit.UndoSnapshot;
            if (target == null)
            {
                reason = "no snapshot";
                return false;
            }
            HandlingFieldGates gates = ResolveHandlingFieldGates(vehicle);
            XDocument capture = handlingMetaXml.BuildHandlingXml(handling, label, gates, ExportOverlay.None);
            int fieldsInvalid, fieldsAbsent;
            var createdCategories = new List<string>();
            try { ApplyHandlingDocument(target, handling, vehicle, gates, LiveRequestOrigin.History, ref replayFlags, out fieldsInvalid, out fieldsAbsent, createdCategories); }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
            if (redo) edit.UndoSnapshot = capture;
            else edit.RedoSnapshot = capture;
            edit.FieldsSkipped = fieldsInvalid + fieldsAbsent;
            // The snapshot an undo applies is the whole handling as it stood before the apply, and every later entry is already undone
            // So a category the handling holds that the snapshot lacks is one the apply created, and the undo removes it
            if (!redo) edit.FieldsSkipped += RemoveCategoriesCreatedByApply(handling, vehicle, createdCategories, LiveRequestOrigin.History);
            if (edit.FieldsSkipped > 0) logger.Log(Dbg.Level.Warn, Dbg.HandlingAppliedPartially(label, fieldsInvalid, fieldsAbsent));
            reason = null;
            return true;
        }

        private static string DescribeUndoEntry(UndoEntry entry)
        {
            return entry.Edit.Description;
        }

        // Presence is read at the moment of the press, so removing a live category later makes its export-only entries apply again
        private bool IsObsoleteExportOnlyEntry(UndoEntry entry, HandlingData handling)
        {
            string category = entry.Edit.ExportOnlyCategory;
            return category != null && IsCategoryPresent(handling, category);
        }

        // Moves every obsolete entry at the top of a stack onto the other stack in order, so the press acts on the next real entry
        // A grouped cascade of export-only entries is all obsolete together, so it moves whole
        private void MoveObsoleteEntries(List<UndoEntry> sourceStack, List<UndoEntry> targetStack, HandlingData handling)
        {
            while (sourceStack.Count > 0 && IsObsoleteExportOnlyEntry(sourceStack[sourceStack.Count - 1], handling))
            {
                UndoEntry skipped = sourceStack[sourceStack.Count - 1];
                sourceStack.RemoveAt(sourceStack.Count - 1);
                targetStack.Add(skipped);
                logger.Log(Dbg.Level.Info, Dbg.UndoEntrySkipped(DescribeUndoEntry(skipped), skipped.Edit.ExportOnlyCategory));
            }
        }

        // What differs between an Undo step and a Redo step: The toasts and log lines of each outcome, and which member of a group takes the focus
        // Every member is a const or a static method, so the 2 instances below are safe to build in a static initializer across the partial files
        private sealed class UndoStepMessages
        {
            // Names the step in the replay's own log line
            public string Trigger;
            public string NoVehicleKey;
            public string NoVehicleToast;
            public string EmptyKey;
            public string EmptyToast;
            public string FailedToast;
            public string DoneToast;
            public string ApplyToast;
            public string ApplySkippedToast;
            public Func<string> WaitingLog;
            public Func<string> EmptyLog;
            public Func<string> AppliedHandlingLog;
            public Func<string, string, string> FailedLog;
            public Func<string, string> AppliedLog;
            // Undo pops the clicked flag's own entry last of its group, so the focus follows the last member applied. Redo pops it first and keeps the focus on it
            public bool FocusFollowsLastMember;
        }

        private static readonly UndoStepMessages UndoMessages = new UndoStepMessages
        {
            Trigger = "Undo",
            NoVehicleKey = NOTIFICATION_KEY_NO_VEHICLE_UNDO,
            NoVehicleToast = NOTIF_NO_VEHICLE_UNDO,
            EmptyKey = NOTIFICATION_KEY_UNDO_EMPTY,
            EmptyToast = NOTIF_NOTHING_TO_UNDO,
            FailedToast = NOTIF_UNDO_FAILED,
            DoneToast = NOTIF_UNDO_DONE,
            ApplyToast = NOTIF_UNDO_APPLY,
            ApplySkippedToast = NOTIF_UNDO_APPLY_SKIPPED,
            WaitingLog = Dbg.UndoWaitingForLiveRequest,
            EmptyLog = Dbg.UndoNothingToApply,
            AppliedHandlingLog = Dbg.AppliedHandlingUndone,
            FailedLog = Dbg.UndoEntryFailed,
            AppliedLog = Dbg.UndoApplied,
            FocusFollowsLastMember = true
        };

        private static readonly UndoStepMessages RedoMessages = new UndoStepMessages
        {
            Trigger = "Redo",
            NoVehicleKey = NOTIFICATION_KEY_NO_VEHICLE_REDO,
            NoVehicleToast = NOTIF_NO_VEHICLE_REDO,
            EmptyKey = NOTIFICATION_KEY_REDO_EMPTY,
            EmptyToast = NOTIF_NOTHING_TO_REDO,
            FailedToast = NOTIF_REDO_FAILED,
            DoneToast = NOTIF_REDO_DONE,
            ApplyToast = NOTIF_REDO_APPLY,
            ApplySkippedToast = NOTIF_REDO_APPLY_SKIPPED,
            WaitingLog = Dbg.RedoWaitingForLiveRequest,
            EmptyLog = Dbg.RedoNothingToApply,
            AppliedHandlingLog = Dbg.AppliedHandlingRedone,
            FailedLog = Dbg.RedoEntryFailed,
            AppliedLog = Dbg.RedoApplied,
            FocusFollowsLastMember = false
        };

        // The entry leaves its stack before its apply and always lands on the other one, since an apply that throws would otherwise discard it from both
        // A throw is treated as a failed apply, so the entry stays recoverable
        private UndoEntry ApplyTopEntry(List<UndoEntry> sourceStack, List<UndoEntry> targetStack, bool redo, Vehicle vehicle, HandlingData handling, ref ReplayFlags replayFlags, out bool applied, out string reason)
        {
            UndoEntry entry = sourceStack[sourceStack.Count - 1];
            sourceStack.RemoveAt(sourceStack.Count - 1);
            try { applied = entry.Edit.Apply(this, redo, vehicle, handling, ref replayFlags, out reason); }
            catch (Exception ex)
            {
                applied = false;
                reason = ex.Message;
            }
            targetStack.Add(entry);
            return entry;
        }

        // An empty stack still reports, since silence there reads as a broken hotkey
        // context threads through vehicle resolution and the display sync, so Saved Handlings' hotkeys behave like every other entry point
        // What a step can revert never depends on the open menu
        // The result is posted after the display has synced, so the sync-then-notify ordering is identical for Undo and Redo
        private void StepUndoHistory(EditorContext context, bool redo)
        {
            FlushAllPendingCycleBatches();
            UndoStepMessages messages = redo ? RedoMessages : UndoMessages;
            Vehicle vehicle;
            HandlingData handling = ResolveUndoTargetHandling(context, out vehicle);
            if (handling == null)
            {
                notifications.PostNotificationOnce(messages.NoVehicleKey, messages.NoVehicleToast);
                return;
            }
            if (HasPendingLiveRequest(activeHandlingKey))
            {
                logger.Log(Dbg.Level.Info, messages.WaitingLog());
                return;
            }
            UndoHistory history = GetOrCreateHistory(activeHandlingKey);
            List<UndoEntry> sourceStack = redo ? history.RedoStack : history.UndoStack;
            List<UndoEntry> targetStack = redo ? history.UndoStack : history.RedoStack;
            MoveObsoleteEntries(sourceStack, targetStack, handling);
            if (sourceStack.Count == 0)
            {
                logger.Log(Dbg.Level.Info, messages.EmptyLog());
                notifications.PostNotificationOnce(messages.EmptyKey, messages.EmptyToast);
                return;
            }
            ReplayFlags replayFlags = ReplayFlags.None;
            string reason;
            bool applied;
            UndoEntry entry = ApplyTopEntry(sourceStack, targetStack, redo, vehicle, handling, ref replayFlags, out applied, out reason);
            // The rest of a grouped entry's group moves with it
            UndoEntry focusEntry = entry;
            bool groupMemberFailed = false;
            while (entry.GroupId != 0 && sourceStack.Count > 0 && sourceStack[sourceStack.Count - 1].GroupId == entry.GroupId)
            {
                string memberReason;
                bool memberApplied;
                UndoEntry member = ApplyTopEntry(sourceStack, targetStack, redo, vehicle, handling, ref replayFlags, out memberApplied, out memberReason);
                if (!memberApplied)
                {
                    logger.Log(Dbg.Level.Warn, messages.FailedLog(DescribeUndoEntry(member), memberReason));
                    groupMemberFailed = true;
                }
                if (messages.FocusFollowsLastMember) focusEntry = member;
            }
            MarkAutosavePending(activeHandlingKey);
            ConsumeReplayRefreshAndRespawn(vehicle, messages.Trigger, replayFlags);
            SyncEditorContext(context);
            RecalculateVisibleMenuWidth(handling);
            if (!applied)
            {
                logger.Log(Dbg.Level.Warn, messages.FailedLog(DescribeUndoEntry(entry), reason));
                notifications.PostNotificationOnce(NOTIFICATION_KEY_UNDO_UNAPPLIED, messages.FailedToast);
                return;
            }
            if (groupMemberFailed)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_UNDO_UNAPPLIED, messages.FailedToast);
                return;
            }
            if (entry.Edit.FinishesLater || entry.Edit.ReportApplyResult(this, messages)) return;
            notifications.Notify(NOTIFICATION_KEY_UNDO_REDO, messages.DoneToast);
            logger.Log(Dbg.Level.Info, messages.AppliedLog(focusEntry.Edit.Label));
            FocusUndoTarget(focusEntry, redo);
        }

        // ======================================
        // Undo All: The baseline and its restore
        // ======================================
        // Runs when a vehicle becomes an editor's target, which always precedes any editor change, so a history's first entry always finds its baseline
        // It captures only while the history has no baseline and no entries, and a history that already has entries with none can never get one
        private void CaptureUndoAllBaseline(Vehicle vehicle)
        {
            HandlingKey key = ResolveHandlingKey(vehicle);
            if (!key.IsValid || !vehicle.HandlingData.IsValid) return;
            UndoHistory history = GetOrCreateHistory(key);
            if (history.Baseline != null) return;
            if (history.UndoStack.Count > 0 || history.RedoStack.Count > 0)
            {
                if (history.BaselineMissingLogged) return;
                history.BaselineMissingLogged = true;
                logger.Log(Dbg.Level.Warn, Dbg.UndoAllBaselineMissing(modelNames.DescribeHandling(key)));
                return;
            }
            // Every gate is open, so a model without rocket boost still keeps those fields for a shared handling's other models
            history.Baseline = handlingMetaXml.BuildHandlingXml(vehicle.HandlingData, modelNames.ResolveHandlingName(vehicle), new HandlingFieldGates { RocketBoost = true, RearMountedTurret = true }, ExportOverlay.None);
            logger.Log(Dbg.Level.Info, Dbg.UndoAllBaselineCaptured(modelNames.DescribeHandling(key)));
        }

        // Every edit of a handling pushes a history entry, so its export-only state before the first entry is empty
        // Each toggle goes through SetSubDataToggle, which also forgets the category's scroll position
        private void ClearPhantomStateForHandling(HandlingKey key)
        {
            HashSet<string> toggled;
            if (pendingSubDataToggle.TryGetValue(key, out toggled))
            {
                foreach (string category in new List<string>(toggled)) SetSubDataToggle(key, category, false);
            }
            pendingSubDataElements.Remove(key);
            adoptedPhantomCategories.Remove(key);
            pendingAdvancedData.Remove(key);
        }

        private bool HasPhantomStateForHandling(HandlingKey key)
        {
            HashSet<string> toggled;
            Dictionary<string, XElement> elements;
            HashSet<string> adopted;
            if (pendingSubDataToggle.TryGetValue(key, out toggled) && toggled.Count > 0) return true;
            if (pendingSubDataElements.TryGetValue(key, out elements) && elements.Count > 0) return true;
            if (adoptedPhantomCategories.TryGetValue(key, out adopted) && adopted.Count > 0) return true;
            return pendingAdvancedData.ContainsKey(key);
        }

        // One Undo All, kept between frames while live requests it queued wait on the plugin
        // Every request of the run carries it, so each outcome lands in its failures, respawn flags, and not reverted count
        private class UndoAllRun
        {
            public EditorContext Context;
            public HandlingKey Key;
            public HandlingData Handling;
            public Vehicle Vehicle;
            public int EntryCount;
            public ReplayFlags Flags;
            public int NotReverted;
            public List<KeyValuePair<string, string>> Failures = new List<KeyValuePair<string, string>>();
        }

        // Set while an Undo All waits on live requests, which the live SubData poll resumes once none is pending on the handling
        private UndoAllRun pausedUndoAllRun;

        // Set only while the baseline apply runs, so the requests it queues join the run
        private UndoAllRun undoAllRunInProgress;

        // Not routed through StepUndoHistory, since reverting everything at once has no one-step-at-a-time shape
        // It jumps to the end state: The handling is restored from its baseline, so a category that starts and ends absent is never touched
        private void UndoAllEdits(EditorContext context)
        {
            FlushAllPendingCycleBatches();
            if (pausedUndoAllRun != null)
            {
                logger.Log(Dbg.Level.Info, Dbg.UndoAllStillWaiting(CountPendingLiveRequests(pausedUndoAllRun.Key)));
                return;
            }
            Vehicle undoVehicle;
            HandlingData handling = ResolveUndoTargetHandling(context, out undoVehicle);
            if (handling == null)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_UNDO, NOTIF_NO_VEHICLE_UNDO);
                return;
            }
            if (HasPendingLiveRequest(activeHandlingKey))
            {
                logger.Log(Dbg.Level.Info, Dbg.UndoAllWaitingForLiveRequest());
                return;
            }
            UndoHistory history = GetOrCreateHistory(activeHandlingKey);
            if (history.UndoStack.Count == 0)
            {
                logger.Log(Dbg.Level.Info, Dbg.UndoNothingToApply());
                notifications.PostNotificationOnce(NOTIFICATION_KEY_UNDO_EMPTY, NOTIF_NOTHING_TO_UNDO);
                return;
            }
            if (history.Baseline == null)
            {
                logger.Log(Dbg.Level.Warn, Dbg.UndoAllBaselineMissing(modelNames.DescribeHandling(activeHandlingKey)));
                notifications.PostNotificationOnce(NOTIFICATION_KEY_UNDO_UNAPPLIED, NOTIF_UNDO_FAILED);
                return;
            }
            // Entries that cancel out, such as a category created then deleted, leave the handling where it started, so the press has nothing to revert
            if (!HasPhantomStateForHandling(activeHandlingKey) && handlingMetaXml.BuildHandlingXml(handling, modelNames.ResolveHandlingName(undoVehicle), new HandlingFieldGates { RocketBoost = true, RearMountedTurret = true }, ExportOverlay.None).ToString() == history.Baseline.ToString())
            {
                logger.Log(Dbg.Level.Info, Dbg.UndoAllAtBaseline(history.UndoStack.Count));
                history.UndoStack.Clear();
                history.RedoStack.Clear();
                notifications.PostNotificationOnce(NOTIFICATION_KEY_UNDO_EMPTY, NOTIF_NOTHING_TO_UNDO);
                return;
            }
            var run = new UndoAllRun { Context = context, Key = activeHandlingKey, Handling = handling, Vehicle = undoVehicle, EntryCount = history.UndoStack.Count };
            history.UndoStack.Clear();
            history.RedoStack.Clear();
            ClearPhantomStateForHandling(run.Key);
            var presentCategoriesNotCarried = new List<string>();
            int fieldsInvalid = 0;
            int fieldsAbsent = 0;
            undoAllRunInProgress = run;
            try
            {
                ApplyHandlingDocument(history.Baseline, handling, undoVehicle, ResolveHandlingFieldGates(undoVehicle), LiveRequestOrigin.UndoAll, ref run.Flags, out fieldsInvalid, out fieldsAbsent, presentCategoriesNotCarried);
                int notRemoved = RemoveCategoriesCreatedByApply(handling, undoVehicle, presentCategoriesNotCarried, LiveRequestOrigin.UndoAll);
                if (notRemoved > 0)
                {
                    run.NotReverted += notRemoved;
                    run.Failures.Add(new KeyValuePair<string, string>("Categories the baseline lacks", notRemoved + " could not be removed"));
                }
            }
            catch (Exception ex) { AddUndoAllFailure(run, "Baseline apply", ex.Message); }
            finally { undoAllRunInProgress = null; }
            if (fieldsInvalid + fieldsAbsent > 0)
            {
                run.NotReverted += fieldsInvalid + fieldsAbsent;
                run.Failures.Add(new KeyValuePair<string, string>("Baseline fields", (fieldsInvalid + fieldsAbsent) + " could not be written"));
            }
            logger.Log(Dbg.Level.Info, Dbg.UndoAllApplied(run.EntryCount));
            if (HasPendingLiveRequest(run.Key))
            {
                pausedUndoAllRun = run;
                logger.Log(Dbg.Level.Info, Dbg.UndoAllPaused(CountPendingLiveRequests(run.Key)));
                return;
            }
            FinishUndoAll(run);
        }

        // A request that failed or timed out still resumes, so the run reports it with the rest instead of waiting forever
        private void ResumeUndoAll()
        {
            UndoAllRun run = pausedUndoAllRun;
            pausedUndoAllRun = null;
            logger.Log(Dbg.Level.Info, Dbg.UndoAllResumed());
            if (Entity.IsNullOrNotExisting(run.Vehicle)) run.Vehicle = ResolveLiveVehicleOfHandling(run.Key);
            FinishUndoAll(run);
        }

        // One respawn and one refresh for the whole run, however many categories it created or removed
        private void FinishUndoAll(UndoAllRun run)
        {
            MarkAutosavePending(run.Key);
            if (!Entity.IsNullOrNotExisting(run.Vehicle)) ConsumeReplayRefreshAndRespawn(run.Vehicle, "UndoAll", run.Flags);
            SyncEditorContext(run.Context);
            RecalculateVisibleMenuWidth(run.Handling);
            logger.Log(Dbg.Level.Info, Dbg.UndoAllFinished(run.EntryCount, run.NotReverted));
            if (run.NotReverted > 0)
            {
                logger.Log(Dbg.Level.Warn, Dbg.UndoAllFailuresHeader());
                for (int i = 0; i < run.Failures.Count; i++) logger.Log(Dbg.Level.Warn, Dbg.UndoAllFailureEntry(run.Failures[i].Key, run.Failures[i].Value));
            }
            notifications.Notify(NOTIFICATION_KEY_UNDO_REDO, run.NotReverted > 0 ? string.Format(NOTIF_UNDO_ALL_PARTIAL, run.NotReverted) : NOTIF_UNDO_ALL_DONE);
        }

        // ==========================
        // Persistence across reloads
        // ==========================
        // Every history line is one shape: The handling's name hash as 8 hex digits, the stack word, the group id, the kind, then the family's own payload
        // The Undo All baseline line is the only other shape, since it is no entry
        private static void AppendUndoEntryLine(StringBuilder sb, string handlingHash, string stack, UndoEntry entry)
        {
            sb.Append(handlingHash).Append('|').Append(stack).Append('|').Append(entry.GroupId).Append('|').Append(entry.Edit.Kind);
            entry.Edit.WritePayload(sb);
            sb.AppendLine();
        }

        // The caller has already checked that the kind has a parser
        private static UndoEntry ParseStructuralLine(string[] parts, HandlingKey handling)
        {
            int groupId;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out groupId)) return null;
            StructuralEdit edit = StructuralEditParsers[parts[3]](parts[3], parts);
            if (edit == null) return null;
            edit.Handling = handling;
            return new UndoEntry { Edit = edit, GroupId = groupId };
        }

        // One line per stack entry, oldest first (stack order), so the consumer's in-order Add per line rebuilds the same order
        // A handling whose name hash does not verify is left out
        private void WriteUndoHistory(StringBuilder sb)
        {
            foreach (KeyValuePair<HandlingKey, UndoHistory> handlingHistory in undoHistories)
            {
                string handlingHash = reloadState.FormatReloadStateHandlingHash(handlingHistory.Key);
                if (handlingHash == null) continue;
                bool hasEntries = handlingHistory.Value.UndoStack.Count > 0 || handlingHistory.Value.RedoStack.Count > 0;
                if (hasEntries && handlingHistory.Value.Baseline != null) sb.Append(handlingHash).Append('|').Append(BASELINE_LINE_TAG).Append('|').Append(EncodeSnapshot(handlingHistory.Value.Baseline)).AppendLine();
                foreach (UndoEntry entry in handlingHistory.Value.UndoStack) AppendUndoEntryLine(sb, handlingHash, UNDO_LINE_TAG, entry);
                foreach (UndoEntry entry in handlingHistory.Value.RedoStack) AppendUndoEntryLine(sb, handlingHash, REDO_LINE_TAG, entry);
            }
        }

        private bool ConsumeUndoHistoryLine(string raw)
        {
            string[] parts = raw.Split('|');
            HandlingKey handling;
            if (parts.Length == UNDO_BASELINE_LINE_FIELD_COUNT && parts[1] == BASELINE_LINE_TAG)
            {
                if (!reloadState.ResolveReloadStateHandling(ReloadStateSection.UndoHistory, parts[0], out handling)) return false;
                try
                {
                    GetOrCreateHistory(handling).Baseline = DecodeSnapshot(parts[2]);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.ReloadStateSectionFailed(ReloadStateSection.UndoHistory.ToString(), ex.Message));
                    return false;
                }
            }
            if (parts.Length >= STRUCTURAL_EDIT_PAYLOAD_START && (parts[1] == UNDO_LINE_TAG || parts[1] == REDO_LINE_TAG))
            {
                if (!reloadState.ResolveReloadStateHandling(ReloadStateSection.UndoHistory, parts[0], out handling)) return false;
                if (!StructuralEditParsers.ContainsKey(parts[3]))
                {
                    logger.Log(Dbg.Level.Warn, Dbg.UndoHistoryKindUnknown(parts[3]));
                    return false;
                }
                UndoEntry loadedEntry = ParseStructuralLine(parts, handling);
                if (loadedEntry == null)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.UndoHistoryLineUnrecognized(raw.Length));
                    return false;
                }
                // A new group must never reuse an id a loaded entry already carries, or Undo would merge the 2 groups
                lastUndoGroupId = Math.Max(lastUndoGroupId, loadedEntry.GroupId);
                UndoHistory loadedHistory = GetOrCreateHistory(handling);
                if (parts[1] == UNDO_LINE_TAG) loadedHistory.UndoStack.Add(loadedEntry);
                else loadedHistory.RedoStack.Add(loadedEntry);
                return true;
            }
            logger.Log(Dbg.Level.Warn, Dbg.UndoHistoryLineUnrecognized(raw.Length));
            return false;
        }
    }
}
