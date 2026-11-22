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
        private void CheckSavedHandlingFileExists()
        {
            // Search results are not files, so there is nothing on disk to compare them against
            if (browsedSource == searchSource) return;
            SavedHandlingKey selectedKey = GetHighlightedSavedHandlingKey();
            if (selectedKey.IsValid && !System.IO.File.Exists(SavedHandlingPath(selectedKey)))
            {
                RefreshSavedHandlingsMenu();
                return;
            }
            // With Autosave off, the Autosaves row appears with the first autosave and leaves with the last. A toggle of the setting also shows or hides it
            if (browsedSource != autosavesSource && IsAutosavesRowWanted() != autosavesRowShown)
            {
                RefreshSavedHandlingsMenu();
                return;
            }
            SavedHandlingKey[] currentSavedHandlings = browsedSource.ListKeys();
            if (currentSavedHandlings.Length != cachedSavedHandlingKeys.Length)
            {
                RefreshSavedHandlingsMenu();
                return;
            }
            for (int i = 0; i < currentSavedHandlings.Length; i++)
            {
                if (Array.IndexOf(cachedSavedHandlingKeys, currentSavedHandlings[i]) < 0)
                {
                    RefreshSavedHandlingsMenu();
                    return;
                }
            }
            // A save to the highlighted saved handling from outside the game changes its write time. Preview and Diff rebuild from the reparsed file
            if (selectedKey.IsValid)
            {
                HandlingPreviewCache cached;
                if (previewFieldListCache.TryGetValue(selectedKey, out cached) && cached.WriteTime != System.IO.File.GetLastWriteTimeUtc(SavedHandlingPath(selectedKey))) RefreshPreviewMenu(cachedSavedHandlingKeys);
            }
        }

        // Saved Handlings lists Exports, the Autosaves folder, or search results in the same menu
        // Back inside Autosaves returns to Exports
        // A toggle-key close keeps the source, while entering from the main menu or its hotkey starts on Exports
        private SavedHandlingSource browsedSource;
        // The Autosaves row sits above the saved handlings while Autosave is on or any autosave exists, shifting every saved handling row down by one
        private bool autosavesRowShown;
        private NativeItem autosavesRowItem;
        // The row above the list: The New Search row while searching, or the Autosaves row while it is shown
        private int SavedHandlingRowOffset { get { return browsedSource == searchSource || autosavesRowShown ? 1 : 0; } }
        // False while there is no Active vehicle. Saved handling rows then read dimmed, since applying one needs a vehicle
        // Every other saved handling action still works
        private bool savedHandlingRowsApplicable = true;

        // Re-tints in place when the Active vehicle comes or goes, which keeps the highlight and any armed row
        private void SyncSavedHandlingRowsApplicable(bool applicable)
        {
            if (applicable == savedHandlingRowsApplicable) return;
            savedHandlingRowsApplicable = applicable;
            if (savedHandlingsMenu == null) return;
            if (browsedSource == searchSource)
            {
                SyncSearchRowTints();
                return;
            }
            for (int i = SavedHandlingRowOffset; i < savedHandlingsMenu.Items.Count && i - SavedHandlingRowOffset < cachedSavedHandlingKeys.Length; i++)
            {
                NativeItem item = savedHandlingsMenu.Items[i];
                if (applicable) item.Colors = new ColorSet();
                else DimItemAsDisabled(item);
                // An armed row restores this color when it disarms, so it has to follow the new tint too
                if (item == armedItem) armedOriginalColor = item.Colors.TitleHovered;
            }
        }

        // Every file operation reads GetHighlightedSavedHandlingKey instead, which never returns a search result
        private SavedHandlingKey GetHighlightedPreviewKey()
        {
            int index = savedHandlingsMenu.SelectedIndex - SavedHandlingRowOffset;
            return index >= 0 && index < cachedSavedHandlingKeys.Length ? cachedSavedHandlingKeys[index] : new SavedHandlingKey();
        }

        // Invalid for a search result, so no file call can ever receive one
        private SavedHandlingKey GetHighlightedSavedHandlingKey()
        {
            SavedHandlingKey key = GetHighlightedPreviewKey();
            return key.IsValid && key.Source.Folder != null ? key : new SavedHandlingKey();
        }

        // The setting is checked first, so the folder is only listed while Autosave is off
        private bool IsAutosavesRowWanted() { return autosaveEnabled || autosavesSource.ListKeys().Length > 0; }

        private void EnterAutosavesList()
        {
            browsedSource = autosavesSource;
            RefreshSavedHandlingsMenu();
            savedHandlingsMenu.SelectedIndex = 0;
        }

        private void RefreshSavedHandlingsMenu() { RefreshSavedHandlingsMenu(null, -1); }

        // focusHandlingName or focusRow moves the highlight, since a Duplicate or Rename can land its saved handling anywhere in the sorted list
        // With neither, the row stays
        private void RefreshSavedHandlingsMenu(string focusHandlingName, int focusRow)
        {
            DisarmArmed();
            int previousIndex = savedHandlingsMenu.SelectedIndex;
            // The clear below zeroes the scroll window, and the row assignment only moves it when the row falls outside
            int previousFirstRow = ReadMenuScrollWindow(savedHandlingsMenu);
            savedHandlingsMenu.Clear();
            if (browsedSource == searchSource)
            {
                BuildSearchResultRows(previousIndex);
                return;
            }
            SetSavedHandlingsBanner(browsedSource.Banner);
            SavedHandlingKey[] autosaveKeys = autosavesSource.ListKeys();
            SavedHandlingKey[] mainKeys = exportsSource.ListKeys();
            SavedHandlingKey[] savedHandlings = browsedSource == autosavesSource ? autosaveKeys : mainKeys;
            cachedSavedHandlingKeys = savedHandlings;
            // Pruned against both folders, so switching between Exports and Autosaves keeps each list's warm parse and Preview positions
            var knownKeys = new SavedHandlingKey[mainKeys.Length + autosaveKeys.Length];
            mainKeys.CopyTo(knownKeys, 0);
            autosaveKeys.CopyTo(knownKeys, mainKeys.Length);
            PrunePreviewFieldListCache(knownKeys);
            PrunePreviewPositionBySavedHandling(knownKeys);
            savedHandlingsMenu.Name = browsedSource.Subtitle;
            autosavesRowShown = browsedSource != autosavesSource && (autosaveEnabled || autosaveKeys.Length > 0);
            if (focusRow >= 0) previousIndex = focusRow;
            int focusHandlingIndex = focusHandlingName != null ? Array.FindIndex(savedHandlings, listed => string.Equals(listed.Name, focusHandlingName, StringComparison.OrdinalIgnoreCase)) : -1;
            if (focusHandlingIndex >= 0) previousIndex = focusHandlingIndex + SavedHandlingRowOffset;
            if (autosavesRowShown)
            {
                if (autosavesRowItem == null)
                {
                    autosavesRowItem = new StyledChevronItem(LABEL_AUTOSAVES, DESC_AUTOSAVES_ROW);
                    SubscribeActivated(autosavesRowItem, "autosavesRowItem.Activated", () => EnterAutosavesList());
                }
                savedHandlingsMenu.Items.Add(autosavesRowItem);
            }
            if (savedHandlings.Length == 0)
            {
                if (browsedSource == autosavesSource) savedHandlingsMenu.Items.Add(new NativeItem(TITLE_NO_AUTOSAVES, DESC_NO_AUTOSAVES));
                else savedHandlingsMenu.Items.Add(new NativeItem(TITLE_NO_SAVED_HANDLINGS, DESC_NO_SAVED_HANDLINGS));
                RestoreSavedHandlingsRow(previousIndex, previousFirstRow);
                RecalculateTargetWidth(savedHandlingsMenu);
                RefreshPreviewMenu(savedHandlings);
                return;
            }
            for (int i = 0; i < savedHandlings.Length; i++)
            {
                SavedHandlingKey savedKey = savedHandlings[i];
                var savedHandlingItem = new NativeItem(savedKey.Name, "");
                if (browsedSource.Has(SavedHandlingCapabilities.Apply)) SubscribeActivated(savedHandlingItem, "savedHandlingItem.Activated", () => ApplyHandling(savedKey));
                if (!savedHandlingRowsApplicable) DimItemAsDisabled(savedHandlingItem);
                savedHandlingsMenu.Items.Add(savedHandlingItem);
            }
            // Keeps the highlighted row and the view across a refresh such as a delete, instead of snapping back to the first file
            RestoreSavedHandlingsRow(previousIndex, previousFirstRow);
            RecalculateTargetWidth(savedHandlingsMenu);
            // Passes the already-fetched list down to avoid a second directory scan
            RefreshPreviewMenu(savedHandlings);
        }

        private void RestoreSavedHandlingsRow(int previousIndex, int previousFirstRow)
        {
            int row = Math.Max(0, Math.Min(previousIndex, savedHandlingsMenu.Items.Count - 1));
            RestoreMenuScrollWindow(savedHandlingsMenu, previousFirstRow, row);
            SelectSavedHandlingsRowWithoutPreviewRefresh(row);
        }

        // True while a list rebuild assigns its row, so the SelectedIndexChanged handler skips the Preview rebuild the caller runs right after anyway
        private bool listRowAssignmentSkipsPreviewRefresh;

        private void SelectSavedHandlingsRowWithoutPreviewRefresh(int index)
        {
            listRowAssignmentSkipsPreviewRefresh = true;
            try { savedHandlingsMenu.SelectedIndex = index; }
            finally { listRowAssignmentSkipsPreviewRefresh = false; }
        }

        // Each viewed saved handling's last Preview position (its category path and rows), restored by every refresh of it
        // Keyed by saved handling key, so an autosave and an export of the same name stay apart
        private Dictionary<SavedHandlingKey, PreviewPosition> previewPositionBySavedHandling = new Dictionary<SavedHandlingKey, PreviewPosition>();
        // The key of the saved handling Preview last showed, since previewMenu.Name holds only its display name
        private SavedHandlingKey previewShownSavedHandlingKey;

        private void PrunePreviewPositionBySavedHandling(SavedHandlingKey[] savedHandlingKeys)
        {
            var staleKeys = new List<SavedHandlingKey>();
            foreach (SavedHandlingKey savedKey in previewPositionBySavedHandling.Keys) if (Array.IndexOf(savedHandlingKeys, savedKey) < 0) staleKeys.Add(savedKey);
            for (int i = 0; i < staleKeys.Count; i++) previewPositionBySavedHandling.Remove(staleKeys[i]);
        }

        // A cleared previewMenu stores nothing, since the refresh's Preview-off branch reaches ClosePreviewTree after the clear and would drop the path
        private void RememberPreviewPosition()
        {
            if (previewMenu.Items.Count == 0) return;
            PreviewPosition position = BuildPreviewPosition();
            if (position != null) previewPositionBySavedHandling[previewShownSavedHandlingKey] = position;
        }

        // Reused across every RefreshPreviewMenu call, and only grows the first time a saved handling needs more rows than any previous one
        private List<NativeItem> previewItemPool = new List<NativeItem>();

        // Lets a category whose values match the previous saved handling skip its own Clear, repopulate, and width recalculation entirely
        // MeasureCached's width cache only hits when the measured text is unchanged, and virtually every field differs between two different saved handlings
        private Dictionary<string, List<PreviewField>> previewCategoryLastFields = new Dictionary<string, List<PreviewField>>();

        private static bool CategoryFieldsEqual(List<PreviewField> a, List<PreviewField> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Ref.Equals(b[i].Ref) || a[i].Value != b[i].Value) return false;
            }
            return true;
        }

        // WeaponHandlingData's categoryValue carries only its 3 whole-category scalars, and the cache's per-slot WeaponSlots data is a separate parse
        // The unchanged check above cannot see a per-slot-only change, so this is the matching last-rendered snapshot for that parse
        private List<List<PreviewField>> previewLastWeaponSlots;

        private static bool WeaponSlotsEqual(List<List<PreviewField>> a, List<List<PreviewField>> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!CategoryFieldsEqual(a[i], b[i])) return false;
            }
            return true;
        }

        // Diff's own copy of previewCategoryLastFields and previewLastWeaponSlots, keyed and compared identically
        private Dictionary<string, List<PreviewField>> previewDiffCategoryLastFields = new Dictionary<string, List<PreviewField>>();
        private List<List<PreviewField>> previewDiffLastWeaponSlots;

        // The menu keys of the flag families that sit on the base handling, built on first use like the families
        private static string[] previewFlagFieldNames;
        private static string[] PreviewFlagFieldNames
        {
            get
            {
                if (previewFlagFieldNames == null)
                {
                    var keys = new List<string>();
                    foreach (FlagFamily family in FlagFamilies) if (family.ElementCategory == null) keys.Add(family.MenuKey);
                    previewFlagFieldNames = keys.ToArray();
                }
                return previewFlagFieldNames;
            }
        }

        private static List<PreviewField> ExtractTopLevelFields(List<PreviewField> fields)
        {
            var topLevel = new List<PreviewField>();
            for (int i = 0; i < fields.Count; i++) { if (fields[i].Ref.Category == null) topLevel.Add(fields[i]); }
            return topLevel;
        }

        // The top-level row order both panels render, so the passive index-based Diff mirror stays aligned when one side lacks a field
        // With no baseline it is Preview's own order. Otherwise it is an ordered merge that emits shared fields once and each side's own fields at their own positions
        // On an order conflict the Preview field comes first. The emitted field also supplies the title of the placeholder row on the side that lacks it
        private static List<PreviewField> BuildTopLevelFieldOrder(List<PreviewField> previewFields, List<PreviewField> baselineFields)
        {
            List<PreviewField> previewTopLevel = ExtractTopLevelFields(previewFields);
            if (baselineFields == null) return previewTopLevel;
            List<PreviewField> baselineTopLevel = ExtractTopLevelFields(baselineFields);
            var previewSet = new HashSet<FieldRef>();
            for (int i = 0; i < previewTopLevel.Count; i++) previewSet.Add(previewTopLevel[i].Ref);
            var baselineSet = new HashSet<FieldRef>();
            for (int i = 0; i < baselineTopLevel.Count; i++) baselineSet.Add(baselineTopLevel[i].Ref);
            var emitted = new HashSet<FieldRef>();
            var order = new List<PreviewField>();
            int previewIndex = 0;
            int baselineIndex = 0;
            while (previewIndex < previewTopLevel.Count || baselineIndex < baselineTopLevel.Count)
            {
                if (previewIndex < previewTopLevel.Count && emitted.Contains(previewTopLevel[previewIndex].Ref))
                {
                    previewIndex++;
                    continue;
                }
                if (baselineIndex < baselineTopLevel.Count && emitted.Contains(baselineTopLevel[baselineIndex].Ref))
                {
                    baselineIndex++;
                    continue;
                }
                PreviewField next;
                if (previewIndex < previewTopLevel.Count && baselineIndex < baselineTopLevel.Count && previewTopLevel[previewIndex].Ref.Equals(baselineTopLevel[baselineIndex].Ref))
                {
                    next = previewTopLevel[previewIndex];
                    previewIndex++;
                    baselineIndex++;
                }
                else if (baselineIndex < baselineTopLevel.Count && !previewSet.Contains(baselineTopLevel[baselineIndex].Ref)) next = baselineTopLevel[baselineIndex++];
                else if (previewIndex < previewTopLevel.Count && !baselineSet.Contains(previewTopLevel[previewIndex].Ref)) next = previewTopLevel[previewIndex++];
                else if (previewIndex < previewTopLevel.Count) next = previewTopLevel[previewIndex++];
                else next = baselineTopLevel[baselineIndex++];
                emitted.Add(next.Ref);
                order.Add(next);
            }
            return order;
        }

        // A field this side lacks gets a placeholder row from placeholderPool, tinted placeholderTint (green on Preview, red on Diff)
        // The row is described by placeholderDescription
        // It has the same shape and text as a category's MissingPlaceholder, which keeps the index-based Diff mirror aligned
        // wireBaseItem wires a newly-pooled base row for Selective Copy and is null on Diff's read-only side
        private Dictionary<string, List<PreviewField>> BuildCategoryFieldsAndBaseRows(List<PreviewField> fields, Dictionary<string, PreviewCategoryData> categoryDataMap, NativeMenu targetMenu, List<NativeItem> basePool, Action<NativeItem> wireBaseItem, List<PreviewField> topLevelFieldOrder, List<NativeItem> placeholderPool, Color placeholderTint, string placeholderDescription)
        {
            var categoryFields = new Dictionary<string, List<PreviewField>>();
            var topLevelFieldsByRef = new Dictionary<FieldRef, PreviewField>();
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Ref.Category != null)
                {
                    string categoryKey = ResolveCategoryLongName(fields[i].Ref.Category);
                    List<PreviewField> categoryList = GetOrAdd(categoryFields, categoryKey);
                    categoryList.Add(fields[i]);
                }
                else topLevelFieldsByRef[fields[i].Ref] = fields[i];
            }
            int basePoolIndex = 0;
            int placeholderPoolIndex = 0;
            for (int i = 0; i < topLevelFieldOrder.Count; i++)
            {
                PreviewField field;
                if (!topLevelFieldsByRef.TryGetValue(topLevelFieldOrder[i].Ref, out field))
                {
                    NativeItem placeholder;
                    if (placeholderPoolIndex < placeholderPool.Count) placeholder = placeholderPool[placeholderPoolIndex];
                    else
                    {
                        placeholder = new NativeItem("");
                        placeholder.Enabled = false;
                        placeholder.Description = placeholderDescription;
                        placeholderTints[placeholder] = placeholderTint;
                        placeholderPool.Add(placeholder);
                    }
                    placeholder.Title = topLevelFieldOrder[i].Title;
                    SetPreviewRowValue(placeholder, "");
                    placeholder.Tag = null;
                    targetMenu.Items.Add(placeholder);
                    placeholderPoolIndex++;
                    continue;
                }
                FlagFamily flagFamily = FindFlagFamilyByTag(field.Ref.Tag);
                if (flagFamily != null && flagFamily.ElementCategory == null)
                {
                    PreviewCategoryData flagData = categoryDataMap[flagFamily.MenuKey];
                    // A top-level flag menu lists every entry of its family, so once built only its ticks change between saved handlings
                    // Ticking in place keeps its rows, their layout, and their widths. PopulatePreviewFlagMenuFromEntries only ever appends, so a build clears first
                    if (flagData.Menu.Items.Count == flagFamily.Entries.Length) TickPreviewFlagRows(flagData, flagFamily.Entries, field.Value);
                    else
                    {
                        ClearPreviewMenu(flagData.Menu);
                        PopulatePreviewFlagMenuFromEntries(flagData, flagFamily.Entries, field.Value, wireBaseItem != null);
                        RecalculatePreviewTargetWidth(flagData.Menu);
                    }
                    flagData.Menu.Name = string.Format(SUBTITLE_FLAGS_TEXT, flagFamily.MenuKey, field.Value, CountTickedFlagRows(ParseFlagHex(field.Value), flagFamily.Entries));
                    flagData.SubmenuItem.Title = flagFamily.MenuKey;
                    targetMenu.Items.Add(flagData.SubmenuItem);
                }
                else
                {
                    NativeItem fieldItem;
                    if (basePoolIndex < basePool.Count) fieldItem = basePool[basePoolIndex];
                    else
                    {
                        fieldItem = new NativeItem("");
                        basePool.Add(fieldItem);
                        CaptureOriginalItemColor(fieldItem);
                        if (wireBaseItem != null) wireBaseItem(fieldItem);
                    }
                    fieldItem.Title = field.Title;
                    SetPreviewRowValue(fieldItem, field.Value);
                    fieldItem.Tag = field;
                    targetMenu.Items.Add(fieldItem);
                    basePoolIndex++;
                }
            }
            return categoryFields;
        }

        // categoryDataMap resolves the nested AdvancedFlags, SpecialFlightFlags, and AdvancedData submenus for the same tree as categoryMenu
        // wireItem wires a newly-pooled row for Selective Copy and is null on Diff's read-only side
        // populateWeaponSlots appends the Weapon category's per-slot submenus after its generic rows
        private void PopulateCategoryRows(Dictionary<string, PreviewCategoryData> categoryDataMap, string categoryKey, NativeMenu categoryMenu, PreviewCategoryData categoryData, List<PreviewField> categoryValue, Action<NativeItem> wireItem, Action<NativeMenu> populateWeaponSlots)
        {
            ClearPreviewMenu(categoryMenu);
            int categoryPoolIndex = 0;
            List<PreviewField> advancedDataRows = null;
            for (int i = 0; i < categoryValue.Count; i++)
            {
                FlagFamily flagFamily = FindFlagFamilyByTag(categoryValue[i].Ref.Tag);
                if (flagFamily != null && flagFamily.ElementCategory != null)
                {
                    PreviewCategoryData flagCategoryData;
                    if (categoryDataMap.TryGetValue(flagFamily.MenuKey, out flagCategoryData))
                    {
                        ClearPreviewMenu(flagCategoryData.Menu);
                        PopulatePreviewFlagMenuFromEntries(flagCategoryData, flagFamily.Entries, categoryValue[i].Value, wireItem != null);
                        RecalculatePreviewTargetWidth(flagCategoryData.Menu);
                        flagCategoryData.Menu.Name = string.Format(flagFamily.PreviewSubtitleTemplate, categoryValue[i].Value, CountTickedFlagRows(ParseFlagHex(categoryValue[i].Value), flagFamily.Entries));
                        flagCategoryData.SubmenuItem.Title = flagFamily.ResidualTitle;
                        categoryMenu.Items.Add(flagCategoryData.SubmenuItem);
                    }
                    continue;
                }
                // Scoped to CarHandlingData, the only category BuildCarHandlingXml writes an AdvancedData element under
                // An AdvancedData element in another category is then never misrouted here
                if (categoryKey == ResolveCategoryDefinition(CATEGORY_CAR).LongName && categoryValue[i].Ref.Tag == ADVANCED_DATA_TAG)
                {
                    if (advancedDataRows == null) advancedDataRows = new List<PreviewField>();
                    advancedDataRows.Add(categoryValue[i]);
                    continue;
                }
                NativeItem categoryItem;
                if (categoryPoolIndex < categoryData.ItemPool.Count) categoryItem = categoryData.ItemPool[categoryPoolIndex];
                else
                {
                    categoryItem = new NativeItem("");
                    categoryData.ItemPool.Add(categoryItem);
                    CaptureOriginalItemColor(categoryItem);
                    if (wireItem != null) wireItem(categoryItem);
                }
                categoryItem.Title = categoryValue[i].Title;
                SetPreviewRowValue(categoryItem, categoryValue[i].Value);
                categoryItem.Tag = categoryValue[i];
                categoryMenu.Items.Add(categoryItem);
                categoryPoolIndex++;
            }
            if (advancedDataRows != null)
            {
                PreviewCategoryData advancedDataCategoryData;
                if (categoryDataMap.TryGetValue("AdvancedData", out advancedDataCategoryData))
                {
                    bool isDiff = categoryDataMap == previewDiffCategoryData;
                    ClearPreviewMenu(advancedDataCategoryData.Menu);
                    // Each row's entry comes from its own ref, and consecutive rows of the same entry fill that entry's own menu
                    int filledEntryIndex = -1;
                    PreviewCategoryData entryData = null;
                    for (int i = 0; i < advancedDataRows.Count; i++)
                    {
                        int entryIndex = advancedDataRows[i].Ref.Index;
                        if (entryIndex >= ADVANCEDDATA_COUNT_SANITY_CEILING) continue;
                        if (entryIndex != filledEntryIndex)
                        {
                            if (entryData != null) FinishPreviewAdvancedDataEntry(entryData.Menu);
                            filledEntryIndex = entryIndex;
                            advancedDataCategoryData.Menu.Items.Add(EnsurePreviewAdvancedDataEntryMenu(entryIndex, isDiff));
                            entryData = categoryDataMap[PreviewAdvancedDataEntryKey(entryIndex, isDiff)];
                            ClearPreviewMenu(entryData.Menu);
                        }
                        int rowIndex = entryData.Menu.Items.Count;
                        NativeItem advancedDataItem;
                        if (rowIndex < entryData.ItemPool.Count) advancedDataItem = entryData.ItemPool[rowIndex];
                        else
                        {
                            advancedDataItem = new NativeItem("");
                            entryData.ItemPool.Add(advancedDataItem);
                            CaptureOriginalItemColor(advancedDataItem);
                            if (wireItem != null) wireItem(advancedDataItem);
                        }
                        advancedDataItem.Title = advancedDataRows[i].Title;
                        SetPreviewRowValue(advancedDataItem, advancedDataRows[i].Value);
                        advancedDataItem.Tag = advancedDataRows[i];
                        entryData.Menu.Items.Add(advancedDataItem);
                    }
                    if (entryData != null) FinishPreviewAdvancedDataEntry(entryData.Menu);
                    if (advancedDataCategoryData.Menu.Items.Count > 0) SelectPreviewRow(advancedDataCategoryData.Menu, 0);
                    RecalculatePreviewTargetWidth(advancedDataCategoryData.Menu);
                    advancedDataCategoryData.Menu.Name = "AdvancedData";
                    advancedDataCategoryData.SubmenuItem.Title = "AdvancedData";
                    categoryMenu.Items.Add(advancedDataCategoryData.SubmenuItem);
                }
            }
            if (categoryKey == ResolveCategoryDefinition(CATEGORY_WEAPON).LongName && populateWeaponSlots != null) populateWeaponSlots(categoryMenu);
            RecalculatePreviewTargetWidth(categoryMenu);
        }

        private void FinishPreviewAdvancedDataEntry(NativeMenu entryMenu)
        {
            if (entryMenu.Items.Count > 0) SelectPreviewRow(entryMenu, 0);
            RecalculatePreviewTargetWidth(entryMenu);
        }

        // A category absent on this side but present on the other gets a placeholder row, so the passive index-based Diff mirror never drifts
        private void AddParityPlaceholderIfNeeded(NativeMenu targetMenu, PreviewCategoryData categoryData, bool parityEligible, bool haveCategoryData, bool otherSideHasCategory)
        {
            if (parityEligible && haveCategoryData && otherSideHasCategory) targetMenu.Items.Add(categoryData.MissingPlaceholder);
        }

        // The parity tint (green on Preview, red on Diff) applies when the category lives only on this side. An empty category adds nothing
        private void FinalizeCategoryPlacement(NativeMenu targetMenu, PreviewCategoryData categoryData, string categoryKey, bool parityEligible, bool onlyOnThisSide, Color tint)
        {
            if (categoryData.Menu.Items.Count == 0) return;
            SelectPreviewRow(categoryData.Menu, 0);
            categoryData.SubmenuItem.Title = categoryKey;
            targetMenu.Items.Add(categoryData.SubmenuItem);
            if (parityEligible) ApplyItemStateColor(categoryData.SubmenuItem, onlyOnThisSide, tint, UnfocusedDiffColor(tint));
        }

        // Each slot reuses its pooled NativeItems instead of constructing fresh rows every refresh
        // They are the "Weapon Slot N (Preview/Diff)" ItemPool that ConfigurePanelMenu registers
        // wireSlot wires a slot's field row for Selective Copy, once per pooled item on first construction, and is null on Diff's read-only side
        private int PopulateWeaponSlotMenus(List<List<PreviewField>> slots, NativeMenu[] slotMenus, NativeSubmenuItem[] slotSubmenuItems, NativeMenu weaponMenu, Dictionary<string, PreviewCategoryData> categoryDataMap, string slotKeySuffix, Action<NativeItem> wireSlot)
        {
            int desired = Math.Min(slots.Count, slotMenus.Length);
            for (int i = 0; i < desired; i++)
            {
                NativeMenu slotMenu = slotMenus[i];
                ClearPreviewMenu(slotMenu);
                slotMenu.Name = string.Format(TITLE_WEAPON_SLOT, (i + 1));
                List<NativeItem> itemPool = categoryDataMap[string.Format(TITLE_WEAPON_SLOT, (i + 1)) + slotKeySuffix].ItemPool;
                List<PreviewField> slotFields = slots[i];
                for (int f = 0; f < slotFields.Count; f++)
                {
                    NativeItem fieldItem;
                    if (f < itemPool.Count) fieldItem = itemPool[f];
                    else
                    {
                        fieldItem = new NativeItem("");
                        itemPool.Add(fieldItem);
                        CaptureOriginalItemColor(fieldItem);
                        if (wireSlot != null) wireSlot(fieldItem);
                    }
                    fieldItem.Title = slotFields[f].Title;
                    SetPreviewRowValue(fieldItem, slotFields[f].Value);
                    fieldItem.Tag = slotFields[f];
                    slotMenu.Items.Add(fieldItem);
                }
                if (slotMenu.Items.Count > 0) SelectPreviewRow(slotMenu, 0);
                RecalculatePreviewTargetWidth(slotMenu);
                slotSubmenuItems[i].Title = string.Format(TITLE_WEAPON_SLOT, (i + 1));
                weaponMenu.Items.Add(slotSubmenuItems[i]);
            }
            return desired;
        }

        // The deliberate Preview on/off toggle, available for a saved file and a search result alike
        private void TogglePreviewWanted()
        {
            navigation.PreviewWanted = !navigation.PreviewWanted;
            RefreshPreviewMenu();
        }

        // Repopulates the Preview panel from the currently highlighted saved handling
        private void RefreshPreviewMenu() { RefreshPreviewMenu(cachedSavedHandlingKeys); }

        // The library's value text setter lays the row out again and measures the text through the game, once per row on every rebuild
        // Writing the row's text element directly skips that, and every menu whose rows change is laid out once by its row select afterward
        // That layout covers only the rows on screen, and a scroll lays out the rows it brings into view
        private void SetPreviewRowValue(NativeItem item, string value)
        {
            LemonUI.Elements.ScaledText valueText = ItemAltTitleField != null ? ItemAltTitleField.GetValue(item) as LemonUI.Elements.ScaledText : null;
            if (valueText == null) item.AltTitle = value;
            else if (valueText.Text != value) valueText.Text = value;
        }

        // Menus cleared since their last row select, so their new rows still need the select's layout
        private readonly HashSet<NativeMenu> previewMenusNeedingLayout = new HashSet<NativeMenu>();

        private void ClearPreviewMenu(NativeMenu menu)
        {
            menu.Clear();
            previewMenusNeedingLayout.Add(menu);
        }

        // The library's row select lays the whole menu out again even when the row does not change
        // A menu whose rows are unchanged and whose row is already the one asked for is left as it is
        private void SelectPreviewRow(NativeMenu menu, int index)
        {
            if (!previewMenusNeedingLayout.Remove(menu) && menu.SelectedIndex == index) return;
            menu.SelectedIndex = index;
        }

        private void RefreshPreviewMenu(SavedHandlingKey[] savedHandlings)
        {
            // Before the hide below, since the position walks up from the visible category menu
            RememberPreviewPosition();
            // A category submenu stays Visible (just dimmed) after unfocusing, by design
            // Scrolling to a different saved handling while still viewing one fires this same refresh, which clears and repopulates every category's content
            // A submenu left Visible here would stack underneath previewMenu's unconditional re-show below with invalidated content
            // The hide runs as a refresh hide, which leaves focus alone, and the re-show below reapplies it to previewMenu
            NativeMenu previouslyVisiblePreviewMenu = GetVisiblePreviewMenu();
            if (previouslyVisiblePreviewMenu != null && previouslyVisiblePreviewMenu != previewMenu) HidePreviewPanelForRefresh(previouslyVisiblePreviewMenu);
            // A result is built from the model's live handling, so every refresh reads it fresh instead of reusing an earlier parse
            if (browsedSource == searchSource) searchPreviewCache.Clear();
            ClearPreviewMenu(previewMenu);
            // Every previewCategoryData entry clears lazily, only when its own values changed from the previous saved handling
            // That covers the 3 top-level flag categories, the render-order categories, and the menus nested inside them (AdvancedFlags, AdvancedData, SpecialFlightFlags)
            int savedHandlingIndex = savedHandlingsMenu.SelectedIndex - SavedHandlingRowOffset;
            if (!navigation.PreviewWanted || savedHandlingIndex < 0 || savedHandlingIndex >= savedHandlings.Length)
            {
                // Hides whichever Preview-tree menu is on screen, not just previewMenu, so toggling Preview off from any nesting depth fully closes the tree
                // A submenu left visible would show its just-cleared, now-empty item list
                ClosePreviewTree();
                return;
            }
            SavedHandlingKey savedKey = savedHandlings[savedHandlingIndex];
            previewShownSavedHandlingKey = savedKey;
            previewMenu.Name = savedKey.Name;
            List<PreviewField> fields = GetHandlingPreviewCache(savedKey).Fields;
            // Row-count parity: Both panels must show one row per top-level field and per sub-handling or Weapon category that exists on *either* side
            // That keeps the passive index-based mirror from drifting
            // The baseline and its cached parse are resolved once here, before any row is built, and handed to RefreshPreviewDiff below
            // Search results have no Diff, since a result is a model to browse and copy from, not a saved handling to compare with the vehicle's original handling
            XDocument parityBaseline = showPreviewDiff && savedKey.Source.Has(SavedHandlingCapabilities.Diff) ? ResolvePreviewDiffSnapshot(savedKey) : null;
            PreviewDiffBaselineParse baselineParse = parityBaseline != null ? GetPreviewDiffBaselineParse(parityBaseline) : null;
            List<PreviewField> baselineFields = baselineParse != null ? baselineParse.Fields : null;
            List<List<PreviewField>> baselineWeaponSlots = baselineParse != null ? baselineParse.WeaponSlots : null;
            List<PreviewField> topLevelFieldOrder = BuildTopLevelFieldOrder(fields, baselineFields);
            Dictionary<string, List<PreviewField>> categoryFields = BuildCategoryFieldsAndBaseRows(fields, previewCategoryData, previewMenu, previewItemPool, WireSelectiveCopy, topLevelFieldOrder, previewBasePlaceholderPool, DIFF_PREVIEW_COLOR, "");
            // Hoisted to method scope so RefreshPreviewDiff and SyncPreviewDiffColoring reuse the same parse instead of reparsing the file from disk a second time
            // It is only assigned inside the Weapon category's branch below
            List<List<PreviewField>> weaponSlotsThisRefresh = new List<List<PreviewField>>();
            HashSet<string> baselineCategoryKeys = ExtractCategoryKeys(baselineFields);
            bool diffActiveForParity = parityBaseline != null;
            // Iterates in the editor's own canonical category order (PreviewCategoryOrder, MenuBase.cs) plus Weapon appended last
            // categoryFields' dictionary insertion order follows BuildHandlingXml's XML write sequence, so using it would not match the live editor's own order
            string weaponLongName = ResolveCategoryDefinition(CATEGORY_WEAPON).LongName;
            for (int categoryIndex = 0; categoryIndex < PreviewCategoryRenderOrder.Length; categoryIndex++)
            {
                string categoryKey = PreviewCategoryRenderOrder[categoryIndex];
                // Flag categories always exist on both a valid saved handling and a valid baseline, so they never need a placeholder
                // Only the 10 sub-handling categories, Weapon included, can differ between the two sides
                bool parityEligible = Array.IndexOf(PreviewFlagFieldNames, categoryKey) < 0;
                PreviewCategoryData categoryData;
                bool haveCategoryData = previewCategoryData.TryGetValue(categoryKey, out categoryData);
                List<PreviewField> categoryValue;
                if (!categoryFields.TryGetValue(categoryKey, out categoryValue))
                {
                    // Preview lacks this category. When Diff's baseline has it, add a green placeholder so both panels keep matching row counts
                    AddParityPlaceholderIfNeeded(previewMenu, categoryData, parityEligible, haveCategoryData, diffActiveForParity && baselineCategoryKeys.Contains(categoryKey));
                    // Only clear on the actual present-to-absent transition (the dictionary held a last-rendered snapshot to remove)
                    // A category already absent on the previous call has nothing left to clear, so a run of consecutive saved handlings all lacking it costs nothing here
                    if (haveCategoryData && previewCategoryLastFields.Remove(categoryKey)) ClearPreviewMenu(categoryData.Menu);
                    continue;
                }
                if (!haveCategoryData) continue;
                NativeMenu categoryMenu = categoryData.Menu;
                categoryMenu.Name = categoryKey;
                List<PreviewField> lastCategoryFields;
                bool categoryUnchanged = categoryMenu.Items.Count > 0 && previewCategoryLastFields.TryGetValue(categoryKey, out lastCategoryFields) && CategoryFieldsEqual(lastCategoryFields, categoryValue);
                // Weapon's own scalar check above can't see a per-slot-only change, since per-slot data is a wholly separate parse
                if (categoryKey == weaponLongName) weaponSlotsThisRefresh = GetHandlingPreviewCache(savedKey).WeaponSlots;
                if (categoryUnchanged && categoryKey == weaponLongName) categoryUnchanged = WeaponSlotsEqual(previewLastWeaponSlots, weaponSlotsThisRefresh);
                if (!categoryUnchanged)
                {
                    // Weapon's per-slot submenus append after its 3 whole-category scalars, which the generic row loop populates like any other category
                    Action<NativeMenu> populateWeaponSlots = null;
                    if (categoryKey == weaponLongName) populateWeaponSlots = menu =>
                    {
                        PopulatePreviewWeaponSlots(weaponSlotsThisRefresh, menu);
                        previewLastWeaponSlots = weaponSlotsThisRefresh;
                    };
                    PopulateCategoryRows(previewCategoryData, categoryKey, categoryMenu, categoryData, categoryValue, WireSelectiveCopy, populateWeaponSlots);
                    previewCategoryLastFields[categoryKey] = categoryValue;
                }
                // Green when this category lives only on Preview's side (Diff's baseline lacks it), and reverts to its captured original otherwise
                FinalizeCategoryPlacement(previewMenu, categoryData, categoryKey, parityEligible, diffActiveForParity && !baselineCategoryKeys.Contains(categoryKey), DIFF_PREVIEW_COLOR);
            }
            PreviewPosition rememberedPosition;
            previewPositionBySavedHandling.TryGetValue(savedKey, out rememberedPosition);
            if (previewMenu.Items.Count > 0) SelectPreviewRow(previewMenu, rememberedPosition != null ? Math.Max(0, Math.Min(rememberedPosition.TopRow, previewMenu.Items.Count - 1)) : 0);
            RecalculatePreviewTargetWidth(previewMenu);
            RefreshPreviewDiff(fields, weaponSlotsThisRefresh, parityBaseline, baselineFields, baselineWeaponSlots, topLevelFieldOrder);
            // After RefreshPreviewDiff, not before: It repopulates the Diff tree's items, which this call then dims in step with Preview's own focus state
            SyncPreviewEnabled();
            RepositionPreviewMenu();
            ShowMenu(previewMenu);
            // The current focus, not the remembered one, so a scroll in the list never moves input into Preview
            if (rememberedPosition != null && rememberedPosition.Path.Count > 0) ApplyPreviewPosition(rememberedPosition, navigation.PreviewFocused);
            // Focus is reapplied to the menu now showing, so a refresh that hid a focused category never strands input with no menu
            FocusPreview(navigation.PreviewFocused);
            // A panel swap hid the old panels before their turn to draw, so the new ones draw this frame in their place
            // A first open keeps the gate, since the menu that opened Saved Handlings has already drawn
            if (previouslyVisiblePreviewMenu == null) return;
            NativeMenu visiblePreview = GetVisiblePreviewMenu();
            SyncDiffMirror(visiblePreview);
            ReleaseFrameGate(visiblePreview);
            if (diffBaselineActive) ReleaseFrameGate(lastShownDiffMenu);
        }

        private static void ReleaseFrameGate(NativeMenu menu)
        {
            FrameGatedMenu gatedMenu = menu as FrameGatedMenu;
            if (gatedMenu != null) gatedMenu.ReleaseGate();
        }

        private void PopulatePreviewWeaponSlots(List<List<PreviewField>> slots, NativeMenu weaponMenu)
        {
            PopulateWeaponSlotMenus(slots, previewWeaponSlotMenus, previewWeaponSlotSubmenuItems, weaponMenu, previewCategoryData, SUFFIX_PREVIEW, WireSelectiveCopyWeaponSlot);
        }

        private static uint ParseFlagHex(string hexValue)
        {
            uint flags = 0;
            if (hexValue != null && hexValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint parsed;
                if (uint.TryParse(hexValue.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) flags = parsed;
            }
            return flags;
        }

        private void PopulatePreviewFlagMenuFromEntries(PreviewCategoryData data, FlagNameEntry[] entries, string hexValue, bool wireForSelectiveCopy)
        {
            uint flags = ParseFlagHex(hexValue);
            for (int i = 0; i < entries.Length; i++)
            {
                NativeCheckboxItem checkbox;
                if (i < data.CheckboxPool.Count) checkbox = data.CheckboxPool[i];
                else
                {
                    var flagCheckbox = new PreviewFlagCheckbox();
                    SubscribeCheckboxChanged(flagCheckbox, "PopulatePreviewFlagMenuFromEntries.CheckboxChanged", flagCheckbox.ApplyBadge);
                    checkbox = flagCheckbox;
                    checkbox.Enabled = false;
                    if (wireForSelectiveCopy) checkbox.Description = DESC_COPY_VALUE;
                    data.CheckboxPool.Add(checkbox);
                }
                checkbox.Title = StripFlagPrefix(entries[i].Name);
                checkbox.Checked = (flags & (1u << entries[i].BitIndex)) != 0;
                data.Menu.Items.Add(checkbox);
            }
            if (data.Menu.Items.Count > 0) SelectPreviewRow(data.Menu, 0);
        }

        // Each checkbox's index in CheckboxPool is its entry's index, as PopulatePreviewFlagMenuFromEntries built them
        // Only the highlight returns to the top, as a rebuilt menu's would
        private void TickPreviewFlagRows(PreviewCategoryData data, FlagNameEntry[] entries, string hexValue)
        {
            uint flags = ParseFlagHex(hexValue);
            for (int i = 0; i < entries.Length; i++) data.CheckboxPool[i].Checked = (flags & (1u << entries[i].BitIndex)) != 0;
            SelectPreviewRow(data.Menu, 0);
        }

        // Checkbox rows never go through the real Enabled toggle below, because NativeCheckboxItem's constructor wires Activated to flip Checked internally
        // Enabled = True would make a read-only preview row toggleable the moment it is focused and pressed
        // The library draws a disabled checkbox with its light texture and a white badge whatever the highlight, so this class picks both itself
        private class PreviewFlagCheckbox : NativeCheckboxItem
        {
            // True while Preview is unfocused, so the badge dims with the row's title
            public bool Dimmed;

            public PreviewFlagCheckbox() : base("", false)
            {
            }

            // Runs after every library path that repaints the badge: UpdateColors, the texture pick at the end of Recalculate, and the Checked setter's own pick
            // The Checked setter's pick runs through CheckboxChanged, which the code that builds each checkbox subscribes this to
            public void ApplyBadge()
            {
                BadgeSet set = Checked ? CheckedSet : UncheckedSet;
                check.Texture = lastSelected && !Dimmed ? set.HoveredTexture : set.NormalTexture;
                check.Color = Dimmed ? DIMMED_TEXT_COLOR : Color.White;
            }

            public override void Recalculate(PointF pos, SizeF size, bool selected)
            {
                base.Recalculate(pos, size, selected);
                ApplyBadge();
            }

            public override void UpdateColors()
            {
                base.UpdateColors();
                ApplyBadge();
            }
        }

        // Difference cues: Green marks the Preview side, red the Diff side
        // Each has a darker variant shown while Preview is unfocused
        // An unfocused row is Enabled = false and paints its Disabled tier, which would otherwise swallow the cue into the generic dim
        private static readonly Color DIFF_PREVIEW_COLOR = Color.LimeGreen;
        private static readonly Color DIFF_BASELINE_COLOR = Color.Red;
        private static readonly Color DIFF_PREVIEW_UNFOCUSED_COLOR = Color.ForestGreen;
        private static readonly Color DIFF_BASELINE_UNFOCUSED_COLOR = Color.DarkRed;
        private static Color UnfocusedDiffColor(Color focusedColor)
        {
            return focusedColor == DIFF_PREVIEW_COLOR ? DIFF_PREVIEW_UNFOCUSED_COLOR : DIFF_BASELINE_UNFOCUSED_COLOR;
        }

        // A placeholder row stays Enabled = false, so it can never be activated or descended into
        // Its color lives on the Disabled tier, tinted green or red when Preview is focused and the darker variant when not
        // Every other row toggles Enabled directly with focus
        private void ApplyPreviewRowEnabled(NativeItem item)
        {
            Color tint;
            if (placeholderTints.TryGetValue(item, out tint))
            {
                item.Colors.TitleDisabled = navigation.PreviewFocused ? tint : UnfocusedDiffColor(tint);
                item.UpdateColors();
                return;
            }
            item.Enabled = navigation.PreviewFocused;
        }

        // Every preview category submenu follows the focus too, so drilling into a category reads like the top-level rows' dim-until-focused behavior
        // Flag checkboxes stay permanently Enabled = false (see PreviewFlagCheckbox above) and light up through SyncPreviewFlagCheckboxColors instead
        // The Diff tree mirrors the same Enabled toggle, so it dims exactly when Preview does
        // RefreshPreviewDiff populates Diff's items, so the rebuild caller runs this after that populate, not before it
        private void SyncPreviewEnabled()
        {
            for (int i = 0; i < previewMenu.Items.Count; i++) ApplyPreviewRowEnabled(previewMenu.Items[i]);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData)
            {
                for (int i = 0; i < data.Value.Menu.Items.Count; i++)
                {
                    NativeItem item = data.Value.Menu.Items[i];
                    if (!(item is NativeCheckboxItem)) item.Enabled = navigation.PreviewFocused;
                }
                SyncPreviewFlagCheckboxColors(data.Value.Menu);
            }
            for (int i = 0; i < diffMenu.Items.Count; i++) ApplyPreviewRowEnabled(diffMenu.Items[i]);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData)
            {
                for (int i = 0; i < data.Value.Menu.Items.Count; i++)
                {
                    NativeItem item = data.Value.Menu.Items[i];
                    if (!(item is NativeCheckboxItem)) item.Enabled = navigation.PreviewFocused;
                }
                SyncPreviewFlagCheckboxColors(data.Value.Menu);
            }
        }

        // NativeItem.UpdateColors() paints the Disabled-tier title color whenever Enabled is False, whatever TitleNormal and TitleHovered hold
        // So lighting up a permanently disabled checkbox means substituting what Disabled itself looks like, not touching Enabled
        // The API picks TitleHovered or TitleNormal by selection for a real Enabled = True row, but here it is done manually per row and on every SelectedIndexChanged
        // That gives the highlighted checkbox TitleHovered's dark color for contrast against the menu's white selection highlight
        private void SyncPreviewFlagCheckboxColors(NativeMenu menu)
        {
            NativeItem selected = menu.SelectedItem;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                NativeCheckboxItem checkbox = menu.Items[i] as NativeCheckboxItem;
                if (checkbox == null) continue;
                // A bit that differs between Preview and Diff takes its tint instead, bright while Preview is focused and the darker variant while it is not
                Color tint;
                if (flagBitTints.TryGetValue(checkbox, out tint)) checkbox.Colors.TitleDisabled = navigation.PreviewFocused ? tint : UnfocusedDiffColor(tint);
                else if (!navigation.PreviewFocused) checkbox.Colors.TitleDisabled = DIMMED_TEXT_COLOR;
                else checkbox.Colors.TitleDisabled = checkbox == selected ? checkbox.Colors.TitleHovered : checkbox.Colors.TitleNormal;
                PreviewFlagCheckbox flagCheckbox = checkbox as PreviewFlagCheckbox;
                if (flagCheckbox != null) flagCheckbox.Dimmed = !navigation.PreviewFocused;
                checkbox.UpdateColors();
            }
        }

        // previewMenu sits adjacent to savedHandlingsMenu, on the side away from the screen edge
        // Its offset is recomputed every tick while both are visible, because savedHandlingsMenu's width animates
        // The visible Preview menu is looked up once here and shared with Diff's reposition and mirror
        private void RepositionPreviewMenu()
        {
            float previewOffsetX = menuAlignLeft ? savedHandlingsMenu.Offset.X + savedHandlingsMenu.Width + PREVIEW_PANEL_SPACING : savedHandlingsMenu.Offset.X - savedHandlingsMenu.Width - PREVIEW_PANEL_SPACING;
            PointF previewOffset = new PointF(previewOffsetX, savedHandlingsMenu.Offset.Y);
            SetPanelOffset(previewMenu, previewOffset);
            // Only the currently visible submenu needs its Offset touched
            // Every weapon-slot menu is reached here too, since ConfigurePanelMenu registers each one into previewCategoryData
            // A separate loop over previewWeaponSlotMenus would touch a visible one twice
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) { if (data.Value.Menu.Visible) SetPanelOffset(data.Value.Menu, previewOffset); }
            NativeMenu visiblePreview = GetVisiblePreviewMenu();
            // Diff has no per-tick reposition call site of its own
            // Piggybacking on Preview's (called every tick while any part of the Preview tree is visible) keeps the two from drifting out of sync
            if (diffBaselineActive) RepositionDiffMenu(visiblePreview);
            SyncDiffMirror(visiblePreview);
        }

        // The library's Offset setter recalculates every element of the menu, so an unchanged offset is never written
        // The banner text is recentered after a write, since the recalculation moves it back to the library's own position
        private void SetPanelOffset(NativeMenu menu, PointF offset)
        {
            if (menu.Offset == offset) return;
            menu.Offset = offset;
            RecenterBannerText(menu);
        }

        // Diff sits one column further out than Preview, on the same side away from Saved Handlings
        private void RepositionDiffMenu(NativeMenu visiblePreview)
        {
            // Offsets from whichever Preview submenu is on screen, not always previewMenu's top-level width
            // A nested submenu wider or narrower than the top-level panel then still sits flush beside Diff
            // Every Preview-tree menu shares previewMenu's Offset
            float previewWidth = visiblePreview != null ? visiblePreview.Width : previewMenu.Width;
            float diffOffsetX = menuAlignLeft ? previewMenu.Offset.X + previewWidth + PREVIEW_PANEL_SPACING : previewMenu.Offset.X - previewWidth - PREVIEW_PANEL_SPACING;
            PointF diffOffset = new PointF(diffOffsetX, previewMenu.Offset.Y);
            SetPanelOffset(diffMenu, diffOffset);
            // previewDiffCategoryData already reaches every Diff weapon-slot menu too
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) { if (data.Value.Menu.Visible) SetPanelOffset(data.Value.Menu, diffOffset); }
        }

        // ====================
        // ShowPreviewDiff panel
        // ====================
        // This is the no-args entry point for callers that don't have the highlighted saved handling on hand
        // The visible branch rebuilds Preview as well, since a baseline appearing or disappearing changes Preview's own placeholder rows too
        private void SyncDiffVisibility()
        {
            if (!previewMenu.Visible || string.IsNullOrEmpty(previewMenu.Name))
            {
                diffBaselineActive = false;
                HideDiffCategoryMenus();
                HideMenu(diffMenu);
                lastShownDiffMenu = null;
                SyncPreviewDiffColoring(null, new List<List<PreviewField>>(), null, new List<PreviewField>(), new List<List<PreviewField>>());
                return;
            }
            RefreshPreviewMenu();
        }

        // A visibility gate only, with no capture side effect and no live vehicle involved
        private XDocument ResolvePreviewDiffSnapshot(SavedHandlingKey savedKey)
        {
            string handlingName = GetHandlingPreviewCache(savedKey).HandlingName;
            if (string.IsNullOrEmpty(handlingName)) return null;
            XDocument baseline;
            return previewDiffSnapshots.TryGetValue(modelNames.ResolveHandlingKeyFromName(handlingName), out baseline) ? baseline : null;
        }

        // Base fields and flag fields carry no category and contribute nothing
        // Drives row-count parity by exposing which sub-handling and Weapon categories each side's data contains
        private static HashSet<string> ExtractCategoryKeys(List<PreviewField> fields)
        {
            var keys = new HashSet<string>();
            if (fields == null) return keys;
            for (int i = 0; i < fields.Count; i++) { if (fields[i].Ref.Category != null) keys.Add(ResolveCategoryLongName(fields[i].Ref.Category)); }
            return keys;
        }

        // A lookup miss simply leaves Diff hidden, with no error state
        // RefreshPreviewMenu resolves the baseline and its cached parse, so baselineFields and baselineWeaponSlots are null exactly when baseline is
        private void RefreshPreviewDiff(List<PreviewField> previewFields, List<List<PreviewField>> previewWeaponSlots, XDocument baseline, List<PreviewField> baselineFields, List<List<PreviewField>> baselineWeaponSlots, List<PreviewField> topLevelFieldOrder)
        {
            if (baseline == null)
            {
                diffBaselineActive = false;
                HideDiffCategoryMenus();
                HideMenu(diffMenu);
                lastShownDiffMenu = null;
                SyncPreviewDiffColoring(previewFields, previewWeaponSlots, null, new List<PreviewField>(), new List<List<PreviewField>>());
                return;
            }
            // Diff needs the categories Preview is showing to place its red placeholders and colors
            // previewFields is the flat list Preview rendered from, so its category-key set is Preview's present set without re-parsing the saved handling
            PopulateDiffMenu(baselineFields, baselineWeaponSlots, ExtractCategoryKeys(previewFields), topLevelFieldOrder);
            SyncPreviewDiffColoring(previewFields, previewWeaponSlots, baseline, baselineFields, baselineWeaponSlots);
            diffBaselineActive = true;
            // Hidden before the show, so a visible category's Closed writes the width seed diffMenu's Shown starts from
            HideDiffCategoryMenus();
            ShowMenu(diffMenu);
            lastShownDiffMenu = diffMenu;
        }

        // The baseline is the vehicle's original handling captured at scope time
        // This mirrors RefreshPreviewMenu's population against the live saved handling, so the two trees stay in lockstep for SyncDiffMirror's passive navigation
        private void PopulateDiffMenu(List<PreviewField> baselineFields, List<List<PreviewField>> baselineWeaponSlots, HashSet<string> handlingCategoryKeys, List<PreviewField> topLevelFieldOrder)
        {
            ClearPreviewMenu(diffMenu);
            diffBaselineOnlyCategoryRows.Clear();
            // Every previewDiffCategoryData entry clears lazily, only when its own values changed from the previous baseline
            // That covers the 3 top-level flag categories, the render-order categories, and the menus nested inside them (AdvancedFlags, AdvancedData, SpecialFlightFlags)
            Dictionary<string, List<PreviewField>> categoryFields = BuildCategoryFieldsAndBaseRows(baselineFields, previewDiffCategoryData, diffMenu, diffItemPool, null, topLevelFieldOrder, diffBasePlaceholderPool, DIFF_BASELINE_COLOR, PLACEHOLDER_DIFF_NOT_PRESENT);
            string weaponLongName = ResolveCategoryDefinition(CATEGORY_WEAPON).LongName;
            for (int categoryIndex = 0; categoryIndex < PreviewCategoryRenderOrder.Length; categoryIndex++)
            {
                string categoryKey = PreviewCategoryRenderOrder[categoryIndex];
                bool parityEligible = Array.IndexOf(PreviewFlagFieldNames, categoryKey) < 0;
                PreviewCategoryData categoryData;
                bool haveCategoryData = previewDiffCategoryData.TryGetValue(categoryKey, out categoryData);
                List<PreviewField> categoryValue;
                if (!categoryFields.TryGetValue(categoryKey, out categoryValue))
                {
                    // Diff's baseline lacks this category. When Preview has it, add a red placeholder so both panels keep matching row counts
                    AddParityPlaceholderIfNeeded(diffMenu, categoryData, parityEligible, haveCategoryData, handlingCategoryKeys.Contains(categoryKey));
                    // Only clear on the actual present-to-absent transition (the dictionary held a last-rendered snapshot to remove)
                    // A baseline already absent on the previous call has nothing left to clear, so a run of consecutive baselines all lacking it costs nothing here
                    if (haveCategoryData && previewDiffCategoryLastFields.Remove(categoryKey)) ClearPreviewMenu(categoryData.Menu);
                    continue;
                }
                if (!haveCategoryData) continue;
                NativeMenu categoryMenu = categoryData.Menu;
                categoryMenu.Name = categoryKey;
                List<PreviewField> lastCategoryFields;
                bool categoryUnchanged = categoryMenu.Items.Count > 0 && previewDiffCategoryLastFields.TryGetValue(categoryKey, out lastCategoryFields) && CategoryFieldsEqual(lastCategoryFields, categoryValue);
                // Weapon's own scalar check above can't see a per-slot-only change, since per-slot data is a wholly separate parse
                if (categoryUnchanged && categoryKey == weaponLongName) categoryUnchanged = WeaponSlotsEqual(previewDiffLastWeaponSlots, baselineWeaponSlots);
                if (!categoryUnchanged)
                {
                    Action<NativeMenu> populateWeaponSlots = null;
                    if (categoryKey == weaponLongName) populateWeaponSlots = menu =>
                    {
                        PopulateDiffWeaponSlots(baselineWeaponSlots, menu);
                        previewDiffLastWeaponSlots = baselineWeaponSlots;
                    };
                    PopulateCategoryRows(previewDiffCategoryData, categoryKey, categoryMenu, categoryData, categoryValue, null, populateWeaponSlots);
                    previewDiffCategoryLastFields[categoryKey] = categoryValue;
                }
                // Red when this category lives only on Diff's baseline (the current saved handling lacks it), and reverts to its captured original otherwise
                bool baselineOnly = !handlingCategoryKeys.Contains(categoryKey);
                FinalizeCategoryPlacement(diffMenu, categoryData, categoryKey, parityEligible, baselineOnly, DIFF_BASELINE_COLOR);
                if (parityEligible && baselineOnly && categoryData.Menu.Items.Count > 0) diffBaselineOnlyCategoryRows.Add(categoryData.SubmenuItem);
            }
            if (diffMenu.Items.Count > 0) SelectPreviewRow(diffMenu, Math.Max(0, Math.Min(diffMenu.SelectedIndex, diffMenu.Items.Count - 1)));
            RecalculatePreviewTargetWidth(diffMenu);
        }

        private void PopulateDiffWeaponSlots(List<List<PreviewField>> baselineWeaponSlots, NativeMenu weaponMenu)
        {
            PopulateWeaponSlotMenus(baselineWeaponSlots, diffWeaponSlotMenus, diffWeaponSlotSubmenuItems, weaponMenu, previewDiffCategoryData, SUFFIX_DIFF, null);
        }

        // A row with no field (a category or weapon slot navigation row) keeps its normal color, and a field present on only one side counts as different
        // Flag checkbox rows are colored per bit by ColorFlagCheckboxes
        // When baseline is null (no match, or ShowPreviewDiff off), every override on Preview's side is reverted, since there is nothing left to compare against
        private void SyncPreviewDiffColoring(List<PreviewField> previewFields, List<List<PreviewField>> previewWeaponSlots, XDocument baseline, List<PreviewField> baselineFields, List<List<PreviewField>> baselineWeaponSlots)
        {
            if (baseline == null)
            {
                RevertColorFieldItems(previewMenu.Items);
                foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) RevertColorFieldItems(data.Value.Menu.Items);
                for (int i = 0; i < previewWeaponSlotMenus.Length; i++) RevertColorFieldItems(previewWeaponSlotMenus[i].Items);
                flagBitTints.Clear();
                SyncAllPreviewFlagCheckboxColors();
                return;
            }
            Dictionary<FieldRef, string> previewMap = ToFieldMap(previewFields, previewWeaponSlots);
            Dictionary<FieldRef, string> baselineMap = ToFieldMap(baselineFields, baselineWeaponSlots);
            ColorFieldItemsByRef(previewMenu.Items, previewMap, baselineMap, DIFF_PREVIEW_COLOR);
            ColorFieldItemsByRef(diffMenu.Items, previewMap, baselineMap, DIFF_BASELINE_COLOR);
            // Both dictionaries also hold every weapon slot and AdvancedData entry menu, so each of those rows is colored here too
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) ColorFieldItemsByRef(data.Value.Menu.Items, previewMap, baselineMap, DIFF_PREVIEW_COLOR);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) ColorFieldItemsByRef(data.Value.Menu.Items, previewMap, baselineMap, DIFF_BASELINE_COLOR);
            ColorFlagCheckboxes(previewMap, baselineMap);
            UpdateDiffBannerCounts(previewMap, baselineMap);
        }

        // A ref listed twice keeps its last value
        private static Dictionary<FieldRef, string> ToFieldMap(List<PreviewField> fields, List<List<PreviewField>> weaponSlots)
        {
            var map = new Dictionary<FieldRef, string>();
            if (fields != null) AddFieldsToMap(map, fields);
            if (weaponSlots != null) for (int slot = 0; slot < weaponSlots.Count; slot++) AddFieldsToMap(map, weaponSlots[slot]);
            return map;
        }

        private static void AddFieldsToMap(Dictionary<FieldRef, string> map, List<PreviewField> fields)
        {
            for (int i = 0; i < fields.Count; i++) map[fields[i].Ref] = fields[i].Value;
        }

        // Flag checkboxes are colored per bit by ColorFlagCheckboxes instead
        // Only a plain field row (guaranteed CaptureOriginalItemColor'd at its own first construction) is safe to color here
        private void ColorFieldItemsByRef(IList<NativeItem> items, Dictionary<FieldRef, string> previewMap, Dictionary<FieldRef, string> baselineMap, Color overrideColor)
        {
            for (int i = 0; i < items.Count; i++)
            {
                NativeItem item = items[i];
                PreviewField field = item.Tag as PreviewField;
                if (field == null || item is NativeSubmenuItem || item is NativeCheckboxItem || placeholderTints.ContainsKey(item)) continue;
                ApplyItemStateColor(item, IsFieldRowDifferent(field, previewMap, baselineMap), overrideColor, UnfocusedDiffColor(overrideColor));
            }
        }

        // A row differs when its field's value differs between the 2 maps, a field present on only one side included, while a field on neither side stays plain
        private static bool IsFieldRowDifferent(PreviewField field, Dictionary<FieldRef, string> previewMap, Dictionary<FieldRef, string> baselineMap)
        {
            string previewValue, baselineValue;
            previewMap.TryGetValue(field.Ref, out previewValue);
            baselineMap.TryGetValue(field.Ref, out baselineValue);
            return previewValue != baselineValue;
        }

        // Each Diff menu's banner counts the rows of that menu that themselves show a difference
        // A submenu row whose contents differ adds nothing, since the menu it opens shows its own count
        // Every Diff menu is recounted on each coloring, including one the lazy repopulate left holding the last baseline's rows
        private void UpdateDiffBannerCounts(Dictionary<FieldRef, string> previewMap, Dictionary<FieldRef, string> baselineMap)
        {
            UpdateDiffBannerCount(diffMenu, previewMap, baselineMap);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) UpdateDiffBannerCount(data.Value.Menu, previewMap, baselineMap);
        }

        // The banner text is measured on assignment, and the width pass reads the banner, so both run only when the count changes
        private void UpdateDiffBannerCount(NativeMenu menu, Dictionary<FieldRef, string> previewMap, Dictionary<FieldRef, string> baselineMap)
        {
            int count = 0;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                NativeItem item = menu.Items[i];
                NativeCheckboxItem checkbox = item as NativeCheckboxItem;
                if (placeholderTints.ContainsKey(item) || diffBaselineOnlyCategoryRows.Contains(item) || (checkbox != null && flagBitTints.ContainsKey(checkbox)))
                {
                    count++;
                    continue;
                }
                PreviewField field = item.Tag as PreviewField;
                if (field != null && checkbox == null && !(item is NativeSubmenuItem) && IsFieldRowDifferent(field, previewMap, baselineMap)) count++;
            }
            string text = string.Format(TITLE_DIFF_COUNT, count);
            if (menu.BannerText.Text == text) return;
            menu.BannerText.Text = text;
            RecalculatePreviewTargetWidth(menu);
        }

        private void RevertColorFieldItems(IList<NativeItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                NativeItem item = items[i];
                if (item is NativeSubmenuItem || item is NativeCheckboxItem || placeholderTints.ContainsKey(item)) continue;
                // ApplyItemStateColor's revert branch (applyOverride: false) never reads overrideColor, so Color.Empty makes that explicit here
                ApplyItemStateColor(item, false, Color.Empty);
            }
        }

        // Flag bits carry no field of their own, so each flag category's checkboxes are colored by comparing the 2 sides' hex values bit by bit
        // A checkbox's index in its CheckboxPool is its entry's index in the category's FlagNameEntry table on both trees
        // A category missing on either side is skipped, since it already shows a placeholder row
        private void ColorFlagCheckboxes(Dictionary<FieldRef, string> previewMap, Dictionary<FieldRef, string> baselineMap)
        {
            flagBitTints.Clear();
            foreach (FlagFamily family in FlagFamilies) ColorFlagCheckboxCategory(family.MenuKey, family.Entries, new FieldRef(family.ElementCategory, family.XmlTag), previewMap, baselineMap);
            SyncAllPreviewFlagCheckboxColors();
        }

        private void ColorFlagCheckboxCategory(string menuKey, FlagNameEntry[] entries, FieldRef flagField, Dictionary<FieldRef, string> previewMap, Dictionary<FieldRef, string> baselineMap)
        {
            string previewHex, baselineHex;
            if (!previewMap.TryGetValue(flagField, out previewHex) || !baselineMap.TryGetValue(flagField, out baselineHex)) return;
            uint differingBits = ParseFlagHex(previewHex) ^ ParseFlagHex(baselineHex);
            if (differingBits == 0) return;
            TintDifferingFlagCheckboxes(previewCategoryData, menuKey, entries, differingBits, DIFF_PREVIEW_COLOR);
            TintDifferingFlagCheckboxes(previewDiffCategoryData, menuKey, entries, differingBits, DIFF_BASELINE_COLOR);
        }

        private void TintDifferingFlagCheckboxes(Dictionary<string, PreviewCategoryData> categoryDataMap, string menuKey, FlagNameEntry[] entries, uint differingBits, Color tint)
        {
            PreviewCategoryData data;
            if (!categoryDataMap.TryGetValue(menuKey, out data)) return;
            int count = Math.Min(data.CheckboxPool.Count, entries.Length);
            for (int i = 0; i < count; i++) if ((differingBits & (1u << entries[i].BitIndex)) != 0) flagBitTints[data.CheckboxPool[i]] = tint;
        }

        private void SyncAllPreviewFlagCheckboxColors()
        {
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) SyncPreviewFlagCheckboxColors(data.Value.Menu);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) SyncPreviewFlagCheckboxColors(data.Value.Menu);
        }

        // The Diff-tree menu currently shown by the mirror, which makes the per-tick sync idempotent so an unchanged view never re-transitions visibility
        private NativeMenu lastShownDiffMenu;
        // Passive navigation mirror: Diff never takes input focus itself
        // This per-tick call (piggybacked on RepositionPreviewMenu) is the only way its SelectedIndex or nested-category visibility ever changes
        // Visibility is toggled only when the shown menu changes: Re-showing the same menu every tick forces a redundant Visible False to True transition
        // That transition fires the API's ResetCursor (SET_CURSOR_POSITION) and locks the hardware cursor while a diff submenu is open
        private void SyncDiffMirror(NativeMenu visiblePreview)
        {
            if (!diffBaselineActive)
            {
                lastShownDiffMenu = null;
                return;
            }
            NativeMenu target;
            int sourceIndex = 0;
            bool mirrorIndex;
            if (visiblePreview == null || visiblePreview == previewMenu)
            {
                target = diffMenu;
                sourceIndex = previewMenu.SelectedIndex;
                mirrorIndex = true;
            }
            else
            {
                target = ResolveDiffMirrorTarget(visiblePreview);
                // A nested Preview submenu whose Diff twin is empty falls back to the top-level diffMenu
                // Its selection already sits on that category's placeholder row, so the nested field index must not be mirrored onto it
                mirrorIndex = target != diffMenu;
                if (mirrorIndex) sourceIndex = visiblePreview.SelectedIndex;
            }
            if (target != lastShownDiffMenu)
            {
                ShowDiffMirrorTarget(target);
                lastShownDiffMenu = target;
            }
            // SelectedIndex's setter has no unchanged-value guard, unlike Visible's
            // Reassigning the same index on a visible menu still plays the navigation sound and fires the change event every tick
            // So this only writes when the clamped value differs
            if (mirrorIndex && target.Items.Count > 0)
            {
                int clampedIndex = ClampMirrorIndex(target, sourceIndex);
                if (target.SelectedIndex != clampedIndex) target.SelectedIndex = clampedIndex;
            }
        }

        // A Diff menu mirrors the visible Preview submenu only while every level down to it is listed in the current Diff tree and has rows,
        // since a category, slot, or entry menu the baseline lacks keeps the rows of whichever baseline last filled it
        // Anything else stays on the top-level diffMenu rather than descending into stale rows
        // The level list is reused, since this runs every tick while a nested Preview submenu shows
        private readonly List<NativeMenu> diffMirrorPreviewLevels = new List<NativeMenu>();

        private NativeMenu ResolveDiffMirrorTarget(NativeMenu visiblePreview)
        {
            List<NativeMenu> previewLevels = diffMirrorPreviewLevels;
            previewLevels.Clear();
            NativeMenu level = visiblePreview;
            for (int depth = 0; level != previewMenu; depth++)
            {
                MenuNode node;
                if (depth >= MENU_REACHABILITY_MAX_DEPTH || !menuNodes.TryGetValue(level, out node) || node.ReopenTarget == null) return diffMenu;
                previewLevels.Insert(0, level);
                level = node.ReopenTarget;
            }
            NativeMenu diffParent = diffMenu;
            for (int i = 0; i < previewLevels.Count; i++)
            {
                NativeMenu diffLevel = ResolveDiffTwin(previewLevels[i]);
                if (diffLevel == null || diffLevel.Items.Count == 0 || FindSubmenuEntryIndex(diffParent, diffLevel) < 0) return diffMenu;
                diffParent = diffLevel;
            }
            return diffParent;
        }

        // The Diff menu that pairs with a Preview submenu. Weapon slot and AdvancedData entry menus are registered in previewCategoryData under "(Preview)" names
        // and the Diff side keys its own under "(Diff)" names, so they pair by index and the rest by category key
        private NativeMenu ResolveDiffTwin(NativeMenu previewLevel)
        {
            int slot = Array.IndexOf(previewWeaponSlotMenus, previewLevel);
            if (slot >= 0) return slot < diffWeaponSlotMenus.Length ? diffWeaponSlotMenus[slot] : null;
            int entry = previewAdvancedDataEntryMenus.IndexOf(previewLevel);
            if (entry >= 0) return entry < diffAdvancedDataEntryMenus.Count ? diffAdvancedDataEntryMenus[entry] : null;
            string key = FindPreviewCategoryKey(previewLevel);
            PreviewCategoryData diffData;
            return key != null && previewDiffCategoryData.TryGetValue(key, out diffData) ? diffData.Menu : null;
        }

        private static int ClampMirrorIndex(NativeMenu menu, int index) { return menu.Items.Count > 0 ? Math.Max(0, Math.Min(index, menu.Items.Count - 1)) : 0; }
    }
}
