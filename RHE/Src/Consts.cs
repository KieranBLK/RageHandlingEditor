using System;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // =======
        // VEHICLE
        // =======
        // --Respawn.cs--
        // MODIFY_VEHICLE_TOP_SPEED's argument is a percentage change. 0 applies no change and still rebuilds the transmission and drag from the live handling
        // It then re-applies every installed stat and toggle mod, which rebuilds the gears from InitialDriveGears when the vehicle has a mod kit
        private const float SPAWN_REFRESH_TOP_SPEED_PERCENT = 0.0f;
        // GET_VEHICLE_MOD_KIT's value for a vehicle with no mod kit assigned
        private const int NO_VEHICLE_MOD_KIT = 0xFFFF;
        // Game time the Delayed mode of Auto Respawn waits after the last respawn-requiring Left or Right step
        private const int AUTO_RESPAWN_DELAY_MS = 1500;
        // A plane's engine warm-up timer's maximum, in seconds. A captured value outside 0 to this is never copied onto the new plane
        private const float WARM_UP_MAX_SECONDS = 5.0f;
        // The glider states that mean the wings are out or opening, and the highest valid state. A captured value outside 0 to the maximum is dropped
        private const int GLIDER_STATE_DEPLOYING_WINGS = 1;
        private const int GLIDER_STATE_GLIDING         = 2;
        private const int GLIDER_STATE_MAX             = 3;
        // The wing deployed ratio's range, retracted to deployed. A captured value outside it is never written onto the new vehicle
        private const float WING_DEPLOYED_RATIO_RETRACTED = -0.15f;
        private const float WING_DEPLOYED_RATIO_DEPLOYED  = 0.0f;
        // The game moves the wing bones only on a frame where the wing position differs from its target, so the restore writes it this far short
        // One frame of the game's own wing speed covers the gap, so the wings reach the captured position at once instead of staying in the closed pose
        private const float WING_DEPLOYED_RATIO_NUDGE = 0.001f;
        // --UnloadVehicle.cs--
        // Ticks between pending-unload checks
        private const int UNLOAD_POLL_INTERVAL = 1;
        // Game milliseconds the model must stay unloaded before an unload whose fragment was not evicted ends
        // It is the eviction's retry window, and without the plugin the wait that gives the game a chance to drop the fragment itself
        private const int UNLOAD_SETTLE_MS = 8000;
        // Game milliseconds from the confirm within which the eviction and the model reload must finish for the vehicle to respawn on its own
        // Past it the player has likely moved on, so the unload ends with the notification asking for a manual respawn
        private const int UNLOAD_AUTO_RESPAWN_WINDOW_MS = 1000;
        // Half-size of the box, in meters, that switches off every car generator while an unload waits. Larger than the whole map in every direction
        private const float CAR_GENERATOR_BOX_HALF_SIZE = 100000.0f;
        // The game's default vehicle population budget level (0 to 3), restored once no unload is pending
        private const int DEFAULT_VEHICLE_POPULATION_BUDGET = 3;
        // --LiveSubData.cs--
        // Game milliseconds a live SubHandlingData creation request may stay pending before it is given up and falls back to an export-only category
        // The plugin takes a request on its next frame, so a longer wait means the plugin is not running its loop
        private const int LIVE_SUBDATA_REQUEST_TIMEOUT_MS = 3000;
        // --VehicleTargeting.cs--
        // How far Select Vehicle's pick reaches, in meters
        // It is longer than any distance a vehicle stays loaded, so visibility, not distance, limits what can be selected
        private const float SELECT_VEHICLE_RANGE = 1500.0f;
        // Meters short of the vehicle's hit point the occlusion line stops, so it never reports the ground under the vehicle as a blocker
        private const float SELECT_VEHICLE_OCCLUSION_MARGIN = 0.5f;
        // Below this, a ray direction component counts as parallel to that bounds axis
        private const float SELECT_VEHICLE_PARALLEL_EPSILON = 1.0e-6f;
        // --WeaponData.cs--
        // A newly grown phantom or export-only weapon slot seeds from this index
        // None is a harmless placeholder in a saved XML attribute, matching every other unset-slot convention in the editor
        private const int DEFAULT_PHANTOM_WEAPON_SLOT_HASH_INDEX = 0;
        // The longest WeaponHash text the prompt accepts, since native names run past the 20 characters every other prompt allows
        private const int WEAPON_HASH_INPUT_MAX_LENGTH = 60;
        // The shortest typed text that may match a weapon by its start, so one letter never picks a weapon
        private const int WEAPON_HASH_MIN_PREFIX_LENGTH = 2;
        // How many matching weapon names an ambiguous entry lists in its toast
        private const int WEAPON_HASH_CANDIDATES_SHOWN = 5;
        // --WeaponSlots.cs--
        // Size in bytes of a float in the raw turret arrays, which every per-slot raw-offset read and write indexes by slotIndex * FLOAT_FIELD_STRIDE
        private const int FLOAT_FIELD_STRIDE = 4;
        // The fields a packed weapon slot record writes before its turret floats: The hash, seat, and mod type
        // A PhantomWeaponSlotDelete record packs the same 3 first, then the TurretFields values, then the pitch limits
        private const int WEAPON_SLOT_STATE_LEADING_FIELD_COUNT = 3;
        // --NativeMemory.cs--
        // A child count above this is treated as an unreadable composite rather than scanned
        private const int COMPOSITE_CHILD_COUNT_MAX = 256;
        // Upper bound the menu allows for AdvancedData's Slot and Index, which are 32-bit ints in memory
        private const int ADVANCED_DATA_SLOT_INDEX_MAX = 255;
        // Lowest Slot the menu allows: -1, the game's parser schema init value for an entry with no slot
        private const int ADVANCED_DATA_SLOT_MIN = -1;
        // A count exceeding this, or exceeding the array's own reported capacity, is treated as a corrupt or unreadable read rather than trusted
        // It is also the most entries any AdvancedData menu lists or lets Add Slot create
        private const int ADVANCEDDATA_COUNT_SANITY_CEILING = 64;
        // A flag bitmask's valid bit-index range, which bounds every strXxxFlags/AdvancedFlags checkbox write
        private const int FLAG_BIT_COUNT = 32;
        // A game-owned handling struct's address stays valid for the life of the process, so a short TTL costs only a small, bounded re-validation delay
        // Nearly every repeat read of the same field then skips VirtualQuery
        // An access violation at read or write time evicts the offending entry (EvictRawAddressCacheEntry), so a stale cache corrects itself on its first real failure
        private const int RAW_ADDRESS_CACHE_TTL_MS = 500;
        // Milliseconds between sweeps that drop every entry past its TTL, so an address validated once and never read again does not stay cached for the whole session
        private const int RAW_ADDRESS_CACHE_PRUNE_INTERVAL_MS = 30000;
        // --EditorContext.cs--
        // Extra editors beyond Current, and the sole place the editor count is declared. Every loop over the trees reads the array length
        private const int MAX_ADDITIONAL_EDITORS = 3;

        // ===========
        // VISUALIZERS
        // ===========
        // --Hud.cs--
        // [Elements: Boat Groups] Boat elements grouped by the values they read
        // The thrust point (rudder offsets, propeller) and the water sample box (Water Samples, keel spheres). The last const is every boat element
        private const HandlingVisualizationElements VISUALIZATION_THRUST_POINT_ELEMENTS = HandlingVisualizationElements.RudderOffsets | HandlingVisualizationElements.PropRadius;
        private const HandlingVisualizationElements VISUALIZATION_WATER_BOX_ELEMENTS    = HandlingVisualizationElements.WaterSamples | HandlingVisualizationElements.KeelSpheres;
        private const HandlingVisualizationElements VISUALIZATION_BOAT_ELEMENTS         = VISUALIZATION_THRUST_POINT_ELEMENTS | VISUALIZATION_WATER_BOX_ELEMENTS | HandlingVisualizationElements.Impeller;
        // [Shared: Build Timing] How every visualizer builds in and out and glides, in seconds unless stated otherwise
        // Each visualizer's own build seconds sit in its section
        // How many build slots an animation state holds, one per bit position of HandlingVisualizationElements up to its highest flag
        private const int VISUALIZATION_ELEMENT_SLOT_COUNT = 15;
        // The build seconds of a build slot no element uses. Any positive value keeps its build at 0 with no division by zero
        private const float VISUALIZATION_UNUSED_SLOT_BUILD_SECONDS = 2.0f;
        // A vehicle's visualizers build faster as it speeds up, so a build keeps pace with the sense of speed
        // Between the start and full speeds in miles per hour, each build duration shrinks in a straight line toward the shortest duration in seconds
        // It stays there above the full speed, and a duration already at or under the shortest one never changes
        private const float VISUALIZATION_FAST_BUILD_START_MPH   = 20.0f;
        private const float VISUALIZATION_FAST_BUILD_FULL_MPH    = 120.0f;
        private const float VISUALIZATION_FAST_BUILD_MIN_SECONDS = 0.18f;
        // How fast an edited value glides to its new position. It covers about 95 percent of the way in 3 of these, about 0.25 seconds
        private const float VISUALIZATION_GLIDE_TIME_CONSTANT = 0.08f;
        // [Shared: Shapes] Shape consts 2 or more visualizers read. Every visualizer size from here down is in meters unless stated otherwise
        // Share of each bounding box edge one corner tick covers. The Percent Submerged ring stops short of these ticks
        private const float VISUALIZATION_BRACKET_FRACTION = 0.25f;
        // How many straight lines draw each full ring: A wheel bound face, the impeller ring, and each keel sphere ring
        private const int VISUALIZATION_RING_SEGMENTS = 32;
        // A wheel this close to the centerline (a bike's, a trike's single wheel) draws its gauge and roll post on both sides
        // Its live X flips between sides with suspension jitter
        private const float VISUALIZATION_CENTERED_WHEEL_EPSILON = 0.05f;
        // Share of a wire box's build each of its 3 steps takes: The bottom square, the vertical edges, then the top square
        private const float VISUALIZATION_WIRE_BOX_STEP_SHARE = 1.0f / 3.0f;
        // [Bounding Box] Build of the bounding box corner ticks
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_BOUNDING_BUILD_SECONDS = 0.30f;
        // [Wheel Bounds] Shape and build steps of the wheel bound rings
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_WHEEL_BOUND_BUILD_SECONDS = 0.50f;
        // How many of a face's ring lines lie between 2 neighboring spokes. A spoke and a tread line sit at every link
        // The modeling tool's own wheel collision display draws the same spacing
        private const int VISUALIZATION_RING_LINES_PER_LINK = 4;
        // Below this length a wheel bound's axis column reads as unset, and that wheel is skipped for the tick
        private const float VISUALIZATION_BOUND_AXIS_EPSILON = 0.01f;
        // Share of the build each of a wheel bound's 3 steps takes: Both faces' spokes, both rings, then the tread lines
        private const float VISUALIZATION_WHEEL_BOUND_STEP_SHARE = 1.0f / 3.0f;
        // Farthest an extra wheel bound can sit from its wheel's hub across the vehicle's X and Y, in radii of that wheel's main bound
        // The game pushes extras out by 0.6 radii at most
        private const float VISUALIZATION_WHEEL_BOUND_EXTRA_PUSH_RADII = 0.7f;
        // [Percent Submerged] Shape and build steps of the waterline ring
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_SUBMERGED_BUILD_SECONDS = 0.50f;
        // Share of the gap between 2 corner brackets each sinking ring segment stops short of each bracket
        private const float VISUALIZATION_SINK_GAP_FRACTION = 0.10f;
        // Share of the build each step of the waterline ring takes: The front bar, the side edges, the rear bar
        private const float VISUALIZATION_WATERLINE_STEP_SHARE = 1.0f / 3.0f;
        // Sink blend above which the waterline keeps its build with every edge growing out from its own middle
        private const float VISUALIZATION_WATERLINE_SINKING_BLEND = 0.5f;
        // [Center Of Mass And Center Of Gravity] Shape and build of the 2 centers' axes, which share every shape const
        // Seconds each center's axes take to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_COM_BUILD_SECONDS               = 0.40f;
        private const float VISUALIZATION_CENTER_OF_GRAVITY_BUILD_SECONDS = 0.40f;
        // How far the center of mass and center of gravity axes reach past the bounds on each side
        private const float VISUALIZATION_AXIS_OVERHANG = 0.5f;
        // Half the length of the tick across each end of a center of mass or center of gravity axis
        private const float VISUALIZATION_AXIS_TICK_HALF_LENGTH = 0.1f;
        // Below this distance the center of gravity matches the center of mass, and only the center of gravity axes draw
        private const float VISUALIZATION_COM_COG_MATCH_EPSILON = 0.01f;
        // Share of the build the axes take. The axis end ticks grow over the rest
        private const float VISUALIZATION_AXIS_BUILD_SHARE = 0.6f;
        // [Anti-Roll Bars] Build of the anti-roll bar lines
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_ANTI_ROLL_BAR_BUILD_SECONDS = 0.50f;
        // Share of the build the bar lines take to draw. The arm at each end grows over the rest
        private const float VISUALIZATION_ANTI_ROLL_BAR_BUILD_SHARE = 0.5f;
        // [Suspension Load] Size and build steps of the suspension load gauges
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_SUSPENSION_LOAD_BUILD_SECONDS = 0.55f;
        // Smallest distance past the bounding box's side plane a suspension load gauge stands, beyond the roll center posts. It grows with the gauge
        private const float VISUALIZATION_SUSPENSION_LOAD_SIDE_OFFSET = 0.25f;
        // Hubs this close along the vehicle share one axle when the suspension load gauges split the weight between axles
        private const float VISUALIZATION_AXLE_GROUP_EPSILON = 0.1f;
        // Shortest suspension load bias line drawn, as a share of the gauge's height. A shorter one would show as a dot on the level line
        private const float VISUALIZATION_SUSPENSION_LOAD_BIAS_MIN_SHARE = 0.02f;
        // Gap between the level line and the start of the bias line, as a share of the gauge's height, so the 2 colors never overlap and mix
        private const float VISUALIZATION_SUSPENSION_LOAD_BIAS_GAP_SHARE = 0.015f;
        // Smallest width and height of a suspension load gauge, centered on its wheel's hub height
        // It scales up from these with the vehicle's size and the camera distance
        private const float VISUALIZATION_GAUGE_WIDTH  = 0.12f;
        private const float VISUALIZATION_GAUGE_HEIGHT = 0.4f;
        // Gauge height as a share of the larger of the bounding box diagonal and the camera distance, never below VISUALIZATION_GAUGE_HEIGHT
        private const float VISUALIZATION_GAUGE_SIZE_FRACTION = 0.05f;
        // Cap on the camera distance a gauge scales by, as a multiple of the bounding box diagonal
        // A chase camera sits at about 1.4 times the diagonal, so this leaves a little room to zoom out
        private const float VISUALIZATION_GAUGE_CAMERA_DISTANCE_MAX_MULTIPLE = 1.6f;
        // Below this length the camera looks almost straight down or up, and a gauge takes its width from the vehicle's right side instead
        private const float VISUALIZATION_GAUGE_FACING_EPSILON = 0.01f;
        // Share of the build each step of a Suspension Load gauge takes
        // The steps are the bottom line, the side poles, the top line, the level line drawing in, then its slide to the value
        private const float VISUALIZATION_GAUGE_STEP_SHARE = 0.2f;
        // [Roll Centers] Placement and build of the roll center posts
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_ROLL_CENTER_BUILD_SECONDS = 0.35f;
        // How far past the bounding box's side plane a roll center post stands
        private const float VISUALIZATION_ROLL_POST_SIDE_OFFSET = 0.0f;
        // Share of the build the roll center posts take to rise. The join bar grows inward from their tops over the rest
        private const float VISUALIZATION_ROLL_POST_BUILD_SHARE = 0.5f;
        // [Water Samples] Build order of the water sample boxes, which also covers a boat's sample box and a seaplane's pontoon samples
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_WATER_SAMPLES_BUILD_SECONDS = 0.50f;
        // Share of the Water Samples build one sample's box takes. The samples start one after another by height over the rest, lowest first
        private const float VISUALIZATION_WATER_SAMPLE_BUILD_WINDOW = 0.4f;
        // Water sample heights closer than this in meters share a level and start building together
        private const float VISUALIZATION_WATER_SAMPLE_LEVEL_EPSILON = 0.01f;
        // [Rudder Offsets] Shape and build of the rudder offset lines
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_RUDDER_OFFSETS_BUILD_SECONDS = 0.20f;
        // Half the length of the bar across the hull at a boat's thrust point, which keeps the 2 rudder offset lines apart
        private const float VISUALIZATION_RUDDER_STUB_HALF_LENGTH = 0.1f;
        // Share of the build the white stub takes to draw. The 2 offset lines grow from its ends over the rest
        private const float VISUALIZATION_RUDDER_STUB_BUILD_SHARE = 0.5f;
        // [Propeller Radius] Build of the propeller radius cross
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_PROP_RADIUS_BUILD_SECONDS = 0.20f;
        // [Keel Spheres] Build of the keel spheres
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_KEEL_SPHERES_BUILD_SECONDS = 0.35f;
        // Share of the build after which the flat ring starts, when the 2 upright rings climbing from the bottom reach the middle height
        private const float VISUALIZATION_KEEL_FLAT_RING_START_SHARE = 0.5f;
        // [Impeller] Size and build of the impeller ring and arrow
        // Seconds this visualizer takes to build in when shown, and to build back out when hidden
        private const float VISUALIZATION_IMPELLER_BUILD_SECONDS = 0.50f;
        // The game's own impeller water test radius, then the impeller arrow's length per unit of ImpellerForceMultiplier and its head stroke length
        // The last const is the share of the build the impeller ring takes before the arrow grows
        private const float GAME_IMPELLER_RADIUS                         = 0.1f;
        private const float VISUALIZATION_IMPELLER_ARROW_METERS_PER_UNIT = 0.5f;
        private const float VISUALIZATION_IMPELLER_ARROW_HEAD_LENGTH     = 0.1f;
        private const float VISUALIZATION_IMPELLER_RING_BUILD_SHARE      = 0.5f;
        // [Crosshair] The Select Vehicle crosshair's line length and thickness, in screen units
        private const float CROSSHAIR_SIZE      = 18.0f;
        private const float CROSSHAIR_THICKNESS = 0.70f;
        // [Wheel Bones] The hub bone table's starting bone id
        // VehicleWheelBoneId's first value, WheelLeftFront
        private const int WHEEL_BONE_ID_FIRST = 11;

        // ====
        // MENU
        // ====
        // --MenuBase.cs--
        // The weapon and turret slot counts the game defines per build, which the API derives from the game's file version
        // The current counts apply from build 1180.2 on
        private const int WEAPON_SLOT_COUNT_CURRENT = 6;
        private const int WEAPON_SLOT_COUNT_OLDER   = 4;
        private const int TURRET_SLOT_COUNT_CURRENT = 6;
        private const int TURRET_SLOT_COUNT_OLDER   = 3;
        // Every menu's baseline layout: The width floor dynamic width never shrinks below, and the banner's height
        private const int MENU_WIDTH      = 433;
        private const float BANNER_HEIGHT = 108.0f;
        // MaxRows' default and accepted range: How many rows a menu lists before it scrolls
        private const int MENU_MAX_ROWS_DEFAULT = 10;
        private const int MENU_MAX_ROWS_MIN     = 5;
        private const int MENU_MAX_ROWS_MAX     = 15;
        // The default banner color: The INI default, the Custom entry's starting color, and the Default preset's row in BannerColorPresetRgb
        private const int BANNER_DEFAULT_RED   = 255;
        private const int BANNER_DEFAULT_GREEN = 141;
        private const int BANNER_DEFAULT_BLUE  = 100;
        // The index of the Custom entry in BannerColorPresetNames, the user-defined color with no row in BannerColorPresetRgb
        private const int BANNER_COLOR_CUSTOM_INDEX = 6;
        // The submenu arrow texture's width and height
        private const float ARROW_SIZE = 30.0f;
        // Nudges the arrow texture in from the item's right and bottom edge so it does not touch the item's own border
        private const float ARROW_MARGIN_X = 5.0f;
        private const float ARROW_MARGIN_Y = 4.0f;
        // --MenuControls.cs--
        // Held-key navigation repeat delays (fast and default), and how long a direction must be held before the fast delay applies
        private const int NAV_FAST_HELD_TIME_MS    = 50;
        private const int NAV_DEFAULT_HELD_TIME_MS = 166;
        private const int NAV_ACCEL_THRESHOLD_MS   = 400;
        // Parent hops checked before a remembered menu counts as unreachable, deeper than any menu chain the script builds
        private const int MENU_REACHABILITY_MAX_DEPTH = 16;
        // --MenuHandlingItems.cs--
        // Real milliseconds per tick the startup warm-up spends building the extra editors' trees and the Search model list, at least one step per tick
        private const int WARM_UP_FRAME_BUDGET_MS = 1;
        // Real milliseconds per tick the startup warm-up spends compiling methods ahead of their first call, at least one method per tick
        // Larger than the build budget, since the compile is long and this only spreads a steady cost over the first seconds, never one long frame
        private const int WARM_UP_PRECOMPILE_FRAME_BUDGET_MS = 2;
        // --MenuSettings.cs--
        // Settings section header font scale
        private const float SEPARATOR_SCALE = 0.40f;
        // Banner luminance (0 to 1) above which the banner text is black, otherwise white
        private const float BANNER_TEXT_LUMINANCE_THRESHOLD = 0.5f;
        // --MenuWidth.cs--
        // Dynamic menu width: MENU_WIDTH is the floor
        // WIDTH_ITEM_PADDING pads a plain row, the banner, and the name bar
        // Slidable items use WIDTH_VALUE_PADDING, and WIDTH_EXTENDED_PADDING adds room for F6-precision floats
        // WIDTH_LERP_FACTOR is the share of the remaining distance to the target width that each tick's lerp covers
        private const float WIDTH_ITEM_PADDING     = 70.0f;
        private const float WIDTH_VALUE_PADDING    = 130.0f;
        private const float WIDTH_EXTENDED_PADDING = 70.0f;
        private const float WIDTH_LERP_FACTOR      = 0.2f;
        // Distance under which the width lerp snaps straight to target instead of approaching it forever
        private const float WIDTH_SNAP_EPSILON = 0.5f;
        // The gap between Preview's Title and AltTitle columns, which it measures directly rather than assuming a flat value width
        private const float WIDTH_PREVIEW_VALUE_GAP = 40.0f;
        // Text scales the 2 measurement texts use for an item's title and its cycler/preview value
        private const float TITLE_MEASURE_SCALE = 0.345f;
        private const float VALUE_MEASURE_SCALE = 0.35f;
        // --MenuWiring.cs--
        // The finest step the shrink reaches, the last decimal the menu displays
        private const double PRECISION_CYCLING_MIN = 0.000001;
        // Each shrink divides the step by this, so the step drops one decimal at a time
        private const double PRECISION_CYCLING_DIVISOR = 10.0;
        // A step or value this close to a size, as a share of it, counts as equal, since repeated division by 10 leaves noise in the last bits
        private const double PRECISION_CYCLING_EQUAL_RATIO = 0.999999;
        // SF_WORKS_UPSIDE_DOWN's bit in the SpecialFlight flags
        private const int SF_WORKS_UPSIDE_DOWN_BIT = 0;
        // The F2/F6 display threshold is the value's magnitude times RELATIVE, floored at MIN and capped at MAX, because float32 precision is relative to magnitude
        // A fixed epsilon would read the narrowing noise of a field cycling at tens or hundreds (BoostMaxSpeed) as real decimals
        // MIN sits at F6's own resolution, so 0.000001 still shows
        private const float DISPLAY_FLOAT_THRESHOLD_MIN      = 0.000001f;
        private const float DISPLAY_FLOAT_THRESHOLD_MAX      = 0.001f;
        private const float DISPLAY_FLOAT_THRESHOLD_RELATIVE = 0.000001f;
        // A value within this share of its own size, plus the absolute amount below, of a 4 decimal number is taken to be that number before a step is added
        // A field stored in another unit (SteeringLock as radians) comes back from memory with conversion noise
        // A step taken from that value keeps the noise and adds more
        private const double STEP_SNAP_RELATIVE_TOLERANCE = 5.0e-7;
        private const double STEP_SNAP_ABSOLUTE_TOLERANCE = 1.0e-6;
        // --ArmedActions.cs--
        // Flash half-period and arm window, in game milliseconds (Game.GameTime), so the window matches the seconds the confirm hint states at any frame rate
        private const int ARMED_FLASH_INTERVAL_MS = 500;
        private const int ARMED_ACTION_EXPIRY_MS  = 5000;
        // How long a result flash lasts, in game milliseconds (Game.GameTime)
        private const int RESULT_FLASH_DURATION_MS = 1500;
        // --Descriptors.cs--
        // A Descriptors.json stamped with a lower version than DESCRIPTORS_VERSION is backed up and replaced with the compiled defaults
        private const int DESCRIPTORS_VERSION = 1;
        // --Preview.cs--
        // Horizontal gap between a panel and the one beside it, Saved Handlings and Preview, and Preview and Diff
        private const float PREVIEW_PANEL_SPACING = 8.0f;
        // --VehicleSearch.cs--
        // The fewest typed characters a vehicle search accepts
        private const int SEARCH_MIN_LENGTH = 2;
        // The most results one typed search lists, and the toast gives the shown and total counts when more match
        // The full list of every model has no cap
        private const int SEARCH_RESULT_LIMIT = 100;

        // ===========
        // PERSISTENCE
        // ===========
        // --FieldTables.cs--
        // Full float precision to avoid accumulating rounding error across angle-conversion round-trips
        private const float RAD2DEG = (float)(180.0 / Math.PI);
        private const float DEG2RAD = (float)(Math.PI / 180.0);
        // Shared unit-conversion factors between each field's Build (save) and Apply site, named once so the two never drift out of sync
        private const float DRAG_COEFF_SCALE         = 10000.0f;
        private const float KMH_TO_MS                = 3.6f;
        private const float DRIVE_MAX_VELOCITY_MULT  = 1.2f;
        private const float BIAS_FRONT_SCALE         = 2.0f;
        private const float SUSPENSION_DAMPING_SCALE = 10.0f;
        // Smallest drag the max speed estimate can solve, since 0 divides by zero and cuts all forward drive on loose surfaces after a model reload
        private const float INITIAL_DRAG_COEFFICIENT_HARD_MIN = 0.01f;
        // Smallest steering lock in degrees, since 0 divides by zero in every system that reads steering as a fraction of the lock
        private const float STEERING_LOCK_HARD_MIN = 0.01f;
        // Smallest magnitude the rocket boost top speed can have on either side of 0, since 0 divides by zero in the boost force
        // A negative value is valid and makes the boost force grow with speed
        private const float BOOST_MAX_SPEED_MINIMUM_MAGNITUDE = 0.01f;
        // Smallest taxi turning boost peak speed, since 0 divides by zero in the on-ground yaw boost
        private const float ON_GROUND_YAW_BOOST_SPEED_PEAK_HARD_MIN = 0.01f;
        // Converts meters per second to miles per hour for BoostMaxSpeed's description prefix
        private const float MS_TO_MPH = 2.236936f;
        // Converts kilometers per hour to miles per hour for InitialDriveMaxFlatVelocity's description prefix
        private const float KMH_TO_MPH = 0.621371f;
        // The game snaps a file drive bias below the RWD value to pure rear drive and above the FWD value to pure front drive
        private const float DRIVE_BIAS_RWD_SNAP = 0.1f;
        private const float DRIVE_BIAS_FWD_SNAP = 0.9f;
        // InitialDriveGears' highest value. The game's own ratios stop falling steadily from 7 gears, so the cap is 6 unless the gear ratio patch is active
        // The patch keeps every count in order, so with it the cap is the 10 forward gears the transmission's ratio array holds
        private const int INITIAL_DRIVE_GEARS_MAX            = 6;
        private const int INITIAL_DRIVE_GEARS_MAX_WITH_PATCH = 10;
        // --HandlingMetaXml.cs--
        // The parser schema's init value for the base-level fWeaponDamageScaledToVehHealthMult, used when an export-only Weapon category has none to carry
        private const float WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT_DEFAULT = 0.5f;
        // A saved float within this share of its own size, plus the absolute amount below, of a 4 decimal number saves as that number
        // Wider than the cyclers' snap, since a unit-converted field picks up several float steps of drift across repeated saves and applies
        private const double SAVE_SNAP_RELATIVE_TOLERANCE = 2.0e-6;
        private const double SAVE_SNAP_ABSOLUTE_TOLERANCE = 1.0e-6;
        // --HandlingMetaXmlMapper.cs--
        // Every SubHandlingData block in the game's own handling.meta holds exactly 3 items, with NULL items filling unused ones
        private const int SUBHANDLING_ITEM_COUNT = 3;
        // --Presets.cs--
        // Shared cap for every WindowTitle.EnterMessage20-backed text prompt (saved handling names, hotkey and cheat string entry)
        // The RGB color prompt uses its own shorter cap instead
        private const int INPUT_MAX_LENGTH = 20;
        // An "R, G, B" or "#RRGGBB" string never needs more than this many characters
        private const int RGB_INPUT_MAX_LENGTH = 15;
        // Digit count of the collision suffix appended to an export file name (NAME_01)
        private const int EXPORT_SUFFIX_DIGITS = 2;
        // --ReloadState.cs--
        // A section starts on a line holding its name between these characters. No data line of any section starts with the opening one
        private const char RELOAD_STATE_SECTION_OPEN  = '[';
        private const char RELOAD_STATE_SECTION_CLOSE = ']';
        // --Undo.cs--
        // Number of fields a persisted Undo All baseline line carries: The handling name hash, the Baseline tag, and the snapshot
        private const int UNDO_BASELINE_LINE_FIELD_COUNT = 3;
        // The index of a structural edit family's first field in a written line, after the handling hash, the stack tag, the group id, and the kind
        private const int STRUCTURAL_EDIT_PAYLOAD_START = 4;

        // ====
        // CORE
        // ====
        // --Input.cs--
        // The high bit GetAsyncKeyState sets in its return value when the polled key is currently held down
        private const int KEY_PRESSED_MASK = 0x8000;
        // Keypresses a failing key handler stays silent for after it logs
        // A held key's typematic repeat still delivers many KeyDown events per second, so this keeps a broken handler from spamming the log on every repeat
        // It still surfaces again well within a single held-key press
        private const int KEYDOWN_ERROR_COOLDOWN = 20;
        // Game milliseconds a held Undo or Redo waits before its first repeat, so a tap that lingers a few frames never fires twice
        // Every later repeat comes at NAV_DEFAULT_HELD_TIME_MS
        private const int HELD_HOTKEY_FIRST_REPEAT_DELAY_MS = 400;
        // --Log.cs--
        // LoggingMode's default and accepted range, where 0 writes only warnings and errors and 1 writes every line
        private const int LOGGING_MODE_DEFAULT = 1;
        private const int LOGGING_MODE_MIN     = 0;
        private const int LOGGING_MODE_MAX     = 3;
        // Ticks between automatic FlushLog() calls, keeping logBuffer's unwritten tail bounded between writes
        private const int LOG_FLUSH_INTERVAL = 100;
        // Longest a truncated framework exception message may be inside a log line
        private const int EXCEPTION_MESSAGE_MAX_LENGTH = 90;
        // --Notifications.cs--
        // ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME accepts at most 99 characters, so a longer message is cut to fit, ending in a 3-character ellipsis
        private const int NOTIF_MAX_CHARS    = 99;
        private const int NOTIF_ELLIPSIS_LEN = 3;
        // Display durations and the default PostNotificationOnce cooldown, in game milliseconds (Game.GameTime), so they hold at any frame rate
        private const int NOTIF_DISPLAY_MS       = 6000;
        private const int NOTIF_DISPLAY_WARN_MS  = 8000;
        private const int NOTIF_DISPLAY_ERROR_MS = 10000;
        private const int NOTIF_COOLDOWN_MS      = 3000;
        // notifCooldowns size above which PostNotificationOnce drops every entry whose cooldown has already ended
        private const int NOTIF_COOLDOWN_PRUNE_THRESHOLD = 64;
        // --FileEvents.cs--
        // Game milliseconds a watched file must go without a new change event before the script reads it, since an editor saves in several writes
        private const int FILE_WATCH_DEBOUNCE_MS = 300;
        // --Tick.cs--
        // Game milliseconds between polls for external edits to the INI, the saved handlings, and the ModelMetaData file
        // Used only for a file whose watcher could not start
        private const int OWNED_FILE_EXTERNAL_EDIT_POLL_INTERVAL_MS = 5000;
        // Game milliseconds between RefreshDescriptors() polls, used only when the descriptors watcher could not start
        private const int DESCRIPTORS_POLL_INTERVAL_MS = 5000;
        // Fallback cooldown for OnTick's own outer catch, which takes anything a stage's own exception boundary did not catch
        // It is also the suppression window of each tick stage and menu handler
        private const int TICK_ERROR_COOLDOWN = 300;
        // Ticks between truck+trailer auto-mount checks while seated. A genuine seat change checks immediately regardless
        private const int TRAILER_CHECK_INTERVAL = 30;
        // A live and asserted value pair counts as drifted only when it differs by more than this
        // It absorbs float round-trip noise (unit conversions, derived-value recomputation) without misfiring on jitter that was never a real external reset
        private const float DRIFT_EPSILON = 0.0001f;
    }
}
