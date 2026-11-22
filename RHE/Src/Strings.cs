using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // [Descriptions: Main Menu Rows] Descriptions of the main menu's rows
        private const string DESC_SELECT_VEHICLE  = "Enter selection mode: Aim at a vehicle and left click or press Enter to target it for editing in another editor";
        private const string DESC_UNLOAD_VEHICLE  = "Unload the scoped vehicle from memory so edits that don't apply live can take effect without having to restart the game. Every existing instance in the world will be deleted and traffic will be disabled until the process is complete. It'll be auto respawned if it unloads within one second, otherwise you'll get a notification when it's ready";
        private const string DESC_RESPAWN_VEHICLE = "Respawn the scoped vehicle. Current physics state and customizations will be restored";
        private const string DESC_SAVED_HANDLINGS = "Browse, preview, compare, and manage exported handlings";
        private const string DESC_SAVE            = "Export the current handling to a file";

        // [Descriptions: Weapon Slots And AdvancedData] Descriptions of the rows that add and reorder weapon slots and AdvancedData entries
        private const string DESC_ADD_WEAPON_SLOT_LIVE        = "Adds a new slot with a default WeaponHash of {0}";
        private const string DESC_ADD_WEAPON_SLOT_PHANTOM     = "Add a new weapon slot with a default WeaponHash. ~y~Does not apply live";
        private const string DESC_ADD_ADVANCEDDATA_ENTRY      = "Add another AdvancedData entry. ~y~Does not apply live";
        private const string DESC_ADD_ADVANCEDDATA_ENTRY_LIVE = "Add another AdvancedData entry in the running game";
        private const string DESC_REORDER_SLOT_LIVE           = "Hold your configured modifier key and press PageUp or Down to shift this slot up or down in the array";
        private const string DESC_REORDER_SLOT_PHANTOM        = "Hold your configured modifier and press PageUp or Down to reorder the highlighted slot";

        // [Descriptions: Categories] The Delete text of a live category row the game reads as required, or one this vehicle type keeps phantom only
        private const string DESC_CATEGORY_REQUIRED_LIVE     = "This vehicle type needs this SubData, so deleting it will only exclude it from saves";
        private const string DESC_CATEGORY_PHANTOM_ONLY_LIVE = "This SubData can't be deleted or created live on this vehicle type, as it would crash the game, so deleting it will only exclude it from saves";

        // [Descriptions: Saved Handlings And Search] Descriptions of the Saved Handlings list rows and the Search rows
        private const string DESC_AUTOSAVES_ROW      = "Browse autosaved handlings";
        private const string DESC_NO_AUTOSAVES       = "Autosave writes here after every change while it is on";
        private const string DESC_NO_SAVED_HANDLINGS = "Save one from the main menu";
        private const string DESC_SEARCH             = "Browse from a list of all available handlings to preview, apply, or copy specific values from them";
        private const string DESC_NEW_SEARCH         = "Narrow the list by model name. Submitting a blank prompt clears the previous query";

        // [Descriptions: Preview Rows] Description of a Preview row that copies its value
        private const string DESC_COPY_VALUE = "Press Enter to copy this value to the current handling";

        // [Descriptions: Settings] Row descriptions that are not their INI comment
        // They cover Logging Verbosity, Prompt On Save, the About row, and the visualization checkboxes
        private const string DESC_LOGGING_VERBOSITY       = "How much detail is recorded in the log file. Warn/Error = Default";
        private const string DESC_PROMPT_ON_SAVE          = "Prompt for a handling and file name before each save, or save immediately without prompting using the vehicle's own names, even with Always Overwrite on";
        private const string DESC_ABOUT                   = "Built with love by KieranBLK :)" + "\nInspired by ikt's RealTimeHandlingEditor and made possible by Claude Code, ScriptHookVDotNetEnhanced, and LemonUI";
        private const string DESC_VIS_BOUNDING_BOX        = "Draws the corners of the model's collision bound edges in green";
        private const string DESC_VIS_CENTER_OF_MASS      = "Draws a red gizmo at the handling's CenterOfMassOffset, hidden if it matches the CenterOfGravityOffset";
        private const string DESC_VIS_CENTER_OF_GRAVITY   = "Draws a white gizmo at the vehicle's CenterOfGravityOffset, its actual center of mass, which the game copies CenterOfMassOffset into";
        private const string DESC_VIS_ROLL_CENTER_HEIGHTS = "Draws red bars across the front and rear axles out past the wheels, which shift from the tire's ground contact height by RollCenterHeight";
        private const string DESC_VIS_SUBMERGED_HEIGHT    = "Draws a blue rectangle around the vehicle at the height of PercentSubmerged. Turns red when the vehicle won't float in water";
        private const string DESC_VIS_ANTI_ROLL_BAR       = "Draws a yellow arm on each axle whose length shows its share of AntiRollBarForce";
        private const string DESC_VIS_WHEEL_BOUNDS        = "Draws each wheel's collision bound in green and extra bounds in blue, if it has any. Respective of actual game behavior: Typically slightly offset inwards, downwards to match SuspensionLowerLimit, and not steered unless HF_STEER_ALL_WHEELS and HF_EXT_WHEEL_BOUNDS_COL are enabled";
        private const string DESC_VIS_SUSPENSION_LOAD     = "Draws a white gauge at each wheel. The cyan level line = that wheel's share of mass, middle = even. Red line = SpringForce, higher when it's stronger than the load placed on it. In a vehicle with more than 2 axles, the level line turns red and indicates SpringForce instead of mass";
        private const string DESC_VIS_RUDDER_OFFSETS      = "Boats only. Draws a white line across the thrust point. Cyan line = RudderOffsetSubmerge, the point tested for the propeller being under water. Red line = RudderOffsetForce, where thrust and rudder force is applied";
        private const string DESC_VIS_PROP_RADIUS         = "Boats only. Draws an orange cross at RudderOffsetSubmerge, representing PropRadius";
        private const string DESC_VIS_IMPELLER            = "Boats only. Draws a purple ring at ImpellerOffset, with a downward arrow scaled by ImpellerForceMultiplier. Hidden when offset = 0";
        private const string DESC_VIS_WATER_SAMPLES       = "Boats/Seaplanes only. Draws boxes: Light blue = samples the hull or pontoons float on. Dark blue = area of the hull probed for the samples, scaled by BoxMultipliers, from SampleTestCorrection up to SampleTop; PontoonSampleSizes/Length for seaplanes. Edits only shown after an unload";
        private const string DESC_VIS_KEEL_SPHERES        = "Boats only. Draws 3 yellow spheres front to back, scaled by KeelSphereSize";

        // [Descriptions: Row Prefixes And Suffix] Text added to the front or the end of a row's own description
        private const string DESC_UNAVAILABLE_ON_BUILD_PREFIX   = "Unavailable on this game build. ";
        private const string DESC_WORKS_UPSIDE_DOWN_LOCK_PREFIX = "~y~Locked on: This vehicle has no misc_e bone and would crash the game in hover mode.~s~ ";
        private const string DESC_WEAPON_HASH_TYPING_HINT       = " Press Enter to type a name in manually: Accepts game name, menu display name, or hex";

        // [Titles: Main And Editor Menus] Row titles and banners of the main menu and the editor trees
        private const string TITLE_EDIT_CURRENT_NAMED    = "Edit {0} (Current)";
        private const string TITLE_EDIT_SELECTED         = "Edit {0} (Selected)";
        private const string TITLE_SELECT_VEHICLE        = "Select Vehicle";
        private const string TITLE_UNLOAD_VEHICLE        = "Unload Vehicle";
        private const string TITLE_RESPAWN_VEHICLE       = "Respawn Vehicle";
        private const string TITLE_EDIT_HANDLING         = "Edit Handling";
        private const string TITLE_EDIT_CURRENT_HANDLING = "Edit Current Handling";
        private const string TITLE_DIFF_COUNT            = "Diff ({0})";
        private const string SUFFIX_PREVIEW              = " (Preview)";
        private const string SUFFIX_DIFF                 = " (Diff)";

        // [Titles: Weapon Slots And Entries] Titles of weapon slot and AdvancedData entry rows and menus
        private const string TITLE_WEAPON_SLOT_NAMED      = "Weapon Slot {0}: {1}";
        private const string TITLE_WEAPON_SLOT_CUSTOM     = "Weapon Slot {0}: Custom";
        private const string TITLE_WEAPON_SLOT_PREVIEW    = "Weapon Slot {0} (Preview)";
        private const string TITLE_WEAPON_SLOT_DIFF       = "Weapon Slot {0} (Diff)";
        private const string TITLE_ADVANCEDDATA_ENTRY_KEY = "AdvancedData Entry {0}";
        private const string TITLE_WEAPON_SLOT            = "Weapon Slot {0}";
        private const string TITLE_ENTRY                  = "Entry {0}";
        private const string TITLE_ADD_SLOT               = "Add Slot";

        // [Titles: Saved Handlings] Titles of the rows Saved Handlings shows in place of an empty list
        private const string TITLE_NO_AUTOSAVES       = "No autosaves";
        private const string TITLE_NO_SAVED_HANDLINGS = "No saved handlings";

        // [Titles: Settings] Row titles, section headers, and list values of the Settings menu
        private const string TITLE_ACTION_MODIFIER          = "Action Modifier";
        private const string TITLE_TOGGLE_MENU_KEY          = "Toggle Menu Key";
        private const string TITLE_CHEAT_STRING             = "Cheat String";
        private const string TITLE_AUTO_RESPAWN             = "Auto Respawn";
        private const string TITLE_CHECK_FOR_UPDATES        = "Check For Updates";
        private const string TITLE_RESET_ALL_SETTINGS       = "Reset All Settings";
        private const string TITLE_ALWAYS_OVERWRITE         = "Always Overwrite";
        private const string TITLE_PROMPT_ON_SAVE           = "Prompt On Save";
        private const string TITLE_AUTOSAVE                 = "Autosave";
        private const string TITLE_PREVIEW_BY_DEFAULT       = "Preview By Default";
        private const string TITLE_SHOW_PREVIEW_DIFF        = "Show Preview Diff";
        private const string TITLE_BANNER_COLOR             = "Banner Color";
        private const string TITLE_MENU_POSITION            = "Position";
        private const string TITLE_MAX_ROWS                 = "Max Rows";
        private const string TITLE_REASSERT_DRIFTED_FIELDS  = "Reassert Drifted Fields";
        private const string TITLE_LOGGING_VERBOSITY        = "Logging Verbosity";
        private const string TITLE_CLEAR_LOG_ON_RELOAD      = "Clear Log On Reload";
        private const string TITLE_ABOUT                    = "About";
        private const string TITLE_SECTION_SCRIPT           = "Script";
        private const string TITLE_SECTION_EXPORTING        = "Exporting";
        private const string TITLE_SECTION_HANDLING_PREVIEW = "Handling Preview";
        private const string TITLE_SECTION_VISUALIZATION    = "Visualization";
        private const string TITLE_SECTION_APPEARANCE       = "Menu Appearance";
        private const string TITLE_SECTION_DEBUG            = "Debug";
        private const string TITLE_VIS_ISOLATED_DRAWING     = "Isolated Drawing";
        private const string TITLE_VIS_BOUNDING_BOX         = "Bounding Box";
        private const string TITLE_VIS_CENTER_OF_MASS       = "Center-Of-Mass";
        private const string TITLE_VIS_CENTER_OF_GRAVITY    = "Center-Of-Gravity";
        private const string TITLE_VIS_ROLL_CENTER_HEIGHTS  = "RollCenter Heights";
        private const string TITLE_VIS_SUBMERGED_HEIGHT     = "Submerged Height";
        private const string TITLE_VIS_ANTI_ROLL_BAR        = "Anti-RollBar";
        private const string TITLE_VIS_WHEEL_BOUNDS         = "Wheel Bounds";
        private const string TITLE_VIS_SUSPENSION_LOAD      = "Suspension Load";
        private const string TITLE_VIS_RUDDER_OFFSETS       = "Rudder Offsets";
        private const string TITLE_VIS_PROP_RADIUS          = "Prop Radius";
        private const string TITLE_VIS_IMPELLER             = "Impeller";
        private const string TITLE_VIS_WATER_SAMPLES        = "Water Samples";
        private const string TITLE_VIS_KEEL_SPHERES         = "Keel Spheres";

        // [Titles: Search] Banner and row title of the vehicle model search
        private const string TITLE_SEARCH     = "Search";
        private const string TITLE_NEW_SEARCH = "New Search";

        // [Subtitles: Editor Menus] The line under an editor menu's banner
        private const string SUBTITLE_EXPORT_ONLY             = "{0} (Export Only)";
        private const string SUBTITLE_HANDLING_DATA           = "Handling Data";
        private const string SUBTITLE_WEAPON_SLOT_EXPORT_ONLY = "Weapon Slot {0} (Export Only)";

        // [Subtitles: Flag Menus] Flag submenu subtitles that carry the flag value and how many rows are ticked
        private const string SUBTITLE_ADVANCED_FLAGS_VALUE       = "AdvancedFlags (0x{0}) ({1})";
        private const string SUBTITLE_HANDLING_FLAGS_VALUE       = "HandlingFlags (0x{0}) ({1})";
        private const string SUBTITLE_DAMAGE_FLAGS_VALUE         = "DamageFlags (0x{0}) ({1})";
        private const string SUBTITLE_MODEL_FLAGS_VALUE          = "ModelFlags (0x{0}) ({1})";
        private const string SUBTITLE_SPECIAL_FLIGHT_FLAGS_VALUE = "Flags (0x{0}) ({1})";
        private const string SUBTITLE_ADVANCED_FLAGS_TEXT        = "AdvancedFlags ({0}) ({1})";
        private const string SUBTITLE_SPECIAL_FLIGHT_FLAGS_TEXT  = "Flags ({0}) ({1})";
        private const string SUBTITLE_FLAGS_TEXT                 = "{0} ({1}) ({2})";

        // [Subtitles: Search] The search list's subtitle for the full list and for a typed search
        private const string SUBTITLE_SEARCH_ALL     = "All Models";
        private const string SUBTITLE_SEARCH_RESULTS = "Results: {0}";

        // [Placeholders] Rows that stand in for something absent
        private const string PLACEHOLDER_SUBDATA_NOT_PRESENT      = "This SubHandlingData is not present on this vehicle. Press Enter to create it statically for saving to an export. ~y~Does not apply live";
        private const string PLACEHOLDER_SUBDATA_CREATE_LIVE      = "This SubHandlingData is not present on this vehicle. Press Enter to create it in the running game. ~y~Falls back to static creation when it can't be created live";
        private const string PLACEHOLDER_SUBDATA_NOT_LIVE_ON_TYPE = "~y~This SubHandlingData can't be created live on this vehicle type.~s~ Press Enter to create it statically for saving to an export";
        private const string PLACEHOLDER_CATEGORY_NOT_PRESENT     = "This category is not present on this vehicle. Press Enter to create it statically for saving to an export. ~y~Does not apply live";
        private const string PLACEHOLDER_CATEGORY_EXCLUDED        = "This category is excluded from the next save. Press Enter to include it again";
        private const string PLACEHOLDER_ADVANCEDDATA_CREATE_LIVE = "This vehicle has no AdvancedData entries. Press Enter to create one in the running game. ~y~Falls back to static creation when it can't be created live";
        private const string PLACEHOLDER_CATEGORY_NOT_CREATED     = "This category has not been created yet. Press Enter to create it. ~y~Does not apply live";
        private const string PLACEHOLDER_DIFF_NOT_PRESENT         = "Not in the original handling";

        // [Notifications: Feed] The game's text label made of 10 text components, which every toast posts through so it can run past one component's length
        private const string NOTIF_FEED_LABEL = "CELL_EMAIL_BCON";

        // [Notifications: System] Toasts for settings, INI, and error events
        private const string NOTIF_ERROR_MESSAGE    = "~r~RHE: {0}";
        private const string NOTIF_SETTINGS_RESET   = "~g~RHE: All settings reset to defaults";
        private const string NOTIF_INI_RELOADED     = "~g~RHE: INI reloaded";
        private const string NOTIF_INI_REJECTED     = "~y~RHE: Invalid INI value(s) rejected, check log";
        // A template: UpdateChecker.Finish fills {0} with the latest release's version
        private const string NOTIF_UPDATE_AVAILABLE = "RHE: Update v{0} is available. Download it from GitHub or 5Mods";

        // [Notifications: Input And Editing] Toasts for typed entry, key rebinding, value bounds, and the arm-then-confirm cue
        private const string NOTIF_VALUE_BOUNDED              = "~y~RHE: Value bounded to {0}";
        private const string NOTIF_KEY_MUST_BE                = "~r~RHE: Must be {0}";
        private const string NOTIF_KEY_INVALID_MUST_BE        = "~r~RHE: Invalid. Must be {0}";
        private const string NOTIF_ENTRY_CANCELED             = "~y~RHE: Entry canceled";
        private const string NOTIF_NAME_UNCHANGED             = "~y~RHE: Name unchanged";
        private const string NOTIF_KEY_TOGGLE_USE_CLEAR_INPUT = "~r~RHE: Invalid. Use Clear Input to unbind";
        private const string NOTIF_KEY_MODIFIER_REQUIRED      = "~r~RHE: Cannot be undefined";
        private const string NOTIF_ENTRY_POINT_REQUIRED       = "~y~RHE: The menu requires a Toggle Key or Cheat String to be configured";
        private const string NOTIF_KEY_CAPTURE_ARMED          = "RHE: Press the desired key now or Enter to type one in manually instead";
        private const string NOTIF_BANNER_COLOR_INVALID       = "~r~RHE: Invalid color code, enter an RGB or HEX value";
        private const string NOTIF_NO_VEHICLE_EDIT            = "~y~RHE: Get in a vehicle to edit its handling";
        private const string NOTIF_ARMED_CONFIRM_HINT         = "RHE: Press again within {0} seconds to confirm";

        // [Notifications: Exports And Saved Handlings] Toasts for saving, applying, renaming, duplicating, and deleting saved handlings
        private const string NOTIF_HANDLING_NAME_MISMATCH     = "~y~RHE: \"{0}\" may not match this vehicle's handlingId in vehicles.meta. Could crash the game if not corrected after pasting into handling.meta";
        private const string NOTIF_HANDLING_APPLIED_SKIPPED   = "~y~RHE: Applied {0}, {1} fields skipped";
        private const string NOTIF_HANDLING_IDENTICAL         = "~y~RHE: Identical handling, nothing applied";
        private const string NOTIF_HANDLING_RENAMED_OVERWROTE = "~g~RHE: Renamed to {0} and overwrote existing";
        private const string NOTIF_DELETED                    = "~g~RHE: Deleted {0}";
        private const string NOTIF_HANDLING_APPLIED           = "~g~RHE: Applied {0}";
        private const string NOTIF_COPIED_TO_EXPORTS          = "~g~RHE: Copied to Exports as {0}";
        private const string NOTIF_HANDLING_DUPLICATED        = "~g~RHE: Duplicated {0}";
        private const string NOTIF_HANDLING_RENAMED           = "~g~RHE: Renamed to {0}";
        private const string NOTIF_HANDLING_OVERWROTE         = "~g~RHE: Overwrote {0}";
        private const string NOTIF_HANDLING_SAVED             = "~g~RHE: Saved {0}";
        private const string NOTIF_NO_VEHICLE_SAVE            = "~y~RHE: Get in a vehicle to save its handling";
        private const string NOTIF_HANDLING_SAVE_FAILED_NAME  = "~r~RHE: Save failed. Model name isn't a usable file name";
        private const string NOTIF_NO_VEHICLE_OVERWRITE       = "~y~RHE: Get in a vehicle to overwrite a saved handling";
        private const string NOTIF_NO_VEHICLE_APPLY           = "~y~RHE: Get in a vehicle to apply a handling";

        // [Notifications: Selective Copy] Toasts for copying a Preview value, a flag, or a whole category onto the current handling
        private const string NOTIF_COPY_FAILED                   = "~y~RHE: Copy failed: Unknown error, check log";
        private const string NOTIF_COPY_CATEGORY_ABSENT          = "~y~RHE: Unable to copy: Category does not exist in current handling";
        private const string NOTIF_COPY_ENTRY_ABSENT             = "~y~RHE: Unable to copy: AdvancedData entry does not exist in current handling";
        private const string NOTIF_COPY_NO_ROCKET_BOOST          = "~y~RHE: Unable to copy: Vehicle has no rocket boost";
        private const string NOTIF_COPY_NO_REAR_MOUNTED_TURRET   = "~y~RHE: Unable to copy: Vehicle has no rear mounted turret";
        private const string NOTIF_NO_VEHICLE_COPY               = "~y~RHE: Get in a vehicle to copy a field";
        private const string NOTIF_COPY_IDENTICAL                = "~y~RHE: Identical value in the current handling";
        private const string NOTIF_FLAG_COPIED                   = "~g~RHE: {0} copied";
        private const string NOTIF_FLAG_COPIED_CONFLICTS_CLEARED = "~g~RHE: {0} copied. Conflicting flags cleared";
        private const string NOTIF_CATEGORY_COPIED               = "~g~RHE: Copied {0}";
        private const string NOTIF_CATEGORY_COPIED_SKIPPED       = "~y~RHE: Copied {0}, {1} fields skipped";
        private const string NOTIF_CATEGORY_IDENTICAL            = "~y~RHE: {0} is identical in the current handling";

        // [Notifications: Undo And Redo] Toasts for undo, redo, and undo all
        private const string NOTIF_UNDO_APPLY_SKIPPED = "~y~RHE: Last applied handling undone, {0} fields skipped";
        private const string NOTIF_REDO_APPLY_SKIPPED = "~y~RHE: Undone handling reapplied, {0} fields skipped";
        private const string NOTIF_NO_VEHICLE_UNDO    = "~y~RHE: Get in a vehicle to undo";
        private const string NOTIF_NO_VEHICLE_REDO    = "~y~RHE: Get in a vehicle to redo";
        private const string NOTIF_NOTHING_TO_REDO    = "~y~RHE: Nothing to redo";
        private const string NOTIF_NOTHING_TO_UNDO    = "~y~RHE: Nothing to undo";
        private const string NOTIF_UNDO_FAILED        = "~y~RHE: Undo failed, check log";
        private const string NOTIF_REDO_FAILED        = "~y~RHE: Redo failed, check log";
        private const string NOTIF_UNDO_APPLY         = "~g~RHE: Last applied handling undone";
        private const string NOTIF_REDO_APPLY         = "~g~RHE: Undone handling reapplied";
        private const string NOTIF_UNDO_DONE          = "~g~RHE: Last action undone";
        private const string NOTIF_REDO_DONE          = "~g~RHE: Redo applied";
        private const string NOTIF_UNDO_ALL_DONE      = "~g~RHE: All edits undone";
        private const string NOTIF_UNDO_ALL_PARTIAL   = "~y~RHE: All edits undone, {0} could not be reverted, check log";

        // [Notifications: Vehicle Actions] Toasts for respawn, unload, selection, mounting, and deleting a vehicle
        private const string NOTIF_VEHICLE_MOUNTED         = "~g~RHE: Mounted {0}";
        private const string NOTIF_VEHICLE_SELECTED        = "~g~RHE: Selected {0}";
        private const string NOTIF_RELOAD_UNLOADS_CANCELED = "~g~RHE: Reloading. {0} pending unload(s) canceled, traffic and spawning restored";
        private const string NOTIF_SELECT_VEHICLE_AIM      = "RHE: Aim at a vehicle and left click or press Enter to confirm. {0} to cancel";
        private const string NOTIF_UNLOAD_ABORTED          = "~r~RHE: {0} unload aborted, an instance spawned before it unloaded";
        private const string NOTIF_UNLOAD_MARKED           = "~y~RHE: {0} marked for eviction. Respawn it when you receive the success notification";
        private const string NOTIF_SELECT_ALREADY_EDITED   = "~y~RHE: {0}'s handling is already being edited";
        private const string NOTIF_SELECT_EDITORS_FULL     = "~y~RHE: All {0} selected editors are in use";
        private const string NOTIF_UNLOAD_COMPLETE         = "~g~RHE: {0} unloaded. Respawn it to test edits";
        private const string NOTIF_UNLOAD_RESPAWNED        = "~g~RHE: {0} unloaded and respawned with your edits";
        private const string NOTIF_UNLOAD_NOT_GUARANTEED   = "~y~RHE: {0} unloaded but its physics data may not be refreshed, edits may not be applied";
        private const string NOTIF_RESPAWNED               = "~g~RHE: {0} respawned";
        private const string NOTIF_VEHICLE_UNMOUNTED       = "~g~RHE: Unmounted {0}";
        private const string NOTIF_NO_VEHICLE_RESPAWN      = "~y~RHE: Get in a vehicle to respawn it";
        private const string NOTIF_RESPAWN_MODEL_LOADING   = "~y~RHE: Model still loading. Try again in a moment";
        private const string NOTIF_NO_VEHICLE_UNLOAD       = "~y~RHE: Get in a vehicle to unload it";
        private const string NOTIF_TRAIN_REFUSED           = "~y~RHE: Trains can't be respawned or unloaded as they would crash the game";
        private const string NOTIF_SELECT_NONE             = "~y~RHE: No vehicle in view";
        private const string NOTIF_SELECTION_CANCELED      = "~y~RHE: Selection canceled";

        // [Notifications: Categories And Slots] Toasts for SubHandlingData categories, AdvancedData entries, and weapon slots
        private const string NOTIF_CATEGORY_WILL_BE_CREATED         = "~g~RHE: {0} Handling will be created in next save";
        private const string NOTIF_CATEGORY_REMOVED                 = "~y~RHE: {0} Handling removed. Will not be created";
        private const string NOTIF_ADVANCEDDATA_ENTRY_DELETED       = "~y~RHE: AdvancedData entry {0} deleted";
        private const string NOTIF_CATEGORY_EXCLUDED                = "~y~RHE: {0} Handling excluded from next save";
        private const string NOTIF_CATEGORY_INCLUDED                = "~g~RHE: {0} Handling included in next save";
        private const string NOTIF_NO_VEHICLE_SUBDATA_CREATE        = "~y~RHE: Get in a vehicle to create a SubHandlingData";
        private const string NOTIF_PHANTOM_DEFAULTS                 = "~y~RHE: New category created with placeholder defaults";
        private const string NOTIF_LIVE_CATEGORY_CREATED            = "~g~RHE: {0} Handling created";
        private const string NOTIF_LIVE_CATEGORY_CREATED_UNLOAD     = "~y~RHE: {0} Handling created. Use Unload Vehicle to apply it";
        private const string NOTIF_LIVE_CATEGORY_CREATED_RESPAWN    = "~y~RHE: {0} Handling created. Use Respawn Vehicle to apply it";
        private const string NOTIF_LIVE_CATEGORY_CREATE_FAILED      = "~y~RHE: {0} Handling created statically. {1}";
        private const string NOTIF_LIVE_CATEGORY_DELETED            = "~g~RHE: {0} Handling deleted";
        private const string NOTIF_LIVE_CATEGORY_DELETED_RESPAWN    = "~y~RHE: {0} Handling deleted. Use Respawn Vehicle to apply it";
        private const string NOTIF_LIVE_CATEGORY_DELETE_FAILED      = "~y~RHE: {0} Handling could not be deleted: {1}";
        private const string NOTIF_SEA_PLANE_DELETE_IN_WATER        = "~y~RHE: Remove any vehicle using this handling from or near the water before deleting, the game will crash otherwise";
        private const string NOTIF_SEA_PLANE_DELETED_UNLOAD         = "~y~RHE: SeaPlaneHandling deleted. Unload this vehicle before entering the water with it or it'll crash the game";
        private const string NOTIF_LIVE_CATEGORY_REQUIRED_EXCLUDED  = "~y~RHE: {0} Handling can't be deleted live on this vehicle type, excluded from next save instead";
        private const string NOTIF_LIVE_CATEGORY_SHARED_EXCLUDED    = "~y~RHE: {0} Handling can't be deleted live, another vehicle with this handling needs it. Excluded from next save instead";
        private const string NOTIF_LIVE_CATEGORY_CREATED_DEFAULTS   = "~y~RHE: {0} Handling created with default values. {1}";
        private const string NOTIF_LIVE_CATEGORY_CREATED_HOVER_HINT = "RHE: Hold the vehicle roof control (H by default) to switch hover mode";
        private const string NOTIF_LIVE_ADVANCEDDATA_FAILED         = "~y~RHE: AdvancedData entry could not be added live: {0}";
        private const string NOTIF_LIVE_ADVANCEDDATA_CREATE_FAILED  = "~y~RHE: AdvancedData created statically. {0}";
        private const string NOTIF_WORKS_UPSIDE_DOWN_FORCED         = "~y~RHE: SF_WORKS_UPSIDE_DOWN turned on, no " + HOVER_SLIDE_BONE_NAME + " bone, hover mode would crash";
        private const string NOTIF_ADVANCEDDATA_REMOVED             = "~y~RHE: AdvancedData removed, will not be created";
        private const string NOTIF_ADVANCEDDATA_ENTRY_DELETE_FAILED = "~r~RHE: AdvancedData entry delete failed";
        private const string NOTIF_ADVANCEDDATA_DELETE_FAILED       = "~r~RHE: AdvancedData delete failed";
        private const string NOTIF_ADVANCEDDATA_DELETED             = "~y~RHE: AdvancedData deleted";
        private const string NOTIF_WEAPON_FULL                      = "~y~RHE: All weapon slots are in use";
        private const string NOTIF_WEAPON_SLOT_REORDERED            = "~g~RHE: Weapon slot reordered";
        private const string NOTIF_WEAPON_SLOT_ADDED                = "~g~RHE: Weapon slot added";
        private const string NOTIF_WEAPON_SLOT_REMOVED              = "~g~RHE: Weapon slot removed";
        private const string NOTIF_WEAPON_TURRET_BOUNDARY           = "~y~RHE: Cannot reorder across turret slots on this game build";
        private const string NOTIF_WEAPON_HASH_AMBIGUOUS            = "~y~RHE: Several weapons match: {0}";
        private const string NOTIF_WEAPON_HASH_MORE                 = ", and {0} more";

        // [Notifications: Live Failure Reasons] The phrase a live failure toast ends with
        private const string REASON_POOL_FULL          = "The game's handling pool is full";
        private const string REASON_FAULTED            = "The game rejected the new object";
        private const string REASON_UNAVAILABLE        = "The plugin could not find this category";
        private const string REASON_NO_ANSWER          = "The plugin did not answer in time";
        private const string REASON_NOT_PRESENT        = "It is no longer on the vehicle";
        private const string REASON_LIVE_UNAVAILABLE   = "Live creation is unavailable";
        private const string REASON_VALUES_NOT_WRITTEN = "Its values could not be written";
        private const string REASON_REQUIRED_CATEGORY  = "The game reads it as required on this vehicle type, so removing it would crash the game";
        // The phrases of the refusals that judge every vehicle sharing the handling, of growth requests refused before the plugin, and of a request whose finish threw
        private const string REASON_SHARED_REQUIRED    = "Another vehicle with this handling needs it, so removing it would crash the game";
        private const string REASON_SHARED_PHANTOM     = "Another vehicle with this handling cannot hold it live, so creating it would crash the game";
        private const string REASON_GROWTH_PENDING     = "An array resize to the same capacity is already waiting on the plugin";
        private const string REASON_CAR_DELETE_PENDING = "The Car category is being deleted";
        private const string REASON_FINISH_FAILED      = "An error stopped the request from finishing";

        // [Notifications: Search] Toasts for the vehicle model search
        private const string NOTIF_SEARCH_TOO_SHORT         = "~y~RHE: Type at least {0} characters to search";
        private const string NOTIF_SEARCH_NO_MATCH          = "~y~RHE: No vehicle model matches {0}";
        private const string NOTIF_SEARCH_TRUNCATED         = "~y~RHE: Showing {0} of {1} matches";
        private const string NOTIF_SEARCH_RESULT_UNREADABLE = "~y~RHE: Unable to apply: This model's handling can't be read";

        // [Labels] Words shared between menu titles, banners, and hints
        private const string LABEL_SAVE                 = "Save";
        private const string LABEL_PREVIEW              = "Preview";
        private const string LABEL_DIFF                 = "Diff";
        private const string LABEL_SETTINGS             = "Settings";
        private const string LABEL_EXPORTS              = "Exports";
        private const string LABEL_AUTOSAVES            = "Autosaves";
        private const string LABEL_SAVED_HANDLINGS      = "Saved Handlings";
        private const string LABEL_BANNER_DEFAULT       = "Default";
        private const string LABEL_BANNER_BLUE          = "Blue";
        private const string LABEL_BANNER_RED           = "Red";
        private const string LABEL_BANNER_PURPLE        = "Purple";
        private const string LABEL_BANNER_GREEN         = "Green";
        private const string LABEL_BANNER_BLACK         = "Black";
        private const string LABEL_BANNER_CUSTOM        = "Custom";
        private const string LABEL_LOGGING_WARN_ERROR   = "Warn/Error";
        private const string LABEL_LOGGING_ALL          = "All";
        private const string LABEL_LOGGING_DEFAULT_PERF = "Default + Perf";
        private const string LABEL_LOGGING_DEFAULT_INFO = "Default + Info";
        private const string LABEL_POSITION_LEFT        = "Left";
        private const string LABEL_POSITION_RIGHT       = "Right";
        private const string LABEL_SCRIPT_NAME          = "RageHandlingEditor";
        // The accepted values the key rebind toasts name
        private const string LABEL_ACCEPTED_ACTION_MODIFIER = "Control, Shift, Alt, or Tab";
        private const string LABEL_ACCEPTED_TOGGLE_KEY      = "one non-modifier key other than Escape or Backspace";

        // [Hints] Instructional hint labels
        private const string HINT_CANCEL_KEYS          = "Backspace/{0}";
        private const string HINT_CANCEL_KEY_BACKSPACE = "Backspace";
        private const string HINT_RESPAWN              = "Respawn";
        private const string HINT_EXPORTS              = "Exports";
        private const string HINT_UNDO_ALL             = "Undo All";
        private const string HINT_REDO                 = "Redo";
        private const string HINT_UNDO                 = "Undo";
        private const string HINT_DUPLICATE            = "Duplicate";
        private const string HINT_COPY                 = "Copy";
        private const string HINT_RENAME               = "Rename";
        private const string HINT_OVERWRITE            = "Overwrite";
        private const string HINT_DELETE               = "Delete";
        private const string HINT_CLEAR_INPUT          = "Clear Input";
        private const string HINT_CLEAR_SEARCH         = "Clear Search";
        private const string HINT_RESET_TO_DEFAULT     = "Reset To Default";
        private const string HINT_TELEPORT             = "Teleport";
        private const string HINT_DELETE_VEHICLE       = "Delete Vehicle";

        // [Handling File Comment] Text written into a saved handling file
        // Written above a saved handling's handlingName element when the name does not resolve to the saving vehicle's own handling entry,
        // and the padding spaces keep the text clear of the comment delimiters
        private const string HANDLING_NAME_MISMATCH_COMMENT = " This name may not match the vehicle's handlingId in vehicles.meta, double check it after pasting to avoid game crashing ";

        // [Paths] Files and folders the script reads and writes, relative to the game folder
        // The script's own folder, created at startup so its file watcher has a folder to watch on a first run
        private const string SCRIPT_FOLDER_PATH = "scripts\\RageHandlingEditor";
        // Written by the RageMemoryBridge plugin at game init, one entry per registered vehicle model with its model name and handling id
        // The only place a full model name or handling id exists at runtime, since the game keeps just the hashes. The bare file name resolves against the game folder
        private const string MODEL_METADATA_FILE_PATH = "ModelMetaData.json";
        // Where exports are written
        private const string EXPORTS_DIR = "scripts\\RageHandlingEditor\\Exports\\";
        // Subfolder of the Exports folder that holds one autosave per vehicle model. The main Exports folder is never written by autosave
        private const string AUTOSAVES_SUBFOLDER = "Autosaves";
        // Where the state a script reload carries is written when the script unloads, which the next load reads once and deletes. One section per kind of state
        private const string RELOAD_STATE_PATH = "scripts\\RageHandlingEditor\\ReloadState";
        // Added to a file's own path for the copy a write fills before it replaces the file, which no listing pattern matches
        private const string REPLACING_WRITE_SUFFIX = ".tmp";
        // The 2 script hooks the log header reads the file versions of
        private const string SHV_FILE_PATH = "ScriptHookV.dll";
        private const string SHVDN_FILE_PATH = "ScriptHookVDotNet.asi";

        // [INI: Sections] The INI's section header lines
        private const string SECTION_Controls        = "[Controls]";
        private const string SECTION_Script          = "[Script]";
        private const string SECTION_Exporting       = "[Exporting]";
        private const string SECTION_HandlingPreview = "[HandlingPreview]";
        private const string SECTION_Visualization   = "[Visualization]";
        private const string SECTION_Appearance      = "[Appearance]";
        private const string SECTION_Debug           = "[Debug]";

        // [INI: Keys] The INI's key names, grouped by section
        // [Controls]
        private const string KEY_CTL_MenuActionModifier = "MenuActionModifier";
        private const string KEY_CTL_EnableCheatString  = "EnableCheatString";
        private const string KEY_CTL_KeyToggleMenu      = "KeyToggleMenu";
        // [Script]
        private const string KEY_SCR_AutoRespawn           = "AutoRespawn";
        private const string KEY_SCR_CheckForUpdates       = "CheckForUpdates";
        private const string KEY_SCR_ReassertDriftedFields = "ReassertDriftedFields";
        private const string KEY_SCR_ResetAllSettings      = "ResetAllSettings";
        // [Exporting]
        private const string KEY_EXG_AlwaysOverwrite = "AlwaysOverwrite";
        private const string KEY_EXG_Autosave        = "Autosave";
        private const string KEY_EXG_PromptOnSave    = "PromptOnSave";
        // [HandlingPreview]
        private const string KEY_HPV_PreviewByDefault = "PreviewByDefault";
        private const string KEY_HPV_ShowPreviewDiff  = "ShowPreviewDiff";
        // [Visualization]
        private const string KEY_VIS_IsolatedDrawing           = "IsolatedDrawing";
        private const string KEY_VIS_ShowHandlingVisualization = "ShowHandlingVisualization";
        // [Appearance]
        private const string KEY_APR_BannerColorRGB = "BannerColorRGB";
        private const string KEY_APR_MaxRows        = "MaxRows";
        private const string KEY_APR_MenuPosition   = "MenuPosition";
        // [Debug]
        private const string KEY_DBG_AlwaysClearLogOnReload = "AlwaysClearLogOnReload";
        private const string KEY_DBG_LoggingMode            = "LoggingMode";

        // [INI: Comments] The INI's comment text, written with its prefix by AppendIniComment
        // [Controls]
        private const string CMT_CTL_MenuActionModifier1 = "Held alongside the HUD's hotkeys while the menu is open";
        private const string CMT_CTL_MenuActionModifier2 = "Options: Control, Shift, Alt, or Tab";
        private const string CMT_CTL_EnableCheatString   = "String typed into the game's cheat field that also opens the menu. Leave empty to disable it";
        private const string CMT_CTL_KeyToggleMenu       = "Opens/closes the editor. Leave empty to unbind it";
        // [Script]
        private const string CMT_SCR_AutoRespawn1 = "Off, Instant, or Delayed. Respawns the vehicle after an edit to anything that only takes effect after one";
        // A template: AutoRespawnDelayedComment fills {0} with the Delayed wait in seconds
        private const string CMT_SCR_AutoRespawn2          = "Delayed waits {0} seconds after the last cycler step. Typed values, flag toggles, weapon slot changes, undo, redo, and applying a handling always respawn at once";
        private const string CMT_SCR_CheckForUpdates       = "Checks GitHub for the latest release per game session, and posts a notification if a newer version is available";
        private const string CMT_SCR_ReassertDriftedFields = "Reapplies any edited field or flag that has been silently reset or overwritten by the game from what you last set it to";
        private const string CMT_SCR_ResetAllSettings      = "Reset script settings to defaults";
        // [Exporting]
        private const string CMT_EXG_AlwaysOverwrite = "Overwrite an existing export when saving with the same name, instead of creating a new file";
        private const string CMT_EXG_Autosave        = "Saves the vehicle's handling to Exports\\Autosaves after every edit, for crash recovery. Always overwrites an existing file. Your exports are never touched";
        private const string CMT_EXG_PromptOnSave    = "True asks for a handling name and a file name before each save. False saves at once under the vehicle's own names, even with Always Overwrite on";
        // [HandlingPreview]
        private const string CMT_HPV_PreviewByDefault = "Show the Preview pane in the Saved Handlings menu by default";
        private const string CMT_HPV_ShowPreviewDiff  = "Shows an additional pane next to Preview which shows the difference between the original handling and the highlighted export, for comparison";
        // [Visualization]
        private const string CMT_VIS_IsolatedDrawing            = "Draws a visualizer only while a field it represents is highlighted, to reduce visual clutter";
        private const string CMT_VIS_ShowHandlingVisualization1 = "Which visualization elements to draw while editing. Combine multiple with a pipe (e.g. \"Bounding | COM\")";
        private const string CMT_VIS_ShowHandlingVisualization2 = "Options: Disabled | All | Any combo of Bounding, COM, RollCenter, Submerged, AntiRollBarBias, CenterOfGravity, WheelBounds, SuspensionLoad, RudderOffsets, PropRadius, Impeller, WaterSamples, KeelSpheres";
        // [Appearance]
        private const string CMT_APR_BannerColorRGB = "Menu banner color. RGB or HEX";
        private const string CMT_APR_MaxRows1       = "How many item rows are shown in the menu at once";
        private const string CMT_APR_MaxRows2       = "Range: 5-15";
        private const string CMT_APR_MenuPosition   = "Right or Left";
        // [Debug]
        private const string CMT_DBG_AlwaysClearLogOnReload = "Clear the log on every script load instead of appending sessions. Disable this whenever you run into a bug or error you want to report and don't want script reloads or game relaunches to wipe the log of any needed info";
        private const string CMT_DBG_LoggingMode            = "Modes: 0 = Warn & Error, 1 = All, 2 = Default + Perf, 3 = Default + Info";

        // [INI: Defaults] Compiled defaults of INI text values
        // EnableCheatString's compiled default. Also what a load falls back to when it would leave the menu with no entry point and no last value exists
        private const string DEFAULT_CHEAT_STRING = "Q";

        // [Identifiers: Handling File XML] Tags and indents of the saved handling XML
        // Matches the actual depth-6/5 nesting inside the full XDocument tree, since System.Xml.Linq auto-indents tags but not raw Value text
        private const string ARRAY_ITEM_INDENT  = "            ";
        private const string ARRAY_CLOSE_INDENT = "          ";
        // TurretPitchLimitData holds one Item per turret slot, each carrying the 6 TurretPitchLimitFields members as value= children
        private const string TURRET_PITCH_LIMIT_TAG = "TurretPitchLimitData";
        // The XML tag of the AdvancedData block, which every AdvancedData row of a saved handling carries in its field ref
        // The entry is in Index and the member in Member
        private const string ADVANCED_DATA_TAG = "AdvancedData";
        // The XML tags of a weapon slot's name table arrays, which the WeaponHash, WeaponSeat, and WeaponVehicleModType rows of a slot carry in their field refs
        private const string WEAPON_HASH_ARRAY_TAG     = "uWeaponHash";
        private const string WEAPON_SEAT_ARRAY_TAG     = "WeaponSeats";
        private const string WEAPON_MOD_TYPE_ARRAY_TAG = "vehicleModType";
        // The XML tags of a saved handling's document structure: HandlingData holds one Item, whose SubHandlingData holds one Item per category
        private const string HANDLING_DATA_TAG         = "HandlingData";
        private const string ITEM_TAG                  = "Item";
        private const string SUB_HANDLING_DATA_TAG     = "SubHandlingData";
        private const string HANDLING_NAME_TAG         = "handlingName";
        // The XML tag of each flag family's hex value
        private const string MODEL_FLAGS_TAG           = "strModelFlags";
        private const string HANDLING_FLAGS_TAG        = "strHandlingFlags";
        private const string DAMAGE_FLAGS_TAG          = "strDamageFlags";
        private const string ADVANCED_FLAGS_TAG        = "strAdvancedFlags";
        private const string SPECIAL_FLIGHT_FLAGS_TAG  = "strFlags";

        // [Identifiers: Categories] The short name of each sub-handling category, which every editor key, undo record, and Preview ref uses, in the editor's one order
        private const string CATEGORY_CAR             = "Car";
        private const string CATEGORY_BIKE            = "Bike";
        private const string CATEGORY_FLYING          = "Flying";
        private const string CATEGORY_VERTICAL_FLYING = "VerticalFlying";
        private const string CATEGORY_SPECIAL_FLIGHT  = "SpecialFlight";
        private const string CATEGORY_BOAT            = "Boat";
        private const string CATEGORY_SEA_PLANE       = "SeaPlane";
        private const string CATEGORY_SUBMARINE       = "Submarine";
        private const string CATEGORY_TRAILER         = "Trailer";
        private const string CATEGORY_WEAPON          = "Weapon";

        // [Identifiers: Plugins] The bundled plugins' module and export names
        // The gear ratio patch's loaded module name, and its export reporting whether its correction installed
        private const string GEAR_RATIO_PATCH_MODULE_NAME   = "GearRatioPatch.asi";
        private const string GEAR_RATIO_PATCH_ACTIVE_EXPORT = "IsGearRatioPatchActive";
        // The bundled plugin's loaded module name and its name in a failure reason
        // Its 3 exports that queue a model's fragment eviction, read its outcome, and cancel the request
        private const string RAGE_MEMORY_BRIDGE_MODULE_NAME        = "RageMemoryBridge.asi";
        private const string RAGE_MEMORY_BRIDGE_PLUGIN_NAME        = "RageMemoryBridge";
        private const string REQUEST_FRAGMENT_EVICTION_EXPORT      = "RequestFragmentEviction";
        private const string READ_FRAGMENT_EVICTION_OUTCOME_EXPORT = "ReadFragmentEvictionOutcome";
        private const string CANCEL_FRAGMENT_EVICTION_EXPORT       = "CancelFragmentEviction";
        // The same plugin's exports that queue a live SubHandlingData creation or deletion and read its outcome
        private const string REQUEST_SUBHANDLING_CREATION_EXPORT      = "RequestSubHandlingCreation";
        private const string READ_SUBHANDLING_CREATION_OUTCOME_EXPORT = "ReadSubHandlingCreationOutcome";
        private const string REQUEST_SUBHANDLING_DELETION_EXPORT      = "RequestSubHandlingDeletion";
        private const string READ_SUBHANDLING_DELETION_OUTCOME_EXPORT = "ReadSubHandlingDeletionOutcome";
        // The same plugin's exports that queue a growth of a Car object's AdvancedData array and read its outcome
        private const string REQUEST_ADVANCEDDATA_CAPACITY_EXPORT      = "RequestAdvancedDataCapacity";
        private const string READ_ADVANCEDDATA_CAPACITY_OUTCOME_EXPORT = "ReadAdvancedDataCapacityOutcome";
        // The same plugin's exports that cancel a request of each kind once the caller gives up on it
        private const string CANCEL_SUBHANDLING_CREATION_EXPORT  = "CancelSubHandlingCreation";
        private const string CANCEL_SUBHANDLING_DELETION_EXPORT  = "CancelSubHandlingDeletion";
        private const string CANCEL_ADVANCEDDATA_CAPACITY_EXPORT = "CancelAdvancedDataCapacity";
        // The same plugin's export that reads whether its setup finished and succeeded
        private const string READ_SUBHANDLING_SETUP_STATE_EXPORT = "ReadSubHandlingSetupState";

        // [Identifiers: Update Check] The request for the latest published release and the reply field it reads
        // The releases endpoint answers with the newest release that is neither a draft nor a prerelease
        private const string UPDATE_CHECK_URL = "https://api.github.com/repos/KieranBLK/RageHandlingEditor/releases/latest";
        // The endpoint refuses a request with no User-Agent, which carries this product name and the running version
        private const string UPDATE_CHECK_USER_AGENT_PRODUCT = "RageHandlingEditor";
        private const string UPDATE_CHECK_ACCEPT             = "application/vnd.github+json";
        // The reply's release tag, written as the version after a leading v
        private const string UPDATE_CHECK_TAG_KEY    = "tag_name";
        private const string UPDATE_CHECK_TAG_PREFIX = "v";

        // [Identifiers: Reload State] Markers and line tags of the reload state file
        // Starts the menu restore section's line that holds Preview's on or off state and position. The menu line starts with a digit
        private const string PREVIEW_RESTORE_LINE_TAG = "Preview";
        // Starts the menu restore section's line that holds the widths on screen at the reload
        private const string WIDTH_RESTORE_LINE_TAG = "Widths";
        // Written in place of an empty snapshot slot. The Base64 alphabet never contains it
        private const string EMPTY_SNAPSHOT_MARKER = "-";
        // The stack word of an UndoHistory line. Baseline starts the Undo All baseline line, and Undo and Redo start the line of an entry on that stack
        private const string BASELINE_LINE_TAG = "Baseline";
        private const string UNDO_LINE_TAG     = "Undo";
        private const string REDO_LINE_TAG     = "Redo";
        // The record word of a PhantomSubData line: A toggled category, an adopted category, a category's export-only element, or a live Car's pending block
        private const string TOGGLE_LINE_TAG                = "Toggle";
        private const string ADOPTED_LINE_TAG               = "Adopted";
        private const string ELEMENT_LINE_TAG               = "Element";
        private const string PENDING_ADVANCED_DATA_LINE_TAG = "PendingAdvancedData";

        // [Identifiers: Undo] Reasons undo records carry
        // The not-applied reason for a real array AdvancedData undo or redo on a build whose raw offsets are unsupported
        private const string LIVE_ADVANCED_DATA_UNSUPPORTED_REASON = "this game build does not support AdvancedData edits";
        // The not-applied reason for a SeaPlane deletion refused while a vehicle on its handling is on or near the water
        private const string SEA_PLANE_NEAR_WATER_REASON = "a vehicle with this handling is on or near the water, which would crash the game";

        // [Identifiers: Keys] Notification keys
        // Notification key of the confirm hint, which is removed the moment the row disarms so it never outlasts the flash
        private const string NOTIFICATION_KEY_ARMED_ACTION_HINT = "armed_action_hint";
        // The key a notification replaces itself under and takes its cooldown from, so 2 toasts that share a key never stack
        private const string NOTIFICATION_KEY_ADVANCEDDATA_DELETE_ERROR           = "advanceddata_delete_error";
        private const string NOTIFICATION_KEY_ADVANCEDDATA_ENTRY_DELETE           = "advanceddata_entry_delete";
        private const string NOTIFICATION_KEY_DELETE_VEHICLE                      = "delete_vehicle";
        private const string NOTIFICATION_KEY_FIELD_ENTRY_CANCELED                = "field_entry_canceled";
        private const string NOTIFICATION_KEY_FIELD_ENTRY_HARD_BOUNDED            = "field_entry_hard_bounded";
        private const string NOTIFICATION_KEY_FIELD_ENTRY_INVALID_ACTION_MODIFIER = "field_entry_invalid_action_modifier";
        private const string NOTIFICATION_KEY_FIELD_ENTRY_INVALID_BANNER_COLOR    = "field_entry_invalid_banner_color";
        private const string NOTIFICATION_KEY_FIELD_ENTRY_INVALID_TOGGLE_KEY      = "field_entry_invalid_toggle_key";
        private const string NOTIFICATION_KEY_HANDLING_APPLY                      = "handling_apply";
        private const string NOTIFICATION_KEY_HANDLING_DELETE                     = "handling_delete";
        private const string NOTIFICATION_KEY_HANDLING_DUPLICATE                  = "handling_duplicate";
        private const string NOTIFICATION_KEY_HANDLING_NAME_MISMATCH              = "handling_name_mismatch";
        private const string NOTIFICATION_KEY_HANDLING_RENAME                     = "handling_rename";
        private const string NOTIFICATION_KEY_HANDLING_SAVE                       = "handling_save";
        private const string NOTIFICATION_KEY_HANDLING_SAVE_ERROR                 = "handling_save_error";
        private const string NOTIFICATION_KEY_INI_INVALID                         = "ini_invalid";
        private const string NOTIFICATION_KEY_INI_RELOAD                          = "ini_reload";
        private const string NOTIFICATION_KEY_KEY_CAPTURE_ARMED                   = "key_capture_armed";
        private const string NOTIFICATION_KEY_LIVE_ADVANCEDDATA                   = "live_advanceddata";
        private const string NOTIFICATION_KEY_LIVE_SUBDATA_HOVER_HINT             = "live_subdata_hover_hint";
        private const string NOTIFICATION_KEY_MENU_ENTRY_POINT_KEPT               = "menu_entry_point_kept";
        private const string NOTIFICATION_KEY_NO_VEHICLE_APPLY                    = "no_vehicle_apply";
        private const string NOTIFICATION_KEY_NO_VEHICLE_COPY                     = "no_vehicle_copy";
        private const string NOTIFICATION_KEY_NO_VEHICLE_EDIT                     = "no_vehicle_edit";
        private const string NOTIFICATION_KEY_NO_VEHICLE_OVERWRITE                = "no_vehicle_overwrite";
        private const string NOTIFICATION_KEY_NO_VEHICLE_REDO                     = "no_vehicle_redo";
        private const string NOTIFICATION_KEY_NO_VEHICLE_RESPAWN                  = "no_vehicle_respawn";
        private const string NOTIFICATION_KEY_NO_VEHICLE_SAVE                     = "no_vehicle_save";
        private const string NOTIFICATION_KEY_NO_VEHICLE_SUBDATA_CREATE           = "no_vehicle_subdata_create";
        private const string NOTIFICATION_KEY_NO_VEHICLE_UNDO                     = "no_vehicle_undo";
        private const string NOTIFICATION_KEY_NO_VEHICLE_UNLOAD                   = "no_vehicle_unload";
        private const string NOTIFICATION_KEY_REDO_EMPTY                          = "redo_empty";
        private const string NOTIFICATION_KEY_RESPAWN                             = "respawn";
        private const string NOTIFICATION_KEY_RESPAWN_LOADING                     = "respawn_loading";
        private const string NOTIFICATION_KEY_SEARCH_RESULT_UNREADABLE            = "search_result_unreadable";
        private const string NOTIFICATION_KEY_SELECT_VEHICLE                      = "select_vehicle";
        private const string NOTIFICATION_KEY_SELECT_VEHICLE_FULL                 = "select_vehicle_full";
        private const string NOTIFICATION_KEY_SELECT_VEHICLE_NONE                 = "select_vehicle_none";
        private const string NOTIFICATION_KEY_SELECT_VEHICLE_SCOPED               = "select_vehicle_scoped";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_CATEGORY             = "selective_copy_category";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_CATEGORY_ABSENT      = "selective_copy_category_absent";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_ENTRY_ABSENT         = "selective_copy_entry_absent";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_FAILED               = "selective_copy_failed";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_FLAG                 = "selective_copy_flag";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_IDENTICAL            = "selective_copy_identical";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_NO_ROCKET_BOOST      = "selective_copy_no_rocket_boost";
        private const string NOTIFICATION_KEY_SELECTIVE_COPY_NO_TURRET            = "selective_copy_no_turret";
        private const string NOTIFICATION_KEY_SEA_PLANE_UNLOAD_WARNING            = "sea_plane_unload_warning";
        private const string NOTIFICATION_KEY_SETTINGS_RESET                      = "settings_reset";
        private const string NOTIFICATION_KEY_SUBDATA_EXCLUSION                   = "subdata_exclusion";
        private const string NOTIFICATION_KEY_TRAIN_REFUSED                       = "train_refused";
        private const string NOTIFICATION_KEY_UNDO_EMPTY                          = "undo_empty";
        private const string NOTIFICATION_KEY_UNDO_REDO                           = "undo_redo";
        private const string NOTIFICATION_KEY_UNDO_UNAPPLIED                      = "undo_unapplied";
        private const string NOTIFICATION_KEY_UNMOUNT_SELECTED                    = "unmount_selected";
        private const string NOTIFICATION_KEY_UPDATE_AVAILABLE                    = "update_available";
        private const string NOTIFICATION_KEY_VEHICLE_SEARCH                      = "vehicle_search";
        private const string NOTIFICATION_KEY_WEAPON_FULL                         = "weapon_full";
        private const string NOTIFICATION_KEY_WEAPON_HASH_AMBIGUOUS               = "weapon_hash_ambiguous";
        private const string NOTIFICATION_KEY_WEAPON_SLOT_ACTION                  = "weapon_slot_action";
        private const string NOTIFICATION_KEY_WEAPON_SLOT_TURRET_BOUNDARY         = "weapon_slot_turret_boundary";
        private const string NOTIFICATION_KEY_WORKS_UPSIDE_DOWN_FORCED            = "works_upside_down_forced";
        // The start of a key built from a prefix and a variable part, so each variable part replaces and cools down on its own
        private const string NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA     = "live_subdata_";
        private const string NOTIFICATION_KEY_PREFIX_UNLOAD           = "unload_";
        private const string NOTIFICATION_KEY_PREFIX_PHANTOM_DEFAULTS = "phantom_defaults_";
        private const string NOTIFICATION_KEY_PREFIX_ERROR            = "error:";

        // [Identifiers: Log] Text the log file's own structure carries
        // Starts each session's header line, followed by the session number and a closing mark. The next load finds the highest number to continue from
        private const string LOG_SESSION_MARKER = "=== Session-";

        // [Identifiers: Weapons] Weapon name texts the weapon hash parsing matches
        // How the live and export-only weapon hash cyclers show a hash outside WeaponHashTable
        private const string CUSTOM_WEAPON_HASH_LABEL = "Custom";
        // The start of the registry and undo text for such a hash, followed by the raw hash in hex
        private const string CUSTOM_WEAPON_HASH_REGISTRY_PREFIX = "Custom:";
        // The start of a weapon hash display text outside WeaponHashTable, followed by the raw hash in hex and a closing parenthesis
        private const string CUSTOM_WEAPON_HASH_DISPLAY_PREFIX = "Custom (0x";
        // The lead every native weapon name carries, and typed text may leave it off
        private const string WEAPON_NATIVE_NAME_PREFIX = "VEHICLE_WEAPON_";

        // [Identifiers: Game Assets] Names the game itself defines: Sounds, bones, and its unresolved display name
        // The bone hover mode slides every frame while SF_WORKS_UPSIDE_DOWN is off
        private const string HOVER_SLIDE_BONE_NAME = "misc_e";
        // The game's vertical flight mode check: This nozzle bone or the 2 named models give a plane the mode beside its flag, and the excluded model never has it
        private const string VERTICAL_FLIGHT_NOZZLE_BONE_NAME    = "nozzles_f";
        private const string VERTICAL_FLIGHT_TULA_MODEL_NAME     = "tula";
        private const string VERTICAL_FLIGHT_RAIJU_MODEL_NAME    = "raiju";
        private const string VERTICAL_FLIGHT_EXCLUDED_MODEL_NAME = "titan2";
        // The display name the game returns for a model with no gameName in vehicles.meta
        private const string UNRESOLVED_DISPLAY_NAME = "CARNOTFOUND";
        // Frontend sound played with the green unload notification
        private const string UNLOAD_SUCCESS_SOUND_NAME = "Text_Arrive_Tone";
        private const string UNLOAD_SUCCESS_SOUND_SET  = "Phone_SoundSet_Default";

        // [Identifiers: Reflection] Names the flag offset is read through by reflection
        // Where the API core keeps the flag bitset's offset, which is read by reflection since scripts do not compile against the core
        private const string CORE_VEHICLE_MEMORY_TYPE  = "SHVDN.NativeMemory+Vehicle";
        private const string CORE_FLAG_OFFSET_PROPERTY = "FirstVehicleFlagsOffset";
    }
}
