using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // A weapon slot row's array tag -> the live fieldRegistry's field name for the name table rows. Every other slot row takes its table row's title
        private static readonly Dictionary<string, string> SelectiveCopyWeaponFieldNameMap = new Dictionary<string, string>
        {
            { WEAPON_HASH_ARRAY_TAG, "WeaponHash" }, { WEAPON_SEAT_ARRAY_TAG, "WeaponSeats" }, { WEAPON_MOD_TYPE_ARRAY_TAG, "WeaponVehicleModType" },
        };

        // Guards against re-wiring a pooled item's Activated handler on a later refresh that reuses the same pool slot for a different field
        // One set serves both category-row pooling (WireSelectiveCopy) and weapon-slot pooling (WireSelectiveCopyWeaponSlot)
        // The two pools hold different NativeItem objects (separate ItemPool lists), so no cross-pool collision is possible
        private HashSet<NativeItem> selectiveCopyWired = new HashSet<NativeItem>();
        // True on the tick a Preview submenu row was activated to descend into its child (never on a plain field-row activation). The API raises a menu's
        // ItemActivated before every item's own Activated and reads FrontendAccept's "just pressed" flag for the whole frame, so the child menu shown by that
        // descent can fire its own index-0 field row's Activated on the same keypress. Each copy bare-returns on it
        private bool DescentHappenedThisTick() { return navigation.DescentTick == uptimeTicks; }

        // Every outcome past Identical is a failure. Failed is an error the log explains, and each other one is a reason the toast names itself
        private enum SelectiveCopyOutcome { Applied, Identical, Failed, CategoryAbsent, EntryAbsent, RocketBoostAbsent, TurretAbsent }

        // What every copy of one run shares: The resolved target, and the follow-up work its writes ask for
        // A single row finishes its own run, while a category copy finishes one run after every row so the vehicle respawns or refreshes once
        private class SelectiveCopyRun
        {
            public EditorContext Target;
            public Vehicle Vehicle;
            public HandlingData Handling;
            public HandlingKey HandlingKey;
            public ReplayFlags Flags;
            // True once a write reached the live vehicle, since only those writes need the replay, the sync, and the width recalculation
            public bool LiveWritten;
            // The categories this run created because the vehicle lacked them, with the element each copied value lands in
            // Their writes push no undo entry of their own, since the creation's entry is the one step
            public readonly Dictionary<string, XElement> CreatedCategories = new Dictionary<string, XElement>();
            // The created categories the plugin makes live once the rows are written, so the creation applies the copied element
            public readonly List<string> LiveRouteCategories = new List<string>();
        }

        // The reason a category where nothing applied names: A missing category once any row reports one, since it explains the rest
        // Otherwise the failure every skipped row shares, and Failed once 2 rows disagree
        private class SelectiveCopyTally
        {
            public int Applied;
            public int Identical;
            public int Skipped;
            public SelectiveCopyOutcome SharedFailure = SelectiveCopyOutcome.Failed;
        }

        // Null when there is no Active vehicle, after the toast and the red flash on flashItem
        private SelectiveCopyRun BeginSelectiveCopyRun(NativeItem flashItem)
        {
            EditorContext target = activeContext;
            Vehicle vehicle = target.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_COPY, NOTIF_NO_VEHICLE_COPY);
                StartResultFlash(flashItem, Color.Red);
                return null;
            }
            return new SelectiveCopyRun { Target = target, Vehicle = vehicle, Handling = vehicle.HandlingData, HandlingKey = ResolveHandlingKey(vehicle) };
        }

        // A category the vehicle lacks is created first, live when the allow list accepts it and otherwise as an export-only category
        // Returns False for a category the vehicle cannot hold at all, and the copy then reports it missing
        private bool EnsureCopyCategoryExists(SelectiveCopyRun run, string category)
        {
            if (run.CreatedCategories.ContainsKey(category)) return true;
            if (IsCategoryPresent(run.Handling, category)) return false;
            if (Array.IndexOf(GetSubDataCreationCandidates(run.Vehicle.Type, run.Vehicle.ClassType), category) < 0) return false;
            if (IsLiveCreationOffered(category, run.Vehicle.Type) && FindPendingLiveRequest(run.HandlingKey, LiveRequestKind.Creation, category) == null)
            {
                // A fresh default replaces any element stored earlier, so no old values leak into the new category
                CategoryDefinition definition = ResolveCategoryDefinition(category);
                if (definition == null) return false;
                XElement fresh = definition.BuildDefaultElement();
                Dictionary<string, XElement> storedElements = GetOrAdd(pendingSubDataElements, run.HandlingKey);
                storedElements[category] = fresh;
                run.CreatedCategories[category] = fresh;
                run.LiveRouteCategories.Add(category);
                return true;
            }
            ActivatePhantomCategory(run.Target, category);
            XElement exportOnly;
            if (!ResolvePhantomCategory(run.HandlingKey, category, out exportOnly)) return false;
            run.CreatedCategories[category] = exportOnly;
            return true;
        }

        // A live-route category is stored but never toggled, so the run's own record is read before the export-only lookup
        private bool ResolveCopyPhantomElement(SelectiveCopyRun run, string category, out XElement element)
        {
            return run.CreatedCategories.TryGetValue(category, out element) || ResolvePhantomCategory(run.HandlingKey, category, out element);
        }

        private bool ResolveOrCreateCopyElement(SelectiveCopyRun run, string category, out XElement element)
        {
            if (ResolveCopyPhantomElement(run, category, out element)) return true;
            return EnsureCopyCategoryExists(run, category) && ResolveCopyPhantomElement(run, category, out element);
        }

        // Each live-route category is requested once the rows are written, and the plugin's answer applies the copied element and pushes the creation's one undo entry
        // A request the plugin cannot take creates the export-only category from the same element instead
        private void RequestSelectiveCopyCreations(SelectiveCopyRun run)
        {
            foreach (string category in run.LiveRouteCategories)
            {
                if (!BeginLiveCategoryCreation(run.Target, category, run.Vehicle, LiveRequestOrigin.Player)) ActivatePhantomCategory(run.Target, category);
            }
            run.LiveRouteCategories.Clear();
        }

        // A Car this copy created starts with no entries, so the entries up to the copied one are added at the schema's defaults and each row then writes its own value
        private static XElement AddCopyAdvancedDataSlots(XElement carElement, int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= ADVANCEDDATA_COUNT_SANITY_CEILING) return null;
            XElement advancedData = carElement.Element(ADVANCED_DATA_TAG);
            if (advancedData == null)
            {
                advancedData = new XElement(ADVANCED_DATA_TAG);
                carElement.Add(advancedData);
            }
            while (CountItemElements(advancedData) <= slotIndex) advancedData.Add(BuildDefaultAdvancedDataSlot());
            return GetAdvancedDataItem(advancedData, slotIndex);
        }

        // A run that created a category reports the copy as applied, since the creation happened even when every copied value equaled a default
        private static SelectiveCopyOutcome ResolveRunOutcome(SelectiveCopyRun run, SelectiveCopyOutcome outcome)
        {
            return outcome == SelectiveCopyOutcome.Identical && run.CreatedCategories.Count > 0 ? SelectiveCopyOutcome.Applied : outcome;
        }

        // The respawn or refresh acts on the run's own vehicle, since no editor tree is visible when Saved Handlings is the menu on screen
        private void FinishSelectiveCopyRun(SelectiveCopyRun run)
        {
            RequestSelectiveCopyCreations(run);
            if (!run.LiveWritten) return;
            ConsumeReplayRefreshAndRespawn(run.Vehicle, "SelectiveCopy", run.Flags);
            // A respawn replaces the vehicle after the writes pushed their undo entries, so the history's fallback vehicle is stamped again from the live one
            StampLastKnownVehicle(GetOrCreateHistory(run.HandlingKey), run.HandlingKey);
            SyncEditorContext(run.Target);
            RecalculateVisibleMenuWidth(run.Handling);
        }

        private void FinishSelectiveCopyRow(NativeItem item, SelectiveCopyOutcome rowOutcome, SelectiveCopyRun run, string fieldKey)
        {
            SelectiveCopyOutcome outcome = ResolveRunOutcome(run, rowOutcome);
            if (outcome == SelectiveCopyOutcome.Identical)
            {
                SelectiveCopyIdentical(item, fieldKey);
                return;
            }
            if (outcome != SelectiveCopyOutcome.Applied)
            {
                RequestSelectiveCopyCreations(run);
                SelectiveCopyFailed(item, outcome);
                return;
            }
            FinishSelectiveCopyRun(run);
            StartResultFlash(item, Color.LightGreen);
            logger.Log(Dbg.Level.Info, Dbg.SelectiveCopyApplied(fieldKey));
        }

        // The Preview value as the write will hold it, since a value below a crash boundary is stored at the boundary
        // The identical check compares this text, so a row already sitting at the boundary reads as identical
        private static string ApplySelectiveCopyHardBounds(FieldEntry entry, string valueText, bool isInteger)
        {
            if (entry == null) return valueText;
            if (isInteger)
            {
                int parsedInt;
                return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt) ? ApplyHardBounds(entry, parsedInt).ToString(CultureInfo.InvariantCulture) : valueText;
            }
            float parsedFloat;
            return float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedFloat) ? ApplyHardBounds(entry, parsedFloat, 0).ToString("R", CultureInfo.InvariantCulture) : valueText;
        }

        // A missing category, entry, rocket boost, or turret names itself
        // Only a real error (an unresolved field, a malformed value, a refused write) points to the log
        // The no-vehicle guards post their own notification, so a red flash never shows with nothing explaining why
        private void SelectiveCopyFailed(NativeItem item, SelectiveCopyOutcome outcome)
        {
            if (outcome == SelectiveCopyOutcome.CategoryAbsent) notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECTIVE_COPY_CATEGORY_ABSENT, NOTIF_COPY_CATEGORY_ABSENT);
            else if (outcome == SelectiveCopyOutcome.EntryAbsent) notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECTIVE_COPY_ENTRY_ABSENT, NOTIF_COPY_ENTRY_ABSENT);
            else if (outcome == SelectiveCopyOutcome.RocketBoostAbsent) notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECTIVE_COPY_NO_ROCKET_BOOST, NOTIF_COPY_NO_ROCKET_BOOST);
            else if (outcome == SelectiveCopyOutcome.TurretAbsent) notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECTIVE_COPY_NO_TURRET, NOTIF_COPY_NO_REAR_MOUNTED_TURRET);
            else notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECTIVE_COPY_FAILED, NOTIF_COPY_FAILED);
            StartResultFlash(item, Color.Red);
        }

        // An identical value writes nothing, pushes no undo entry, and requests no respawn or refresh
        // Its row flashes yellow with a toast instead of the green success cue
        private void SelectiveCopyIdentical(NativeItem item, string fieldKey)
        {
            notifications.Notify(NOTIFICATION_KEY_SELECTIVE_COPY_IDENTICAL, NOTIF_COPY_IDENTICAL);
            StartResultFlash(item, Color.Yellow);
            logger.Log(Dbg.Level.Info, Dbg.SelectiveCopyIdentical(fieldKey));
        }

        // Every error exit logs the same WARN, and the caller's toast then points to the log
        private SelectiveCopyOutcome FailCopy(string fieldKey)
        {
            logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyFieldUnresolved(fieldKey));
            return SelectiveCopyOutcome.Failed;
        }

        // A value that does not parse logs the failure WARN here, and the caller returns Failed
        private bool ParseCopyFloat(string valueText, string fieldKey, out float parsed)
        {
            if (ParseFiniteFloat(valueText, out parsed)) return true;
            FailCopy(fieldKey);
            return false;
        }

        private bool ParseCopyInt(string valueText, string fieldKey, out int parsed)
        {
            if (int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return true;
            FailCopy(fieldKey);
            return false;
        }

        // Floats compare at the save file's resolution, the same comparison a typed entry and an applied handling use, and ints compare exactly
        // A text that does not parse is never identical, so the write path reports it as the failure it is
        private static bool IsSelectiveCopyValueIdentical(string oldValueText, string newValueText, bool isInteger)
        {
            if (isInteger)
            {
                int oldInt;
                int newInt;
                return int.TryParse(oldValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out oldInt) && int.TryParse(newValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out newInt) && oldInt == newInt;
            }
            float oldFloat;
            float newFloat;
            return float.TryParse(oldValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out oldFloat) && float.TryParse(newValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out newFloat) && SameAtSaveResolution(oldFloat, newFloat);
        }

        // Resolved at copy time, not wire time, since which editor is Active can change between a Preview row being built and being activated
        private string ResolveSelectiveCopyKeySuffix()
        {
            return activeContext.KeySuffix ?? "";
        }

        // The fieldRegistry name of a Preview row's field, built from its ref with the same row titles the live stores register under
        private static string ResolveSelectiveCopyFieldName(FieldRef reference)
        {
            if (reference.Tag == ADVANCED_DATA_TAG) return AdvancedDataRowTitle(reference.Index, reference.Member);
            return ResolveFieldTitle(reference.Category, reference.Tag) + (reference.Component != null ? reference.Component.ToUpperInvariant() : "");
        }

        private static string ResolveSelectiveCopyWeaponFieldName(PreviewField field)
        {
            string fieldName;
            if (SelectiveCopyWeaponFieldNameMap.TryGetValue(field.Ref.Tag, out fieldName)) return fieldName;
            return field.Entry != null ? field.Entry.Title : field.Ref.Tag;
        }

        // AIHandling is a single shared fieldRegistry registration with no per-slot suffix by design
        private string BuildSelectiveCopyFieldKey(PreviewField field)
        {
            string suffix = field.Ref.Tag == "AIHandling" ? "" : ResolveSelectiveCopyKeySuffix();
            return BuildFieldKey(field.Ref.Category, ResolveSelectiveCopyFieldName(field.Ref)) + suffix;
        }

        private string BuildSelectiveCopyWeaponFieldKey(PreviewField field)
        {
            return "Weapon." + ResolveSelectiveCopyWeaponFieldName(field) + "[" + field.Ref.Index + "]" + ResolveSelectiveCopyKeySuffix();
        }

        // Wired once, the first time a pooled Preview field item is constructed
        // The row's field is read from its Tag inside the handler, since a reused pool slot represents a different field on every refresh
        private void WireSelectiveCopy(NativeItem item)
        {
            if (!selectiveCopyWired.Add(item)) return;
            item.Description = DESC_COPY_VALUE;
            SubscribeActivated(item, "WireSelectiveCopy.Activated", () =>
            {
                if (DescentHappenedThisTick()) return;
                PreviewField field = item.Tag as PreviewField;
                if (field == null) return;
                ApplySelectiveCopyField(item, field, BuildSelectiveCopyFieldKey(field));
            });
        }

        private void WireSelectiveCopyWeaponSlot(NativeItem item)
        {
            if (!selectiveCopyWired.Add(item)) return;
            item.Description = DESC_COPY_VALUE;
            SubscribeActivated(item, "WireSelectiveCopyWeaponSlot.Activated", () =>
            {
                if (DescentHappenedThisTick()) return;
                PreviewField field = item.Tag as PreviewField;
                if (field == null) return;
                ApplySelectiveCopyWeaponField(item, field, BuildSelectiveCopyWeaponFieldKey(field));
            });
        }

        // Returns False (nothing to write into) when the category isn't toggled active as an export-only candidate either, matching the absent-category red-flash path
        private bool ResolvePhantomCategory(HandlingKey handlingKey, string category, out XElement phantomElement)
        {
            HashSet<string> toggled;
            if (pendingSubDataToggle.TryGetValue(handlingKey, out toggled) && toggled.Contains(category))
            {
                phantomElement = ResolvePhantomElement(handlingKey, category);
                return phantomElement != null;
            }
            phantomElement = null;
            return false;
        }

        // Reads through the existing binding Getter instead of rebuilding and linear-scanning the whole handling document for one key
        private bool ResolveBoundFieldValue(string fieldKey, HandlingData handling, out string oldValueText)
        {
            FloatFieldBinding floatBinding;
            if (floatBindingByFieldKey.TryGetValue(fieldKey, out floatBinding))
            {
                oldValueText = floatBinding.Getter(handling).ToString("R", CultureInfo.InvariantCulture);
                return true;
            }
            IntFieldBinding intBinding;
            if (intBindingByFieldKey.TryGetValue(fieldKey, out intBinding))
            {
                oldValueText = intBinding.Getter(handling).ToString(CultureInfo.InvariantCulture);
                return true;
            }
            oldValueText = null;
            return false;
        }

        // AIHandling has no HandlingData -> primitive binding at all
        // Its live value only ever exists as a hash repoint (HandlingData.AIHandlingHash), reverse-mapped against the same 4 known names the cycler steps through
        private string ResolveAIHandlingName(HandlingData handling)
        {
            AIHandlingHash hash = handling.AIHandlingHash;
            for (int i = 0; i < AIHandlingNames.Length; i++)
            {
                if ((AIHandlingHash)AIHandlingHashes[i] == hash) return AIHandlingNames[i];
            }
            return AIHandlingNames[0];
        }

        // The 3 weapon cyclers (WeaponHash, WeaponSeat, WeaponVehicleModType) have no HandlingData -> primitive binding either
        // Their live values only exist as whole-array reads
        // The text matches the exact shape each cycler's own PushUndo call records
        private string ResolveWeaponCyclerOldValue(HandlingData handling, string slotTag, int slotIndex)
        {
            if (handling.VehicleWeaponHandlingData == null) return "";
            if (slotTag == WEAPON_HASH_ARRAY_TAG)
            {
                VehicleWeaponHash[] hashes = handling.VehicleWeaponHandlingData.WeaponHash;
                if (slotIndex >= hashes.Length) return "";
                int index = Array.FindIndex(WeaponHashTable, entry => entry.Hash == (uint)hashes[slotIndex]);
                return index >= 0 ? WeaponHashTable[index].DisplayName : CUSTOM_WEAPON_HASH_REGISTRY_PREFIX + ((uint)hashes[slotIndex]).ToString("X8", CultureInfo.InvariantCulture);
            }
            if (slotTag == WEAPON_SEAT_ARRAY_TAG)
            {
                VehicleSeat[] seats = handling.VehicleWeaponHandlingData.WeaponSeats;
                return slotIndex < seats.Length ? seats[slotIndex].ToString() : "";
            }
            if (slotTag == WEAPON_MOD_TYPE_ARRAY_TAG)
            {
                VehicleModType[] modTypes = handling.VehicleWeaponHandlingData.WeaponVehicleModType;
                return slotIndex < modTypes.Length ? modTypes[slotIndex].ToString() : "";
            }
            return "";
        }

        private void ApplySelectiveCopyField(NativeItem sourceItem, PreviewField field, string fieldKey)
        {
            SelectiveCopyRun run = BeginSelectiveCopyRun(sourceItem);
            if (run == null) return;
            FinishSelectiveCopyRow(sourceItem, CopySelectiveField(run, field, fieldKey), run, fieldKey);
        }

        // A null category on the field's ref means a base field, which is always live present
        // AdvancedData rows (Car category) dispatch to their own 3-way Live, LivePending, and Phantom resolver
        // A live-present Car with an empty real AdvancedData array still needs the pending-block path, even though IsCategoryPresent already reads True for Car itself
        // Every failure logs its own WARN here, and the caller decides the flash and the toast
        private SelectiveCopyOutcome CopySelectiveField(SelectiveCopyRun run, PreviewField field, string fieldKey)
        {
            string fieldRegistryCategory = field.Ref.Category;
            string newValueText = field.Value;
            // Same rule as applying a handling: A rocket boost field is never written to a vehicle with no rocket boost
            if (fieldRegistryCategory == null && field.Entry != null && field.Entry.RequiresRocketBoost && !run.Vehicle.HasRocketBoost)
            {
                logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyRocketBoostAbsent(field.Title));
                return SelectiveCopyOutcome.RocketBoostAbsent;
            }
            if (fieldRegistryCategory == CATEGORY_CAR && field.Ref.Tag == ADVANCED_DATA_TAG) return CopySelectiveAdvancedData(run, field.Ref.Index, field.Ref.Member, fieldKey, newValueText);
            Func<Vehicle, string, ApplyResult> apply;
            if (!fieldRegistry.TryGetValue(fieldKey, out apply))
            {
                // A key with no registered closure can still name an export-only category the vehicle lacks
                if (fieldRegistryCategory != null && !IsCategoryPresent(run.Handling, fieldRegistryCategory)) return CopySelectiveAbsentCategory(run, field, fieldKey);
                return FailCopy(fieldKey);
            }
            string oldValueText;
            bool oldValueBound = ResolveBoundFieldValue(fieldKey, run.Handling, out oldValueText);
            if (!oldValueBound) oldValueText = fieldKey == "AIHandling" ? ResolveAIHandlingName(run.Handling) : "";
            // A category the vehicle lacks has no live value to compare, so it goes on to the export-only routing below
            bool categoryLive = fieldRegistryCategory == null || IsCategoryPresent(run.Handling, fieldRegistryCategory);
            bool isInteger = intBindingByFieldKey.ContainsKey(fieldKey);
            bool identical = categoryLive && (fieldKey == "AIHandling" ? oldValueText == newValueText : oldValueBound && IsSelectiveCopyValueIdentical(oldValueText, ApplySelectiveCopyHardBounds(field.Entry, newValueText, isInteger), isInteger));
            if (identical) return SelectiveCopyOutcome.Identical;
            ApplyResult result = apply(run.Vehicle, newValueText);
            if (result.Outcome == ApplyOutcome.CategoryAbsent) return CopySelectiveAbsentCategory(run, field, fieldKey);
            if (result.Outcome != ApplyOutcome.Applied) return FailCopy(fieldKey);
            run.Flags |= result.Flags;
            run.LiveWritten = true;
            PushUndo(run.HandlingKey, fieldKey, oldValueText, newValueText);
            return SelectiveCopyOutcome.Applied;
        }

        // When the vehicle lacks the field's category, the copy creates it first and then writes into its export-only element
        // It fails only for a category the vehicle cannot hold
        private SelectiveCopyOutcome CopySelectiveAbsentCategory(SelectiveCopyRun run, PreviewField field, string fieldKey)
        {
            XElement phantomElement;
            if (ResolveOrCreateCopyElement(run, field.Ref.Category, out phantomElement)) return CopySelectivePhantomScalar(run, field, phantomElement, fieldKey);
            logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyCategoryAbsent(fieldKey, field.Ref.Category));
            return SelectiveCopyOutcome.CategoryAbsent;
        }

        // AdvancedData rows bypass the ordinary category-presence gate above them: Car live-present with a real entry at slotIndex uses the existing
        // fieldRegistry path unchanged, Car live-present but with only a pending block writes through the Pending store,
        // and Car itself phantom (not live) writes through the handling's phantom Car element's own AdvancedData child
        private SelectiveCopyOutcome CopySelectiveAdvancedData(SelectiveCopyRun run, int slotIndex, string member, string fieldKey, string newValueText)
        {
            EditorContext target = run.Target;
            HandlingData handling = run.Handling;
            HandlingKey handlingKey = run.HandlingKey;
            bool carLive = IsCategoryPresent(handling, CATEGORY_CAR);
            if (carLive && slotIndex < RawMemory.GetAdvancedDataCount(handling))
            {
                Func<Vehicle, string, ApplyResult> apply;
                if (!fieldRegistry.TryGetValue(fieldKey, out apply)) return FailCopy(fieldKey);
                string oldValueText;
                bool oldValueBound = ResolveBoundFieldValue(fieldKey, handling, out oldValueText);
                if (!oldValueBound) oldValueText = "";
                FieldEntry liveMemberRow = member == "Value" ? AdvancedDataValueField : ResolveAdvancedDataField(member);
                if (oldValueBound && IsSelectiveCopyValueIdentical(oldValueText, ApplySelectiveCopyHardBounds(liveMemberRow, newValueText, member != "Value"), member != "Value")) return SelectiveCopyOutcome.Identical;
                ApplyResult result = apply(run.Vehicle, newValueText);
                if (result.Outcome != ApplyOutcome.Applied) return FailCopy(fieldKey);
                run.Flags |= result.Flags;
                run.LiveWritten = true;
                PushUndo(handlingKey, fieldKey, oldValueText, newValueText);
                return SelectiveCopyOutcome.Applied;
            }
            XElement targetSlot;
            XmlAdvancedDataStore store = ResolveXmlAdvancedDataStore(carLive ? AdvancedDataMenuKind.Pending : AdvancedDataMenuKind.Phantom);
            XElement phantomElement = null;
            if (carLive) targetSlot = GetAdvancedDataItem(store.ResolveBlock(this, handlingKey), slotIndex);
            else
            {
                // The toggle decides whether the export-only Car exists, since an undone creation keeps its element for a later re-create
                ResolveOrCreateCopyElement(run, CATEGORY_CAR, out phantomElement);
                targetSlot = phantomElement != null ? GetAdvancedDataItem(phantomElement.Element(ADVANCED_DATA_TAG), slotIndex) : null;
                if (targetSlot == null && phantomElement != null && run.CreatedCategories.ContainsKey(CATEGORY_CAR)) targetSlot = AddCopyAdvancedDataSlots(phantomElement, slotIndex);
            }
            // Only a missing export-only Car element is a missing category. A live Car with no pending block, or an element without this entry, lacks the entry
            if (targetSlot == null)
            {
                if (!carLive && phantomElement == null)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyCategoryAbsent(fieldKey, CATEGORY_CAR));
                    return SelectiveCopyOutcome.CategoryAbsent;
                }
                logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyEntryAbsent(fieldKey, CATEGORY_CAR));
                return SelectiveCopyOutcome.EntryAbsent;
            }
            bool recordUndo = carLive || !run.CreatedCategories.ContainsKey(CATEGORY_CAR);
            if (member == "Value")
            {
                float parsed;
                if (!ParseCopyFloat(newValueText, fieldKey, out parsed)) return SelectiveCopyOutcome.Failed;
                float oldFloat;
                ReadFloatAttr(targetSlot, "Value", out oldFloat);
                parsed = ApplyHardBounds(AdvancedDataValueField, parsed, 0);
                if (SameAtSaveResolution(oldFloat, parsed)) return SelectiveCopyOutcome.Identical;
                SetPhantomFloatAttr(targetSlot, "Value", parsed);
                if (recordUndo) PushStructuralEdit(new AdvancedDataFieldEdit(store, slotIndex, "Value", oldFloat.ToString("R", CultureInfo.InvariantCulture), parsed.ToString("R", CultureInfo.InvariantCulture)) { Handling = handlingKey });
            }
            else
            {
                int parsed;
                if (!ParseCopyInt(newValueText, fieldKey, out parsed)) return SelectiveCopyOutcome.Failed;
                int oldInt;
                ReadIntAttr(targetSlot, member, out oldInt);
                FieldEntry memberRow = ResolveAdvancedDataField(member);
                if (memberRow != null) parsed = ApplyHardBounds(memberRow, parsed);
                if (parsed == oldInt) return SelectiveCopyOutcome.Identical;
                SetPhantomIntAttr(targetSlot, member, parsed);
                if (recordUndo) PushStructuralEdit(new AdvancedDataFieldEdit(store, slotIndex, member, oldInt.ToString(CultureInfo.InvariantCulture), parsed.ToString(CultureInfo.InvariantCulture)) { Handling = handlingKey });
            }
            if (carLive || phantomElement == target.Phantom.CurrentPhantomElement) SyncPhantomFieldValues(store.MenusOf(target).ListMenu);
            return SelectiveCopyOutcome.Applied;
        }

        // A field with no table row fails, and a written value holds its row's hard bounds like every other write path
        private SelectiveCopyOutcome CopySelectivePhantomScalar(SelectiveCopyRun run, PreviewField field, XElement phantomElement, string fieldKey)
        {
            FieldEntry entry = field.Entry;
            string tag = field.Ref.Tag;
            string component = field.Ref.Component;
            string fieldRegistryCategory = field.Ref.Category;
            string newValueText = field.Value;
            if (entry == null) return FailCopy(fieldKey);
            string oldValueText;
            string newStructValue;
            string kind;
            if (component != null)
            {
                float parsed;
                if (!ParseCopyFloat(newValueText, fieldKey, out parsed)) return SelectiveCopyOutcome.Failed;
                float oldFloat = GetPhantomVecComponent(phantomElement, tag, component);
                parsed = ApplyHardBounds(entry, parsed, 0);
                if (SameAtSaveResolution(oldFloat, parsed)) return SelectiveCopyOutcome.Identical;
                SetPhantomVecComponent(phantomElement, tag, component, parsed);
                oldValueText = oldFloat.ToString("R", CultureInfo.InvariantCulture);
                newStructValue = parsed.ToString("R", CultureInfo.InvariantCulture);
                kind = StructuralKind.PhantomVector;
            }
            else if (entry.IsInt)
            {
                int parsed;
                if (!ParseCopyInt(newValueText, fieldKey, out parsed)) return SelectiveCopyOutcome.Failed;
                int oldInt;
                ReadIntAttr(phantomElement, tag, out oldInt);
                parsed = ApplyHardBounds(entry, parsed);
                if (parsed == oldInt) return SelectiveCopyOutcome.Identical;
                SetPhantomIntAttr(phantomElement, tag, parsed);
                oldValueText = oldInt.ToString(CultureInfo.InvariantCulture);
                newStructValue = parsed.ToString(CultureInfo.InvariantCulture);
                kind = StructuralKind.PhantomInt;
            }
            else
            {
                float parsed;
                if (!ParseCopyFloat(newValueText, fieldKey, out parsed)) return SelectiveCopyOutcome.Failed;
                float oldFloat;
                ReadFloatAttr(phantomElement, tag, out oldFloat);
                parsed = ApplyHardBounds(entry, parsed, 0);
                if (SameAtSaveResolution(oldFloat, parsed)) return SelectiveCopyOutcome.Identical;
                SetPhantomFloatAttr(phantomElement, tag, parsed);
                oldValueText = oldFloat.ToString("R", CultureInfo.InvariantCulture);
                newStructValue = parsed.ToString("R", CultureInfo.InvariantCulture);
                kind = StructuralKind.PhantomFloat;
            }
            if (!run.CreatedCategories.ContainsKey(fieldRegistryCategory)) PushStructuralEdit(new PhantomFieldEdit(kind, fieldRegistryCategory, tag, component, oldValueText, newStructValue) { Handling = run.HandlingKey });
            if (phantomElement == run.Target.Phantom.CurrentPhantomElement) SyncPhantomFieldValues(GetPhantomMenuForCategory(run.Target, fieldRegistryCategory));
            return SelectiveCopyOutcome.Applied;
        }

        // Same contract as ApplySelectiveCopyField, for a weapon slot field
        private void ApplySelectiveCopyWeaponField(NativeItem sourceItem, PreviewField field, string fieldKey)
        {
            SelectiveCopyRun run = BeginSelectiveCopyRun(sourceItem);
            if (run == null) return;
            FinishSelectiveCopyRow(sourceItem, CopySelectiveWeaponField(run, field, fieldKey), run, fieldKey);
        }

        // Old-value resolution and the live-category presence check both go through the weapon-specific per-slot data
        // The flat category field map is not used, since weapon slots never appear in it
        private SelectiveCopyOutcome CopySelectiveWeaponField(SelectiveCopyRun run, PreviewField field, string fieldKey)
        {
            int slotIndex = field.Ref.Index;
            string slotTag = field.Ref.Tag;
            string newValueText = field.Value;
            HandlingData handling = run.Handling;
            // Same rule as applying a handling: A turret pitch limit is never written to a vehicle whose model has no rear mounted turret
            if (field.Entry != null && field.Entry.RequiresRearMountedTurret && !RawMemory.HasRearMountedTurret(run.Vehicle))
            {
                logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyRearMountedTurretAbsent(fieldKey));
                return SelectiveCopyOutcome.TurretAbsent;
            }
            if (!IsCategoryPresent(handling, CATEGORY_WEAPON))
            {
                XElement phantomElement;
                if (ResolveOrCreateCopyElement(run, CATEGORY_WEAPON, out phantomElement)) return CopySelectivePhantomWeaponField(run, phantomElement, slotIndex, slotTag, fieldKey, newValueText);
                logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyCategoryAbsent(fieldKey, CATEGORY_WEAPON));
                return SelectiveCopyOutcome.CategoryAbsent;
            }
            Func<Vehicle, string, ApplyResult> apply;
            if (!fieldRegistry.TryGetValue(fieldKey, out apply)) return FailCopy(fieldKey);
            string oldValueText;
            bool oldValueBound = ResolveBoundFieldValue(fieldKey, handling, out oldValueText);
            if (!oldValueBound) oldValueText = ResolveWeaponCyclerOldValue(handling, slotTag, slotIndex);
            VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
            string applyText = newValueText;
            bool crossesUnsetBoundary = false;
            uint newHash = 0;
            // WeaponHash needs its own boundary check before writing
            // That lets it zero the slot's other fields via ApplyWeaponHashBoundaryCross the moment the copy crosses the unset boundary
            // Every other weapon field keeps the plain apply() and PushUndo() path unchanged
            if (slotTag == WEAPON_HASH_ARRAY_TAG)
            {
                if (slotIndex >= weaponData.WeaponHash.Length) return FailCopy(fieldKey);
                // A table weapon writes by its display name
                // A "Custom (0xXXXXXXXX)" row writes its raw hash through the registry's "Custom:" form, the same text the live cycler records
                // Undo and Redo then replay it identically
                int index = Array.FindIndex(WeaponHashTable, entry => entry.DisplayName == newValueText);
                if (index >= 0) newHash = WeaponHashTable[index].Hash;
                else if (ParseCustomWeaponHashDisplay(newValueText, out newHash)) applyText = CUSTOM_WEAPON_HASH_REGISTRY_PREFIX + newHash.ToString("X8", CultureInfo.InvariantCulture);
                else return FailCopy(fieldKey);
                crossesUnsetBoundary = IsUnsetWeaponHash(weaponData.WeaponHash[slotIndex]) != IsUnsetWeaponHash((VehicleWeaponHash)newHash);
            }
            // A weapon hash compares as the resolved hash, so a display name and the Custom form of the same hash match
            bool identical;
            if (slotTag == WEAPON_HASH_ARRAY_TAG) identical = (uint)weaponData.WeaponHash[slotIndex] == newHash;
            else if (oldValueBound)
            {
                bool isInteger = intBindingByFieldKey.ContainsKey(fieldKey);
                identical = IsSelectiveCopyValueIdentical(oldValueText, ApplySelectiveCopyHardBounds(field.Entry, applyText, isInteger), isInteger);
            }
            else identical = oldValueText.Length > 0 && oldValueText == applyText;
            if (identical) return SelectiveCopyOutcome.Identical;
            if (crossesUnsetBoundary)
            {
                // The boundary cross writes the arrays directly instead of through the registry, so it asks for the respawn the registry would have
                ApplyWeaponHashBoundaryCross(weaponData, slotIndex, newHash, run.HandlingKey);
                run.Flags |= ReplayFlags.Respawn;
            }
            else
            {
                ApplyResult result = apply(run.Vehicle, applyText);
                if (result.Outcome != ApplyOutcome.Applied) return FailCopy(fieldKey);
                run.Flags |= result.Flags;
                PushUndo(run.HandlingKey, fieldKey, oldValueText, applyText);
            }
            run.LiveWritten = true;
            return SelectiveCopyOutcome.Applied;
        }

        // A weapon slot row's array tag -> the phantom PhantomWeaponFieldEdit.Member it corresponds to
        // Only these 3 have a phantom write target at all, since BuildPhantomWeaponSlotMenuItems wires no turret rows
        private static readonly Dictionary<string, string> SelectiveCopyPhantomWeaponMemberMap = new Dictionary<string, string>
        {
            { WEAPON_HASH_ARRAY_TAG, "Hash" }, { WEAPON_SEAT_ARRAY_TAG, "Seat" }, { WEAPON_MOD_TYPE_ARRAY_TAG, "ModType" },
        };

        // newValueText is Preview's display name (a WeaponHashTable, VehicleSeatNames, or VehicleModTypeNames entry)
        // It goes through the same reverse lookup against those tables that the cyclers use
        private SelectiveCopyOutcome CopySelectivePhantomWeaponField(SelectiveCopyRun run, XElement phantomElement, int slotIndex, string slotTag, string fieldKey, string newValueText)
        {
            string member;
            if (!SelectiveCopyPhantomWeaponMemberMap.TryGetValue(slotTag, out member)) return FailCopy(fieldKey);
            // A slot the export-only Weapon element does not hold is skipped, since a copy never adds slots
            if (GetPhantomWeaponHashText(phantomElement, slotIndex) == null)
            {
                logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyEntryAbsent(fieldKey, CATEGORY_WEAPON));
                return SelectiveCopyOutcome.EntryAbsent;
            }
            string oldText;
            string newText;
            bool applied;
            if (member == "Hash")
            {
                int index = Array.FindIndex(WeaponHashTable, entry => entry.DisplayName == newValueText);
                uint customHash;
                string nativeHashText;
                if (index >= 0) nativeHashText = WeaponHashTable[index].NativeName;
                // The same enum-text shape SetHashArray saves for a hash outside the table, which the handling loader parses back
                else if (ParseCustomWeaponHashDisplay(newValueText, out customHash)) nativeHashText = ((VehicleWeaponHash)customHash).ToString();
                else return FailCopy(fieldKey);
                oldText = GetPhantomWeaponHashText(phantomElement, slotIndex) ?? "";
                if (oldText == nativeHashText) return SelectiveCopyOutcome.Identical;
                applied = SetPhantomWeaponHashText(phantomElement, slotIndex, nativeHashText);
                newText = nativeHashText;
            }
            else if (member == "Seat")
            {
                int index = Array.IndexOf(VehicleSeatNames, newValueText);
                if (index < 0) return FailCopy(fieldKey);
                VehicleSeat seat = (VehicleSeat)Enum.Parse(typeof(VehicleSeat), VehicleSeatNames[index]);
                int newRaw = (int)seat + 1;
                oldText = GetPhantomWeaponIntArrayItem(phantomElement, WEAPON_SEAT_ARRAY_TAG, slotIndex).ToString(CultureInfo.InvariantCulture);
                if (oldText == newRaw.ToString(CultureInfo.InvariantCulture)) return SelectiveCopyOutcome.Identical;
                applied = SetPhantomWeaponIntArrayItem(phantomElement, WEAPON_SEAT_ARRAY_TAG, slotIndex, newRaw.ToString(CultureInfo.InvariantCulture));
                newText = newRaw.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                int index = Array.IndexOf(VehicleModTypeNames, newValueText);
                if (index < 0) return FailCopy(fieldKey);
                VehicleModType modType = (VehicleModType)Enum.Parse(typeof(VehicleModType), VehicleModTypeNames[index]);
                oldText = GetPhantomWeaponIntArrayItem(phantomElement, WEAPON_MOD_TYPE_ARRAY_TAG, slotIndex).ToString(CultureInfo.InvariantCulture);
                if (oldText == ((int)modType).ToString(CultureInfo.InvariantCulture)) return SelectiveCopyOutcome.Identical;
                applied = SetPhantomWeaponIntArrayItem(phantomElement, WEAPON_MOD_TYPE_ARRAY_TAG, slotIndex, ((int)modType).ToString(CultureInfo.InvariantCulture));
                newText = ((int)modType).ToString(CultureInfo.InvariantCulture);
            }
            if (!applied) return FailCopy(fieldKey);
            if (!run.CreatedCategories.ContainsKey(CATEGORY_WEAPON)) PushStructuralEdit(new PhantomWeaponFieldEdit(slotIndex, member, oldText, newText) { Handling = run.HandlingKey });
            if (phantomElement == run.Target.Phantom.CurrentPhantomElement) SyncPhantomWeaponSlotFieldValues(run.Target, slotIndex);
            return SelectiveCopyOutcome.Applied;
        }

        // Preview's flag checkboxes are permanently Enabled = false, and a disabled item can never fire the API's Activated (see PreviewFlagCheckbox)
        // So Selective Copy cannot wire them the normal way and polls here instead
        // The poll is scoped to exactly the moment a flag category submenu is the visible, focused Preview menu and its selected row is a checkbox
        private void CheckSelectiveCopyFlagInput()
        {
            // Flag checkboxes copy through this direct poll, not the Activated closures, so they need the same descent guard those closures carry:
            // Without it, entering a flag submenu copies its first flag on the same keypress
            if (DescentHappenedThisTick()) return;
            if (!navigation.PreviewFocused) return;
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData)
            {
                if (FindFlagFamilyByMenuKey(data.Key) == null) continue;
                NativeMenu menu = data.Value.Menu;
                if (!menu.Visible || !menu.AcceptsInput) continue;
                NativeCheckboxItem checkbox = menu.SelectedItem as NativeCheckboxItem;
                if (checkbox != null && Game.IsControlJustPressed(GTA.Control.FrontendAccept)) ApplySelectiveCopyFlagCheckbox(checkbox, data.Key, data.Value);
                return;
            }
        }

        // An export-only element's flag bit, read from the element its category keeps its flags in
        // A missing element or an unparseable value reads as clear, as the row shows it
        private static bool ReadPhantomFlagBit(XElement phantomElement, int bitIndex, string flagsElementName)
        {
            XElement flagsElement = phantomElement.Element(flagsElementName);
            uint currentFlags;
            return flagsElement != null && uint.TryParse(flagsElement.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out currentFlags) && (currentFlags & (1u << bitIndex)) != 0;
        }

        // The toast says conflicting flags were cleared only when the copy actually unticked some, and the log names them
        private void ReportFlagCopied(string flagName, string fieldKey, List<string> clearedFlagNames)
        {
            notifications.Notify(NOTIFICATION_KEY_SELECTIVE_COPY_FLAG, string.Format(clearedFlagNames.Count > 0 ? NOTIF_FLAG_COPIED_CONFLICTS_CLEARED : NOTIF_FLAG_COPIED, flagName));
            logger.Log(Dbg.Level.Info, Dbg.SelectiveCopyApplied(fieldKey));
            if (clearedFlagNames.Count > 0) logger.Log(Dbg.Level.Info, Dbg.SelectiveCopyConflictsCleared(fieldKey, string.Join(", ", clearedFlagNames.ToArray())));
        }

        private void ApplySelectiveCopyFlagCheckbox(NativeCheckboxItem checkbox, string previewFlagCategory, PreviewCategoryData data)
        {
            SelectiveCopyRun run = BeginSelectiveCopyRun(checkbox);
            if (run == null) return;
            var clearedFlags = new List<string>();
            string flagName;
            string fieldKey;
            SelectiveCopyOutcome outcome = ResolveRunOutcome(run, CopySelectiveFlagCheckbox(run, checkbox, previewFlagCategory, data, true, clearedFlags, out flagName, out fieldKey));
            if (outcome == SelectiveCopyOutcome.Applied)
            {
                FinishSelectiveCopyRun(run);
                StartResultFlash(checkbox, Color.LightGreen);
                ReportFlagCopied(flagName, fieldKey, clearedFlags);
            }
            else if (outcome == SelectiveCopyOutcome.Identical) SelectiveCopyIdentical(checkbox, fieldKey);
            else
            {
                RequestSelectiveCopyCreations(run);
                SelectiveCopyFailed(checkbox, outcome);
            }
        }

        // The checkbox's pool index corresponds to entries[index] for the category's entire lifetime
        // Both arrays are the same fixed static table on every refresh
        // That is what makes resolving the flag name and BitIndex from the checkbox instance safe here
        // A whole-category copy passes untickConflicts False, since it writes the bits as stored like an applied handling
        private SelectiveCopyOutcome CopySelectiveFlagCheckbox(SelectiveCopyRun run, NativeCheckboxItem checkbox, string previewFlagCategory, PreviewCategoryData data, bool untickConflicts, List<string> clearedFlagNames, out string flagName, out string fieldKey)
        {
            flagName = "";
            fieldKey = "";
            FlagFamily family = FindFlagFamilyByMenuKey(previewFlagCategory);
            FlagNameEntry[] entries = family != null ? family.Entries : null;
            int index = entries != null ? data.CheckboxPool.IndexOf(checkbox) : -1;
            if (entries == null || index < 0 || index >= entries.Length) return FailCopy(previewFlagCategory);
            flagName = entries[index].Name;
            int bitIndex = entries[index].BitIndex;
            string fieldRegistryCategory = family.DescriptorCategory;
            fieldKey = fieldRegistryCategory + "." + flagName + ResolveSelectiveCopyKeySuffix();
            EditorContext target = run.Target;
            HandlingData handling = run.Handling;
            HandlingKey handlingKey = run.HandlingKey;
            // ModelFlags, HandlingFlags, and DamageFlags are base CHandlingData fields, always present on a valid handling
            // AdvancedFlags and SpecialFlightFlags write into their category's export-only element when the vehicle lacks the category, creating it first
            string phantomCategory = family.ElementCategory;
            if (phantomCategory != null && !IsCategoryPresent(handling, phantomCategory))
            {
                XElement phantomElement;
                if (ResolveOrCreateCopyElement(run, phantomCategory, out phantomElement))
                {
                    string flagsElementName = ResolveCategoryDefinition(phantomCategory).FlagsMenu.ElementName;
                    bool recordUndo = !run.CreatedCategories.ContainsKey(phantomCategory);
                    bool oldChecked = ReadPhantomFlagBit(phantomElement, bitIndex, flagsElementName);
                    bool newChecked = checkbox.Checked;
                    if (oldChecked == newChecked) return SelectiveCopyOutcome.Identical;
                    SetPhantomFlagBit(phantomElement, bitIndex, newChecked, flagsElementName);
                    if (recordUndo) PushStructuralEdit(new PhantomFlagEdit(phantomCategory, flagName, bitIndex, oldChecked, newChecked) { Handling = handlingKey });
                    if (newChecked && untickConflicts)
                    {
                        // The copy and its unticks undo and redo as one event, like the editor's checkbox cascade
                        BeginUndoGroup(flagName);
                        try
                        {
                            for (int j = 0; j < entries.Length; j++)
                            {
                                if (!FlagEntriesConflict(entries, index, j) || !ReadPhantomFlagBit(phantomElement, entries[j].BitIndex, flagsElementName)) continue;
                                SetPhantomFlagBit(phantomElement, entries[j].BitIndex, false, flagsElementName);
                                if (recordUndo) PushStructuralEdit(new PhantomFlagEdit(phantomCategory, entries[j].Name, entries[j].BitIndex, true, false) { Handling = handlingKey });
                                clearedFlagNames.Add(entries[j].Name);
                            }
                        }
                        finally { EndUndoGroup(); }
                    }
                    if (ForcePhantomWorksUpsideDown(phantomElement, phantomCategory, target.TargetVehicle)) ReportWorksUpsideDownForced(target.TargetVehicle);
                    if (phantomElement == target.Phantom.CurrentPhantomElement) SyncPhantomFieldValues(FindSubHandlingCategory(target, phantomCategory).PhantomFlagsMenu);
                    return SelectiveCopyOutcome.Applied;
                }
                logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyCategoryAbsent(fieldKey, phantomCategory));
                return SelectiveCopyOutcome.CategoryAbsent;
            }
            Func<Vehicle, string, ApplyResult> apply;
            if (!fieldRegistry.TryGetValue(fieldKey, out apply)) return FailCopy(fieldKey);
            IntPtr address = family.ResolveAddress(handling);
            int offset = family.Offset;
            Func<IntPtr, int, uint> readFlags = family.ReadFlags;
            bool oldValue = (readFlags(address, offset) & (1u << bitIndex)) != 0;
            if (oldValue == checkbox.Checked) return SelectiveCopyOutcome.Identical;
            string newValueText = checkbox.Checked.ToString();
            ApplyResult result = apply(run.Vehicle, newValueText);
            if (result.Outcome != ApplyOutcome.Applied) return FailCopy(fieldKey);
            PushUndo(handlingKey, fieldKey, oldValue.ToString(), newValueText);
            run.Flags |= result.Flags;
            run.LiveWritten = true;
            if (checkbox.Checked && untickConflicts)
            {
                // Each conflicting flag that is set unticks through its own registry closure on the same target the copy wrote
                // The copy and its unticks undo and redo as one event, like the editor's checkbox cascade
                BeginUndoGroup(flagName);
                try
                {
                    for (int j = 0; j < entries.Length; j++)
                    {
                        if (!FlagEntriesConflict(entries, index, j) || (readFlags(address, offset) & (1u << entries[j].BitIndex)) == 0) continue;
                        string conflictKey = fieldRegistryCategory + "." + entries[j].Name + ResolveSelectiveCopyKeySuffix();
                        Func<Vehicle, string, ApplyResult> clearConflict;
                        if (!fieldRegistry.TryGetValue(conflictKey, out clearConflict))
                        {
                            logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyFieldUnresolved(conflictKey));
                            continue;
                        }
                        ApplyResult clearResult = clearConflict(run.Vehicle, "False");
                        if (clearResult.Outcome != ApplyOutcome.Applied)
                        {
                            logger.Log(Dbg.Level.Warn, Dbg.SelectiveCopyFieldUnresolved(conflictKey));
                            continue;
                        }
                        run.Flags |= clearResult.Flags;
                        PushUndo(handlingKey, conflictKey, "True", "False");
                        clearedFlagNames.Add(entries[j].Name);
                    }
                }
                finally { EndUndoGroup(); }
            }
            return SelectiveCopyOutcome.Applied;
        }

        // The highlighted row of the Preview menu on screen, when Preview has focus and the row opens a Preview category menu
        private NativeSubmenuItem ResolveHighlightedPreviewCategoryRow()
        {
            if (!navigation.PreviewFocused) return null;
            NativeMenu visiblePreview = GetVisiblePreviewMenu();
            return visiblePreview != null ? visiblePreview.SelectedItem as NativeSubmenuItem : null;
        }

        // Copies every row of the menu the category row opens, as one undo event, then respawns or refreshes the vehicle once
        // Each row goes down the path its own Enter takes, except a flag, which never unticks its conflicting flags
        private void ApplySelectiveCopyCategory(NativeSubmenuItem categoryItem)
        {
            if (categoryItem == null) return;
            SelectiveCopyRun run = BeginSelectiveCopyRun(categoryItem);
            if (run == null) return;
            var tally = new SelectiveCopyTally();
            BeginUndoGroup();
            try { CopySelectiveMenuRows(run, categoryItem.Menu, tally); }
            finally { EndUndoGroup(); }
            FinishSelectiveCopyRun(run);
            string categoryName = categoryItem.Title;
            logger.Log(Dbg.Level.Info, Dbg.SelectiveCopyCategory(categoryName, tally.Applied, tally.Identical, tally.Skipped));
            // A created category counts as applied whatever its rows did, since the creation happened
            if (tally.Applied > 0 || run.CreatedCategories.Count > 0)
            {
                StartResultFlash(categoryItem, tally.Skipped > 0 ? Color.Yellow : Color.LightGreen);
                notifications.Notify(NOTIFICATION_KEY_SELECTIVE_COPY_CATEGORY, string.Format(tally.Skipped > 0 ? NOTIF_CATEGORY_COPIED_SKIPPED : NOTIF_CATEGORY_COPIED, categoryName, tally.Skipped));
            }
            else if (tally.Skipped == 0 && tally.Identical > 0)
            {
                StartResultFlash(categoryItem, Color.Yellow);
                notifications.Notify(NOTIFICATION_KEY_SELECTIVE_COPY_CATEGORY, string.Format(NOTIF_CATEGORY_IDENTICAL, categoryName));
            }
            else SelectiveCopyFailed(categoryItem, tally.Skipped > 0 ? tally.SharedFailure : SelectiveCopyOutcome.Failed);
        }

        // A placeholder row carries no field, so it is skipped
        // Rows of a weapon slot menu take the weapon slot path, since their keys carry the slot index
        private void CopySelectiveMenuRows(SelectiveCopyRun run, NativeMenu menu, SelectiveCopyTally tally)
        {
            bool isWeaponSlotMenu = Array.IndexOf(previewWeaponSlotMenus, menu) >= 0;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                NativeItem item = menu.Items[i];
                NativeSubmenuItem submenuItem = item as NativeSubmenuItem;
                if (submenuItem != null)
                {
                    CopySelectiveMenuRows(run, submenuItem.Menu, tally);
                    continue;
                }
                SelectiveCopyOutcome outcome;
                NativeCheckboxItem checkbox = item as NativeCheckboxItem;
                if (checkbox != null) outcome = CopySelectiveMenuFlag(run, menu, checkbox);
                else
                {
                    PreviewField field = item.Tag as PreviewField;
                    if (field == null) continue;
                    outcome = isWeaponSlotMenu ? CopySelectiveWeaponField(run, field, BuildSelectiveCopyWeaponFieldKey(field)) : CopySelectiveField(run, field, BuildSelectiveCopyFieldKey(field));
                }
                if (outcome == SelectiveCopyOutcome.Applied) tally.Applied++;
                else if (outcome == SelectiveCopyOutcome.Identical) tally.Identical++;
                else
                {
                    if (outcome == SelectiveCopyOutcome.CategoryAbsent || tally.Skipped == 0) tally.SharedFailure = outcome;
                    else if (tally.SharedFailure != outcome && tally.SharedFailure != SelectiveCopyOutcome.CategoryAbsent) tally.SharedFailure = SelectiveCopyOutcome.Failed;
                    tally.Skipped++;
                }
            }
        }

        private SelectiveCopyOutcome CopySelectiveMenuFlag(SelectiveCopyRun run, NativeMenu menu, NativeCheckboxItem checkbox)
        {
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData)
            {
                if (data.Value.Menu != menu || FindFlagFamilyByMenuKey(data.Key) == null) continue;
                string flagName;
                string fieldKey;
                return CopySelectiveFlagCheckbox(run, checkbox, data.Key, data.Value, false, new List<string>(), out flagName, out fieldKey);
            }
            return FailCopy(menu.Name);
        }
    }
}
