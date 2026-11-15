using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using LemonUI.Elements;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private ScaledText widthMeasure = new ScaledText(PointF.Empty, "", TITLE_MEASURE_SCALE);
        private ScaledText valueMeasure = new ScaledText(PointF.Empty, "", VALUE_MEASURE_SCALE);
        private Dictionary<NativeMenu, float> targetMenuWidths = new Dictionary<NativeMenu, float>();
        private Dictionary<NativeMenu, float> currentMenuWidths = new Dictionary<NativeMenu, float>();
        private HashSet<NativeItem> stringCyclerItems = new HashSet<NativeItem>();
        // Per-item measurement cache that skips the native text-measurement call when an item's text hasn't changed since the last recalculation
        // Bounded by the fixed set of NativeItems this script ever constructs, since every item is built once and reused, never recreated
        private struct MeasuredText { public string Text; public float Width; }
        private Dictionary<NativeItem, MeasuredText> measuredTitles = new Dictionary<NativeItem, MeasuredText>();
        private Dictionary<NativeItem, MeasuredText> measuredValues = new Dictionary<NativeItem, MeasuredText>();
        // Menus needing a width recalculation, drained once per tick against that tick's freshly-resolved HandlingData
        // A menu marked dirty while it isn't the visible menu stays queued until it becomes visible, so no menu is ever shown with a stale width
        private HashSet<NativeMenu> dirtyWidthMenus = new HashSet<NativeMenu>();
        // Seeds the next menu's animation start point so the lerp transitions between settled widths
        private float lastVisibleEditorWidth = MENU_WIDTH;
        // Same seeding role as lastVisibleEditorWidth, scoped to Preview's own menu tree (previewMenu and all its submenus)
        // The two trees are browsed independently, so sharing one seed could start a Preview transition from a stale editor width
        private float lastVisiblePreviewWidth = MENU_WIDTH;
        // Same seeding role, scoped to Diff's own tree, kept separate from Preview's so a Diff category switch never seeds from a stale Preview width
        private float lastVisibleDiffWidth = MENU_WIDTH;
        // The handling argument, when provided, lets a float field whose live value needs FormatDisplayFloat's F6 branch claim the extended tier
        private void RecalculateTargetWidth(NativeMenu menu, HandlingData handling = null)
        {
            if (menu == null) return;
            // The search list holds about a thousand plain rows, so its widths come from a cache keyed by text instead of one measurement per row
            if (menu == savedHandlingsMenu && browsedSource == searchSource)
            {
                SetTargetWidthFromRows(menu, MeasureSearchRowsWidth());
                return;
            }
            float longest = 0.0f;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                float itemWidth = MeasureRowWidth(menu.Items[i], handling);
                if (itemWidth > longest) longest = itemWidth;
            }
            SetTargetWidthFromRows(menu, longest);
        }

        // The target is the widest of the rows, the banner, and the name bar
        private void SetTargetWidthFromRows(NativeMenu menu, float longest)
        {
            // BannerText renders at its own font and scale, so measuring it directly is the only way to get its real width
            float bannerWidth = menu.BannerText.Width + WIDTH_ITEM_PADDING;
            if (bannerWidth > longest) longest = bannerWidth;
            float nameBarWidth = MeasureNameBarWidth(menu);
            if (nameBarWidth > longest) longest = nameBarWidth;
            float newTarget = Math.Max(MENU_WIDTH, longest);
            targetMenuWidths[menu] = newTarget;
            if (!currentMenuWidths.ContainsKey(menu)) currentMenuWidths[menu] = menu.Width;
        }

        // One row's width, its title plus its value padding. Its text measurements fill the caches, so a late tree's warm-up measures one row per step
        private float MeasureRowWidth(NativeItem item, HandlingData handling)
        {
            float titleWidth = MeasureCached(item, item.Title, measuredTitles, widthMeasure);
            float padding;
            if (item is NativeSlidableItem)
            {
                padding = WIDTH_VALUE_PADDING;
                if (handling != null && NeedsExtendedWidth(item, handling)) padding += WIDTH_EXTENDED_PADDING;
                string cyclerValueText = ResolveCyclerValueText(item);
                if (cyclerValueText != null)
                {
                    float valueWidth = MeasureCached(item, cyclerValueText, measuredValues, valueMeasure) + WIDTH_ITEM_PADDING + WIDTH_PREVIEW_VALUE_GAP;
                    if (valueWidth > padding) padding = valueWidth;
                }
            }
            else padding = WIDTH_ITEM_PADDING;
            return titleWidth + padding;
        }

        // Matches NativeDynamicItem<T>.SelectedItem.ToString(), the same text the item's own display draws
        // Null for a slidable item type this script never wires as a cycler
        private string ResolveCyclerValueText(NativeItem item)
        {
            if (stringCyclerItems.Contains(item)) return ((NativeDynamicItem<string>)item).SelectedItem;
            NativeDynamicItem<DisplayFloat> floatCycler = item as NativeDynamicItem<DisplayFloat>;
            if (floatCycler != null) return floatCycler.SelectedItem.ToString();
            NativeDynamicItem<int> intCycler = item as NativeDynamicItem<int>;
            if (intCycler != null) return intCycler.SelectedItem.ToString();
            return null;
        }

        // The name bar holds the subtitle on the left and, once the list scrolls, the item counter on the right, and both are measured so they never overlap
        // The counter is measured at its widest, the last row of the list, and the subtitle in capitals, as the menu draws it
        private float MeasureNameBarWidth(NativeMenu menu)
        {
            widthMeasure.Text = menu.KeepNameCasing ? menu.Name : menu.Name.ToUpperInvariant();
            float width = widthMeasure.Width + WIDTH_ITEM_PADDING;
            if (menu.Items.Count > menu.MaxItems)
            {
                widthMeasure.Text = menu.Items.Count + " / " + menu.Items.Count;
                width += widthMeasure.Width;
            }
            return width;
        }

        // The underlying text-width read has no caching of its own, and most items' text is unchanged between recalculations
        // Reusing a measurement for unchanged text is what keeps a bulk width recalculation cheap
        private float MeasureCached(NativeItem item, string text, Dictionary<NativeItem, MeasuredText> cache, ScaledText measure)
        {
            MeasuredText cached;
            if (cache.TryGetValue(item, out cached) && cached.Text == text) return cached.Width;
            measure.Text = text;
            float width = measure.Width;
            cache[item] = new MeasuredText { Text = text, Width = width };
            return width;
        }

        // Reuses FormatDisplayFloat's own F2/F6 decision (IsExtendedDisplayFloat) so the two never drift apart
        private bool NeedsExtendedWidth(NativeItem item, HandlingData handling)
        {
            FloatFieldBinding binding;
            if (!floatBindingByItem.TryGetValue(item, out binding)) return false;
            return IsExtendedDisplayFloat(binding.Getter(handling));
        }

        // A value edit can move the width without a structural item-set change only when the displayed text's length or extended tier changes
        private void RecalculateVisibleMenuWidth(HandlingData handling)
        {
            for (int i = 0; i < allMenus.Length; i++)
            {
                if (allMenus[i].Visible)
                {
                    RecalculateTargetWidth(allMenus[i], handling);
                    return;
                }
            }
        }

        // Preview's rows are plain NativeItems with Title and AltTitle as two independent columns, not a NativeSlidableItem that RecalculateTargetWidth pads for
        // A saved field's value (e.g. a Vector3 triple) can be far longer than the flat value padding covers, so both columns are measured directly here
        // It takes an explicit menu so every category drilldown (previewCategoryData, previewWeaponSlotMenus) gets its own target width
        private void RecalculatePreviewTargetWidth(NativeMenu menu)
        {
            float longest = 0.0f;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                NativeItem item = menu.Items[i];
                float titleWidth = MeasureCached(item, item.Title, measuredTitles, widthMeasure);
                float itemWidth;
                if (item is NativeCheckboxItem) itemWidth = titleWidth + WIDTH_ITEM_PADDING;
                else
                {
                    float valueWidth = MeasureCached(item, item.AltTitle, measuredValues, widthMeasure);
                    itemWidth = titleWidth + valueWidth + WIDTH_PREVIEW_VALUE_GAP;
                }
                if (itemWidth > longest) longest = itemWidth;
            }
            SetTargetWidthFromRows(menu, longest);
        }

        // Called after every tree's TickHandling is current for this tick
        // A dirty menu that is not visible stays queued, so whichever tick first finds it visible recalculates against live data before the player can see it
        private void DrainDirtyWidthMenus()
        {
            if (dirtyWidthMenus.Count == 0) return;
            List<NativeMenu> resolvedThisDrain = null;
            foreach (NativeMenu menu in dirtyWidthMenus)
            {
                if (!menu.Visible) continue;
                HandlingData handling = ResolveDirtyWidthMenuHandling(menu);
                RecalculateTargetWidth(menu, handling);
                if (resolvedThisDrain == null) resolvedThisDrain = new List<NativeMenu>();
                resolvedThisDrain.Add(menu);
            }
            if (resolvedThisDrain != null) for (int i = 0; i < resolvedThisDrain.Count; i++) dirtyWidthMenus.Remove(resolvedThisDrain[i]);
        }

        // Returns the HandlingData this tick already resolved for whichever tree owns the menu
        private HandlingData ResolveDirtyWidthMenuHandling(NativeMenu menu)
        {
            for (int i = 0; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                // A tree not built yet owns no menu, and its AdvancedData slots hold no rows to compare
                if (context.BuildState != EditorBuildState.Built) continue;
                if (menu == context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live].ListMenu || menu == GetLiveMenuForCategory(context, CATEGORY_WEAPON) || menu == context.RootMenu || IsAdvancedDataEntryMenuOf(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live].Entries, menu)) return context.TickHandling;
            }
            return null;
        }

        private static bool IsAdvancedDataEntryMenuOf(AdvancedDataItemSet[] slots, NativeMenu menu)
        {
            for (int i = 0; i < slots.Length; i++) if (slots[i].Menu == menu) return true;
            return false;
        }

        private void AnimateMenuWidth()
        {
            for (int i = 0; i < allMenus.Length; i++)
            {
                if (allMenus[i].Visible) AnimateMenuWidthStep(allMenus[i]);
            }
            // A category submenu stays on screen unfocused while previewMenu is hidden, so neither previewMenu's visibility nor Preview's focus can gate this
            if (previewMenu.Visible) AnimateMenuWidthStep(previewMenu);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) { if (data.Value.Menu.Visible) AnimateMenuWidthStep(data.Value.Menu); }
            // diffBaselineActive covers the whole Diff tree, regardless of which specific submenu is on screen
            if (diffBaselineActive)
            {
                if (diffMenu.Visible) AnimateMenuWidthStep(diffMenu);
                foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) { if (data.Value.Menu.Visible) AnimateMenuWidthStep(data.Value.Menu); }
            }
        }

        private void AnimateMenuWidthStep(NativeMenu menu)
        {
            float target;
            if (!targetMenuWidths.TryGetValue(menu, out target)) return;
            float current;
            if (!currentMenuWidths.TryGetValue(menu, out current)) current = menu.Width;
            if (Math.Abs(target - current) <= WIDTH_SNAP_EPSILON)
            {
                if (current != target)
                {
                    current = target;
                    menu.Width = current;
                }
            }
            else
            {
                current += (target - current) * WIDTH_LERP_FACTOR;
                menu.Width = current;
            }
            currentMenuWidths[menu] = current;
            RecenterBannerText(menu);
        }

        // The API's hardcoded offset only approximates center at the baseline width, and animated widths drift from it
        private void RecenterBannerText(NativeMenu menu)
        {
            if (menu.BannerText == null || menu.Banner == null) return;
            PointF bannerPosition = menu.Banner.Position;
            PointF textPosition = menu.BannerText.Position;
            menu.BannerText.Position = new PointF(bannerPosition.X + menu.Width / 2.0f, textPosition.Y);
        }
    }
}
