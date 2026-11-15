using System;
using System.Collections.Generic;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The last typed text with tilde characters removed, since an unpaired one cuts a game string short
        // It is shown in the subtitle, and it is empty while the full list of models shows
        private string searchQueryText = "";
        // The result keys in list order, each carrying its model name and model hash, held in memory only and replaced by the next search
        private SavedHandlingKey[] searchResultKeys = new SavedHandlingKey[0];
        private NativeItem searchItem;
        private NativeItem newSearchRowItem;
        // Result rows are reused across refreshes, since the full list holds about a thousand rows and a new row per refresh would rebuild every one
        private readonly List<NativeItem> searchResultRowPool = new List<NativeItem>();
        // Whether the pooled rows currently read dimmed, kept so a refresh retints the pool only when the Active vehicle came or went
        private bool searchRowsDimmed;
        // A result row's title width by its text, since every width read is a native text measurement and the same names return on every list
        private readonly Dictionary<string, float> searchTitleWidths = new Dictionary<string, float>();
        // Preview's parse of each result, built from the model's live handling, and cleared on every Preview refresh while searching so values are never stale
        private readonly Dictionary<SavedHandlingKey, HandlingPreviewCache> searchPreviewCache = new Dictionary<SavedHandlingKey, HandlingPreviewCache>();
        // Every searchable model name, upper-cased, with its model hash, in name order, and built on first use
        private List<KeyValuePair<string, int>> vehicleModelNameIndex;
        // Every model whose handling resolves, in name order, built from the name index by the startup warm-up or on first use, whichever comes first
        private SavedHandlingKey[] allSearchResultKeys;
        // A partial build of that list: The index it walks, the next position, and the keys so far. Cleared with the index, so a new index restarts the walk
        private List<KeyValuePair<string, int>> searchKeyBuildIndex;
        private int searchKeyBuildPosition;
        private List<SavedHandlingKey> searchKeyBuildKeys;

        // The base-game model names (the API's VehicleHash list) merged with the ModelMetaData file's names
        // A name is kept only when it hashes back to its own model hash
        // A model that both lists name keeps the file's name, the same order ResolveModelName uses
        private List<KeyValuePair<string, int>> GetVehicleModelNameIndex()
        {
            if (vehicleModelNameIndex != null) return vehicleModelNameIndex;
            var namesByHash = new Dictionary<int, string>();
            foreach (string enumName in Enum.GetNames(typeof(VehicleHash)))
            {
                uint value = (uint)(VehicleHash)Enum.Parse(typeof(VehicleHash), enumName);
                if (GTA.StringHash.AtStringHash(enumName) != value) continue;
                namesByHash[(int)value] = enumName.ToUpperInvariant();
            }
            foreach (KeyValuePair<int, string> fileName in modelNames.GetModelMetaDataNames()) namesByHash[fileName.Key] = fileName.Value.ToUpperInvariant();
            var index = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<int, string> entry in namesByHash) index.Add(new KeyValuePair<string, int>(entry.Value, entry.Key));
            index.Sort((first, second) => string.CompareOrdinal(first.Key, second.Key));
            vehicleModelNameIndex = index;
            return index;
        }

        // The full list is built from the name index, so it goes whenever the index does
        private void DropVehicleModelNameIndex()
        {
            vehicleModelNameIndex = null;
            allSearchResultKeys = null;
            searchKeyBuildIndex = null;
            searchKeyBuildKeys = null;
        }

        // Later opens and narrowing searches read this list and make no native call
        private SavedHandlingKey[] GetAllSearchResultKeys()
        {
            AdvanceSearchResultKeyBuild(long.MaxValue);
            return allSearchResultKeys;
        }

        // A model only counts when it is a vehicle whose handling resolves through ResolveModelHandling, which never reads an add-on's unchecked index
        // Checks models until budgetTicks passes, at least one per call, and returns True once the list is published
        private bool AdvanceSearchResultKeyBuild(long budgetTicks)
        {
            if (allSearchResultKeys != null) return true;
            if (searchKeyBuildKeys == null)
            {
                searchKeyBuildIndex = GetVehicleModelNameIndex();
                searchKeyBuildPosition = 0;
                searchKeyBuildKeys = new List<SavedHandlingKey>();
            }
            long callStart = System.Diagnostics.Stopwatch.GetTimestamp();
            while (searchKeyBuildPosition < searchKeyBuildIndex.Count)
            {
                KeyValuePair<string, int> entry = searchKeyBuildIndex[searchKeyBuildPosition++];
                if (modelNames.ResolveModelHandling(entry.Value) != null) searchKeyBuildKeys.Add(new SavedHandlingKey(searchSource, entry.Key, entry.Value));
                if (System.Diagnostics.Stopwatch.GetTimestamp() - callStart >= budgetTicks) return false;
            }
            allSearchResultKeys = searchKeyBuildKeys.ToArray();
            searchKeyBuildIndex = null;
            searchKeyBuildKeys = null;
            return true;
        }

        // Names that start with the text list before names that only contain it, each group in name order
        // totalMatches is the full count, while the returned list stops at SEARCH_RESULT_LIMIT
        private List<SavedHandlingKey> FindVehicleModelMatches(string text, out int totalMatches)
        {
            string needle = text.Trim().ToUpperInvariant();
            var starting = new List<SavedHandlingKey>();
            var containing = new List<SavedHandlingKey>();
            foreach (SavedHandlingKey key in GetAllSearchResultKeys())
            {
                int position = key.Name.IndexOf(needle, StringComparison.Ordinal);
                if (position < 0) continue;
                if (position == 0) starting.Add(key);
                else containing.Add(key);
            }
            starting.AddRange(containing);
            totalMatches = starting.Count;
            return starting.Count > SEARCH_RESULT_LIMIT ? starting.GetRange(0, SEARCH_RESULT_LIMIT) : starting;
        }

        // The full list holds every searchable model, which the empty search shows
        private void StoreFullSearchList()
        {
            SavedHandlingKey[] allKeys = GetAllSearchResultKeys();
            StoreSearchResults("", allKeys, allKeys.Length);
        }

        // The main menu's Search row. Lists every model with no prompt, and works with no vehicle
        private void OpenVehicleSearch()
        {
            StoreFullSearchList();
            ShowSearchList();
        }

        // The New Search row. The prompt opens empty, and an empty or canceled prompt returns to the full list with no toast
        // A too short text or no match leaves the current list as it is
        private void StartVehicleSearch()
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage20, "", INPUT_MAX_LENGTH);
            string typed = string.IsNullOrEmpty(input) ? "" : input.Trim();
            if (typed.Length == 0)
            {
                if (searchQueryText.Length > 0) ClearVehicleSearch();
                return;
            }
            string shownText = typed.Replace("~", "");
            if (typed.Length < SEARCH_MIN_LENGTH)
            {
                notifications.Notify(NOTIFICATION_KEY_VEHICLE_SEARCH, string.Format(NOTIF_SEARCH_TOO_SHORT, SEARCH_MIN_LENGTH));
                return;
            }
            int totalMatches;
            List<SavedHandlingKey> matches = FindVehicleModelMatches(typed, out totalMatches);
            if (matches.Count == 0)
            {
                notifications.Notify(NOTIFICATION_KEY_VEHICLE_SEARCH, string.Format(NOTIF_SEARCH_NO_MATCH, shownText));
                return;
            }
            StoreSearchResults(shownText, matches.ToArray(), totalMatches);
            ShowSearchList();
            if (totalMatches > matches.Count) notifications.Notify(NOTIFICATION_KEY_VEHICLE_SEARCH, string.Format(NOTIF_SEARCH_TRUNCATED, matches.Count, totalMatches));
        }

        // True while a typed query narrows the list, the only time Clear Search has anything to clear
        private bool IsSearchNarrowed() { return browsedSource == searchSource && searchQueryText.Length > 0; }

        // Clear Search, from any row: The full list returns, the same as an empty New Search
        private void ClearVehicleSearch()
        {
            StoreFullSearchList();
            ShowSearchList();
        }

        // Saved Handlings' own Shown refresh builds the rows when it opens from the main menu. From its own New Search row it is already open and refreshes directly
        private void ShowSearchList()
        {
            if (mainMenu.Visible)
            {
                HideMenu(mainMenu);
                ShowMenu(savedHandlingsMenu);
            }
            else RefreshSavedHandlingsMenu();
            savedHandlingsMenu.SelectedIndex = SavedHandlingRowOffset;
        }

        // The rows build when Saved Handlings next refreshes
        // A search sets the source, and opening Saved Handlings from the main menu or a hotkey resets it. A toggle key close and reopen leaves it set
        private void StoreSearchResults(string shownText, SavedHandlingKey[] keys, int totalMatches)
        {
            searchPreviewCache.Clear();
            searchResultKeys = keys;
            searchQueryText = shownText;
            browsedSource = searchSource;
            navigation.PreviewWanted = true;
            logger.Log(Dbg.Level.Info, shownText.Length == 0 ? Dbg.VehicleSearchAll(keys.Length) : Dbg.VehicleSearchDone(shownText, totalMatches, keys.Length));
        }

        // Runs with no prompt and no toast, and an empty text restores the full list
        // A typed text with no match leaves Saved Handlings on Exports
        private void RestoreVehicleSearch(string typed)
        {
            if (typed.Length == 0)
            {
                StoreFullSearchList();
                return;
            }
            int totalMatches;
            List<SavedHandlingKey> matches = FindVehicleModelMatches(typed, out totalMatches);
            if (matches.Count > 0) StoreSearchResults(typed, matches.ToArray(), totalMatches);
        }

        // Neither saved folder is scanned
        // Each row applies its model's handling on Enter, from the key its Tag holds
        private void BuildSearchResultRows(int previousIndex)
        {
            cachedSavedHandlingKeys = searchResultKeys;
            autosavesRowShown = false;
            SetSavedHandlingsBanner(searchSource.Banner);
            savedHandlingsMenu.Name = searchQueryText.Length == 0 ? SUBTITLE_SEARCH_ALL : string.Format(searchSource.Subtitle, searchQueryText);
            if (newSearchRowItem == null)
            {
                newSearchRowItem = new NativeItem(TITLE_NEW_SEARCH, DESC_NEW_SEARCH);
                SubscribeActivated(newSearchRowItem, "newSearchRowItem.Activated", () => StartVehicleSearch());
            }
            savedHandlingsMenu.Items.Add(newSearchRowItem);
            SyncSearchRowTints();
            for (int i = 0; i < searchResultKeys.Length; i++)
            {
                NativeItem resultRow;
                if (i < searchResultRowPool.Count) resultRow = searchResultRowPool[i];
                else
                {
                    resultRow = new NativeItem("", "");
                    SubscribeActivated(resultRow, "resultRow.Activated", () =>
                    {
                        if (resultRow.Tag is SavedHandlingKey) ApplyHandling((SavedHandlingKey)resultRow.Tag);
                    });
                    if (searchRowsDimmed) DimItemAsDisabled(resultRow);
                    searchResultRowPool.Add(resultRow);
                }
                // The title is assigned only when it changes, since the library measures the text on assignment
                if (resultRow.Title != searchResultKeys[i].Name) resultRow.Title = searchResultKeys[i].Name;
                resultRow.Tag = searchResultKeys[i];
                savedHandlingsMenu.Items.Add(resultRow);
            }
            SelectSavedHandlingsRowWithoutPreviewRefresh(Math.Max(0, Math.Min(previousIndex, savedHandlingsMenu.Items.Count - 1)));
            RecalculateTargetWidth(savedHandlingsMenu);
            RefreshPreviewMenu(searchResultKeys);
        }

        // Result rows dim with no Active vehicle, like export rows, since applying one needs a vehicle
        // The whole pool is retinted, so a row a later list brings into view is already in the right state
        private void SyncSearchRowTints()
        {
            bool shouldDim = !savedHandlingRowsApplicable;
            if (searchRowsDimmed == shouldDim) return;
            searchRowsDimmed = shouldDim;
            for (int i = 0; i < searchResultRowPool.Count; i++)
            {
                if (shouldDim) DimItemAsDisabled(searchResultRowPool[i]);
                else searchResultRowPool[i].Colors = new ColorSet();
            }
        }

        // The widest result row, from title widths cached by text
        // Only a name never measured before costs a native text measurement
        private float MeasureSearchRowsWidth()
        {
            float longest = newSearchRowItem != null ? MeasureRowWidth(newSearchRowItem, null) : 0.0f;
            for (int i = 0; i < searchResultKeys.Length; i++)
            {
                string name = searchResultKeys[i].Name;
                float titleWidth;
                if (!searchTitleWidths.TryGetValue(name, out titleWidth))
                {
                    widthMeasure.Text = name;
                    titleWidth = widthMeasure.Width;
                    searchTitleWidths[name] = titleWidth;
                }
                if (titleWidth + WIDTH_ITEM_PADDING > longest) longest = titleWidth + WIDTH_ITEM_PADDING;
            }
            return longest;
        }

        // The banner text is set only when it changes, since the text is measured on assignment
        private void SetSavedHandlingsBanner(string text)
        {
            if (savedHandlingsMenu.BannerText == null || savedHandlingsMenu.BannerText.Text == text) return;
            savedHandlingsMenu.BannerText.Text = text;
            RecenterBannerText(savedHandlingsMenu);
        }

        // The document of a model's live handling, with every field gate open since a model's flags cannot be read without an entity
        // Preview and applying a result both build it here, so what is previewed is what applies
        // Null when the model's handling cannot be read
        private XDocument BuildSearchResultDocument(SavedHandlingKey key)
        {
            HandlingData handling = modelNames.ResolveModelHandling(key.ModelHash);
            if (handling == null) return null;
            return handlingMetaXml.BuildHandlingXml(handling, key.Name, new HandlingFieldGates { RocketBoost = true, RearMountedTurret = true }, ExportOverlay.None);
        }

        // Built through the same document a saved handling holds
        // A model whose handling cannot be read caches empty data, like a saved handling file that fails to parse
        private HandlingPreviewCache GetSearchPreviewCache(SavedHandlingKey key)
        {
            HandlingPreviewCache cached;
            if (searchPreviewCache.TryGetValue(key, out cached)) return cached;
            cached = new HandlingPreviewCache();
            try
            {
                XDocument doc = BuildSearchResultDocument(key);
                if (doc != null)
                {
                    cached.Fields = BuildFullFieldListFromDocument(doc);
                    cached.HandlingName = ReadHandlingNameFromDocument(doc);
                    cached.WeaponSlots = BuildWeaponPreviewSlotsFromDocument(doc);
                    cached.HasRocketBoostFields = true;
                }
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.PreviewBuildFailed(key.Name, ex.Message)); }
            searchPreviewCache[key] = cached;
            return cached;
        }
    }
}
