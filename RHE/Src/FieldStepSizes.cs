using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Left/Right step per keypress for every editable float field, one const per field, grouped by handling category
        // A vector field's 3 components share their field's const, and an export-only row steps by the same const as its live row
        // The defaults cover any field wired without its own const, every int field, and every const that references them
        private const float DEFAULT_FLOAT_STEP = 0.01f;
        private const int DEFAULT_INT_STEP     = 1;

        // [Base] InitialDriveMaxFlatVelocity steps by its CVT const while HF_CVT is set
        private const float STEP_BASE_MASS                                = 100.0f;
        private const float STEP_BASE_INITIAL_DRAG_COEFFICIENT            = 0.05f;
        private const float STEP_BASE_DOWNFORCE_MODIFIER                  = 0.05f;
        private const float STEP_BASE_POP_UP_LIGHT_ROTATION               = 1.0f;
        private const float STEP_BASE_PERCENT_SUBMERGED                   = 1.0f;
        private const float STEP_BASE_CENTER_OF_MASS_OFFSET               = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_INERTIA_MULTIPLIER                  = 0.05f;
        private const float STEP_BASE_DRIVE_BIAS_FRONT                    = 0.05f;
        private const float STEP_BASE_INITIAL_DRIVE_FORCE                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_DRIVE_INERTIA                       = 0.05f;
        private const float STEP_BASE_CLUTCH_CHANGE_RATE_UP_SHIFT         = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_CLUTCH_CHANGE_RATE_DOWN_SHIFT       = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_INITIAL_DRIVE_MAX_FLAT_VELOCITY     = 1.0f;
        private const float STEP_BASE_INITIAL_DRIVE_MAX_FLAT_VELOCITY_CVT = 0.10f;
        private const float STEP_BASE_BRAKE_FORCE                         = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_BRAKE_BIAS_FRONT                    = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_HANDBRAKE_FORCE                     = 0.10f;
        private const float STEP_BASE_STEERING_LOCK                       = 0.10f;
        private const float STEP_BASE_TRACTION_CURVE_MAX                  = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_TRACTION_CURVE_MIN                  = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_TRACTION_CURVE_LATERAL              = 0.10f;
        private const float STEP_BASE_TRACTION_SPRING_DELTA_MAX           = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_LOW_SPEED_TRACTION_LOSS_MULTIPLIER  = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_CAMBER_STIFFNESS                    = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_TRACTION_BIAS_FRONT                 = 0.005f;
        private const float STEP_BASE_TRACTION_LOSS_MULTIPLIER            = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_FORCE                    = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_COMPRESSION_DAMPING      = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_REBOUND_DAMPING          = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_UPPER_LIMIT              = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_LOWER_LIMIT              = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_RAISE                    = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SUSPENSION_BIAS_FRONT               = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_ANTI_ROLL_BAR_FORCE                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_ANTI_ROLL_BAR_BIAS_FRONT            = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_ROLL_CENTER_HEIGHT_FRONT            = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_ROLL_CENTER_HEIGHT_REAR             = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_COLLISION_DAMAGE_MULTIPLIER         = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_WEAPON_DAMAGE_MULTIPLIER            = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_DEFORMATION_DAMAGE_MULTIPLIER       = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_ENGINE_DAMAGE_MULTIPLIER            = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_PETROL_TANK_VOLUME                  = 1.0f;
        private const float STEP_BASE_OIL_VOLUME                          = 0.10f;
        private const float STEP_BASE_SEAT_OFFSET_DISTANCE_X              = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SEAT_OFFSET_DISTANCE_Y              = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_SEAT_OFFSET_DISTANCE_Z              = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_ROCKET_BOOST_CAPACITY               = DEFAULT_FLOAT_STEP;
        private const float STEP_BASE_BOOST_MAX_SPEED                     = 1.0f;

        // [Car] AdvancedData Value is shared by the live, pending, and export-only entries
        private const float STEP_CAR_BACK_END_POP_UP_CAR_IMPULSE_MULT      = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_BACK_END_POP_UP_BUILDING_IMPULSE_MULT = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_BACK_END_POP_UP_MAX_DELTA_SPEED       = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_TOE_FRONT                             = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_TOE_REAR                              = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_CAMBER_FRONT                          = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_CAMBER_REAR                           = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_CASTOR                                = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_ENGINE_RESISTANCE                     = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_MAX_DRIVE_BIAS_TRANSFER               = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_JUMP_FORCE_SCALE                      = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_INCREASED_RAMMING_FORCE_SCALE         = DEFAULT_FLOAT_STEP;
        private const float STEP_CAR_ADVANCED_DATA_VALUE                   = DEFAULT_FLOAT_STEP;

        // [Bike]
        private const float STEP_BIKE_LEAN_FORWARD_COM_MULTIPLIER           = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_LEAN_FORWARD_FORCE_MULTIPLIER         = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_LEAN_BACK_COM_MULTIPLIER              = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_LEAN_BACK_FORCE_MULTIPLIER            = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_MAX_BANK_ANGLE                        = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_FULL_ANIM_ANGLE                       = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_DES_LEAN_RETURN_FRACTION              = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_STICK_LEAN_MULTIPLIER                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_BRAKING_STABILITY_MULTIPLIER          = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_IN_AIR_STEER_MULTIPLIER               = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_WHEELIE_BALANCE_POINT                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_STOPPIE_BALANCE_POINT                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_WHEELIE_STEER_MULTIPLIER              = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_REAR_BALANCE_MULTIPLIER               = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_FRONT_BALANCE_MULTIPLIER              = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_GROUND_SIDE_FRICTION_MULTIPLIER       = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_WHEEL_GROUND_SIDE_FRICTION_MULTIPLIER = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_ON_STAND_LEAN_ANGLE                   = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_ON_STAND_STEER_ANGLE                  = DEFAULT_FLOAT_STEP;
        private const float STEP_BIKE_JUMP_FORCE                            = DEFAULT_FLOAT_STEP;

        // [Flying] Shared with Vertical Flying
        private const float STEP_FLYING_THRUST                                 = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_THRUST_FALL_OFF                        = 0.000001f;
        private const float STEP_FLYING_THRUST_VECTORING                       = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_INITIAL_THRUST                         = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_INITIAL_THRUST_FALL_OFF                = 0.000001f;
        private const float STEP_FLYING_YAW_MULTIPLIER                         = 0.000001f;
        private const float STEP_FLYING_YAW_STABILIZE                          = 0.000001f;
        private const float STEP_FLYING_SIDE_SLIP_MULTIPLIER                   = 0.001f;
        private const float STEP_FLYING_INITIAL_YAW_MULTIPLIER                 = 0.000001f;
        private const float STEP_FLYING_ROLL_MULTIPLIER                        = 0.000001f;
        private const float STEP_FLYING_ROLL_STABILIZE                         = 0.000001f;
        private const float STEP_FLYING_INITIAL_ROLL_MULTIPLIER                = 0.000001f;
        private const float STEP_FLYING_PITCH_MULTIPLIER                       = 0.000001f;
        private const float STEP_FLYING_PITCH_STABILIZE                        = 0.000001f;
        private const float STEP_FLYING_INITIAL_PITCH_MULTIPLIER               = 0.000001f;
        private const float STEP_FLYING_FORM_LIFT_MULTIPLIER                   = 0.000001f;
        private const float STEP_FLYING_ATTACK_LIFT_MULTIPLIER                 = 0.000001f;
        private const float STEP_FLYING_ATTACK_DIVE_MULTIPLIER                 = 0.000001f;
        private const float STEP_FLYING_GEAR_DOWN_DRAG_V                       = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_GEAR_DOWN_LIFT_MULTIPLIER              = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_WIND_MULTIPLIER                        = 0.001f;
        private const float STEP_FLYING_MOVE_RESISTANCE                        = 0.001f;
        private const float STEP_FLYING_TURN_RESISTANCE                        = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_SPEED_RESISTANCE                       = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_GEAR_DOOR_FRONT_OPEN                   = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_GEAR_DOOR_REAR_OPEN                    = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_GEAR_DOOR_REAR_OPEN2                   = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_GEAR_DOOR_REAR_M_OPEN                  = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_TURBULENCE_MAGNITUDE_MAX               = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_TURBULENCE_FORCE_MULTIPLIER            = 0.000001f;
        private const float STEP_FLYING_TURBULENCE_ROLL_TORQUE_MULTIPLIER      = 0.001f;
        private const float STEP_FLYING_TURBULENCE_PITCH_TORQUE_MULTIPLIER     = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_BODY_DAMAGE_CONTROL_EFFECT_MULTIPLIER  = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_INPUT_SENSITIVITY_FOR_DIFFICULTY       = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_ON_GROUND_YAW_BOOST_SPEED_PEAK         = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_ON_GROUND_YAW_BOOST_SPEED_CAP          = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_ENGINE_OFF_GLIDE_MULTIPLIER            = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_AFTERBURNER_EFFECT_RADIUS              = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_AFTERBURNER_EFFECT_DISTANCE            = 0.10f;
        private const float STEP_FLYING_AFTERBURNER_EFFECT_FORCE_MULTIPLIER    = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_SUBMERGE_LEVEL_TO_PULL_HELI_UNDERWATER = DEFAULT_FLOAT_STEP;
        private const float STEP_FLYING_EXTRA_LIFT_WITH_ROLL                   = DEFAULT_FLOAT_STEP;

        // [Boat]
        private const float STEP_BOAT_BOX_FRONT_MULTIPLIER                  = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_BOX_REAR_MULTIPLIER                   = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_BOX_SIDE_MULTIPLIER                   = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_SAMPLE_TOP                            = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_SAMPLE_BOTTOM                         = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_SAMPLE_BOTTOM_TEST_CORRECTION         = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_AQUAPLANE_FORCE                       = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_AQUAPLANE_PUSH_WATER_MULTIPLIER       = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_AQUAPLANE_PUSH_WATER_CAP              = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_AQUAPLANE_PUSH_WATER_APPLY            = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_RUDDER_FORCE                          = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_RUDDER_OFFSET_SUBMERGE                = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_RUDDER_OFFSET_FORCE                   = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_RUDDER_OFFSET_FORCE_Z_MULTIPLIER      = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_WAVE_AUDIO_MULTIPLIER                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_MOVE_RESISTANCE                       = 1.0f;
        private const float STEP_BOAT_TURN_RESISTANCE                       = 1.0f;
        private const float STEP_BOAT_LOOK_LR_CAM_HEIGHT                    = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_DRAG_COEFFICIENT                      = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_KEEL_SPHERE_SIZE                      = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_PROP_RADIUS                           = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_LOW_LOD_ANG_OFFSET                    = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_LOW_LOD_DRAUGHT_OFFSET                = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_IMPELLER_OFFSET                       = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_IMPELLER_FORCE_MULTIPLIER             = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_DINGHY_SPHERE_BUOY_CONSTANT           = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_PROW_RAISE_MULTIPLIER                 = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_DEEP_WATER_SAMPLE_BUOYANCY_MULTIPLIER = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_TRANSMISSION_MULTIPLIER               = DEFAULT_FLOAT_STEP;
        private const float STEP_BOAT_TRACTION_MULTIPLIER                   = DEFAULT_FLOAT_STEP;

        // [SeaPlane]
        private const float STEP_SEA_PLANE_PONTOON_BUOY_CONSTANT               = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_SAMPLE_SIZE_FRONT           = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_SAMPLE_SIZE_MIDDLE          = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_SAMPLE_SIZE_REAR            = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_LENGTH_FRACTION_FOR_SAMPLES = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_DRAG_COEFFICIENT            = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_VERTICAL_DAMPING_UP         = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_PONTOON_VERTICAL_DAMPING_DOWN       = DEFAULT_FLOAT_STEP;
        private const float STEP_SEA_PLANE_KEEL_SPHERE_SIZE                    = DEFAULT_FLOAT_STEP;

        // [Submarine]
        private const float STEP_SUBMARINE_TURN_RESISTANCE    = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_MOVE_RESISTANCE_XY = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_MOVE_RESISTANCE_Z  = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_PITCH_MULTIPLIER   = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_PITCH_ANGLE        = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_YAW_MULTIPLIER     = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_DIVE_SPEED         = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_ROLL_MULTIPLIER    = DEFAULT_FLOAT_STEP;
        private const float STEP_SUBMARINE_ROLL_STABILIZE     = DEFAULT_FLOAT_STEP;

        // [Trailer]
        private const float STEP_TRAILER_ATTACH_LIMIT_PITCH             = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_ATTACH_LIMIT_ROLL              = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_ATTACH_LIMIT_YAW               = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_UPRIGHT_SPRING_CONSTANT        = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_UPRIGHT_DAMPING_CONSTANT       = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_ATTACHED_MAX_DISTANCE          = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_ATTACHED_MAX_PENETRATION       = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_ATTACH_RAISE_Z                 = DEFAULT_FLOAT_STEP;
        private const float STEP_TRAILER_POSITION_CONSTRAINT_MASS_RATIO = DEFAULT_FLOAT_STEP;

        // [SpecialFlight]
        private const float STEP_SPECIAL_FLIGHT_ANGULAR_DAMPING                  = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_ANGULAR_DAMPING_MIN              = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_LINEAR_DAMPING                   = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_LINEAR_DAMPING_MIN               = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_LIFT_COEFFICIENT                 = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_CRITICAL_LIFT_ANGLE              = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_INITIAL_LIFT_ANGLE               = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MAX_LIFT_ANGLE                   = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_DRAG_COEFFICIENT                 = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_BRAKING_DRAG                     = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MAX_LIFT_VELOCITY                = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MIN_LIFT_VELOCITY                = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_ROLL_TORQUE_SCALE                = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MAX_TORQUE_VELOCITY              = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MIN_TORQUE_VELOCITY              = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_YAW_TORQUE_SCALE                 = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_SELF_LEVELING_PITCH_TORQUE_SCALE = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MAX_PITCH_TORQUE                 = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MAX_STEERING_ROLL_TORQUE         = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_PITCH_TORQUE_SCALE               = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_STEERING_TORQUE_SCALE            = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_MAX_THRUST                       = 1.0f;
        private const float STEP_SPECIAL_FLIGHT_TRANSITION_DURATION              = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_HOVER_VELOCITY_SCALE             = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_STABILITY_ASSIST                 = 1.0f;
        private const float STEP_SPECIAL_FLIGHT_MIN_SPEED_FOR_THRUST_FALLOFF     = DEFAULT_FLOAT_STEP;
        private const float STEP_SPECIAL_FLIGHT_BRAKING_THRUST_SCALE             = DEFAULT_FLOAT_STEP;

        // [Weapon] The scalars, the base-level WeaponDamageScaledToVehHealthMult, then each slot's turret and pitch limit rows
        private const float STEP_WEAPON_UV_ANIMATION_MULT                = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_MISC_GADGET_VAR                  = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_WHEEL_IMPACT_OFFSET              = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_TURRET_SPEED                     = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_TURRET_PITCH_MIN                 = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_TURRET_PITCH_MAX                 = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_TURRET_CAM_PITCH_MIN             = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_TURRET_CAM_PITCH_MAX             = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_BULLET_VELOCITY_FOR_GRAVITY      = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_TURRET_PITCH_FORWARD_MIN         = DEFAULT_FLOAT_STEP;
        private const float STEP_WEAPON_FORWARD_ANGLE_MIN                = 1.0f;
        private const float STEP_WEAPON_FORWARD_ANGLE_MAX                = 1.0f;
        private const float STEP_WEAPON_BACKWARD_ANGLE_MIN               = 1.0f;
        private const float STEP_WEAPON_BACKWARD_ANGLE_MID               = 1.0f;
        private const float STEP_WEAPON_BACKWARD_ANGLE_MAX               = 1.0f;
        private const float STEP_WEAPON_BACKWARD_FORCED_PITCH            = DEFAULT_FLOAT_STEP;
    }
}
