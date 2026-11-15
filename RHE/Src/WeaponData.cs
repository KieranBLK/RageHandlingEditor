using System;
using System.Collections.Generic;
using System.Globalization;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private class WeaponHashEntry
        {
            public string DisplayName;
            public uint Hash;
            public string NativeName;
            public WeaponHashEntry(string displayName, string nativeName) { DisplayName = displayName; NativeName = nativeName; Hash = GTA.StringHash.AtStringHash(nativeName); }
        }

        // Ordered alphabetically by DisplayName, ignoring case, which is the order the cycler steps through, except the leading None sentinel
        // Hash is always GTA.StringHash.AtStringHash(NativeName), so no entry needs a numeric hash of its own
        // None is the one exception: Its Hash is the raw 0 the game uses for no weapon
        // NativeName is the real handling.meta weapon-hash string every saved handling writes, while the cycler, Preview, and undo text use DisplayName
        private static readonly WeaponHashEntry[] WeaponHashTable =
        {
            new WeaponHashEntry("None", "None") { Hash = 0 },
            new WeaponHashEntry("AkulaBarrage", "VEHICLE_WEAPON_AKULA_BARRAGE"),
            new WeaponHashEntry("AkulaMinigun", "VEHICLE_WEAPON_AKULA_MINIGUN"),
            new WeaponHashEntry("AkulaMissile", "VEHICLE_WEAPON_AKULA_MISSILE"),
            new WeaponHashEntry("AkulaTurretDual", "VEHICLE_WEAPON_AKULA_TURRET_DUAL"),
            new WeaponHashEntry("AkulaTurretSingle", "VEHICLE_WEAPON_AKULA_TURRET_SINGLE"),
            new WeaponHashEntry("Annihilator2Barrage", "VEHICLE_WEAPON_ANNIHILATOR2_BARRAGE"),
            new WeaponHashEntry("Annihilator2MG", "VEHICLE_WEAPON_ANNIHILATOR2_MINI"),
            new WeaponHashEntry("Annihilator2Missile", "VEHICLE_WEAPON_ANNIHILATOR2_MISSILE"),
            new WeaponHashEntry("ApcCannon", "VEHICLE_WEAPON_APC_CANNON"),
            new WeaponHashEntry("ApcMinigun", "VEHICLE_WEAPON_APC_MG"),
            new WeaponHashEntry("ApcMissile", "VEHICLE_WEAPON_APC_MISSILE"),
            new WeaponHashEntry("ArdentMinigun", "VEHICLE_WEAPON_ARDENT_MG"),
            new WeaponHashEntry("Avenger3MG", "VEHICLE_WEAPON_AVENGER3_MINIGUN"),
            new WeaponHashEntry("Avenger3Missile", "VEHICLE_WEAPON_AVENGER3_MISSILE"),
            new WeaponHashEntry("AvengerCannon", "VEHICLE_WEAPON_AVENGER_CANNON"),
            new WeaponHashEntry("BarrageRearGrenadeLauncher", "VEHICLE_WEAPON_BARRAGE_REAR_GL"),
            new WeaponHashEntry("BarrageRearMG", "VEHICLE_WEAPON_BARRAGE_REAR_MG"),
            new WeaponHashEntry("BarrageRearMinigun", "VEHICLE_WEAPON_BARRAGE_REAR_MINIGUN"),
            new WeaponHashEntry("BarrageTopMG", "VEHICLE_WEAPON_BARRAGE_TOP_MG"),
            new WeaponHashEntry("BarrageTopMinigun", "VEHICLE_WEAPON_BARRAGE_TOP_MINIGUN"),
            new WeaponHashEntry("BombushkaCannon", "VEHICLE_WEAPON_BOMBUSHKA_CANNON"),
            new WeaponHashEntry("BombushkaDualMinigun", "VEHICLE_WEAPON_BOMBUSHKA_DUALMG"),
            new WeaponHashEntry("Bruiser250CalLaser", "VEHICLE_WEAPON_BRUISER2_50CAL_LASER"),
            new WeaponHashEntry("Bruiser50Cal", "VEHICLE_WEAPON_BRUISER_50CAL"),
            new WeaponHashEntry("Brutus50Cal", "VEHICLE_WEAPON_BRUTUS_50CAL"),
            new WeaponHashEntry("CannonBlazer", "VEHICLE_WEAPON_CANNON_BLAZER"),
            new WeaponHashEntry("CaracaraMG", "VEHICLE_WEAPON_CARACARA_MG"),
            new WeaponHashEntry("CaracaraMinigun", "VEHICLE_WEAPON_CARACARA_MINIGUN"),
            new WeaponHashEntry("ChernoMissile", "VEHICLE_WEAPON_CHERNO_MISSILE"),
            new WeaponHashEntry("CometMinigun", "VEHICLE_WEAPON_COMET_MG"),
            new WeaponHashEntry("Conada2MG", "VEHICLE_WEAPON_CONADA2_MINIGUN"),
            new WeaponHashEntry("Conada2Missile", "VEHICLE_WEAPON_CONADA2_MISSILE"),
            new WeaponHashEntry("Deathbike2MinigunLaser", "VEHICLE_WEAPON_DEATHBIKE2_MINIGUN_LASER"),
            new WeaponHashEntry("DeathbikeDualMinigun", "VEHICLE_WEAPON_DEATHBIKE_DUALMINIGUN"),
            new WeaponHashEntry("DeluxoMinigun", "VEHICLE_WEAPON_DELUXO_MG"),
            new WeaponHashEntry("DeluxoMissile", "VEHICLE_WEAPON_DELUXO_MISSILE"),
            new WeaponHashEntry("DinghyFive50Cal", "VEHICLE_WEAPON_TURRET_DINGHY5_50CAL"),
            new WeaponHashEntry("DogfighterMinigun", "VEHICLE_WEAPON_DOGFIGHTER_MG"),
            new WeaponHashEntry("DogfighterMissile", "VEHICLE_WEAPON_DOGFIGHTER_MISSILE"),
            new WeaponHashEntry("Dominator450Cal", "VEHICLE_WEAPON_DOMINATOR4_50CAL"),
            new WeaponHashEntry("Dominator550CalLaser", "VEHICLE_WEAPON_DOMINATOR5_50CAL_LASER"),
            new WeaponHashEntry("Dune", "VEHICLE_WEAPON_DUNE"),
            new WeaponHashEntry("DuneGrenadeLauncher", "VEHICLE_WEAPON_DUNE_GRENADELAUNCHER"),
            new WeaponHashEntry("DuneMG", "VEHICLE_WEAPON_DUNE_MG"),
            new WeaponHashEntry("DuneMinigun", "VEHICLE_WEAPON_DUNE_MINIGUN"),
            new WeaponHashEntry("EnemyLaser", "VEHICLE_WEAPON_ENEMY_LASER"),
            new WeaponHashEntry("Flamethrower", "VEHICLE_WEAPON_FLAMETHROWER"),
            new WeaponHashEntry("FlamethrowerSciFi", "VEHICLE_WEAPON_FLAMETHROWER_SCIFI"),
            new WeaponHashEntry("HackerMissile", "VEHICLE_WEAPON_HACKER_MISSILE"),
            new WeaponHashEntry("HackerMissileHoming", "VEHICLE_WEAPON_HACKER_MISSILE_HOMING"),
            new WeaponHashEntry("HalftrackDualMinigun", "VEHICLE_WEAPON_HALFTRACK_DUALMG"),
            new WeaponHashEntry("HalftrackQuadMinigun", "VEHICLE_WEAPON_HALFTRACK_QUADMG"),
            new WeaponHashEntry("HavokMinigun", "VEHICLE_WEAPON_HAVOK_MINIGUN"),
            new WeaponHashEntry("HunterBarrage", "VEHICLE_WEAPON_HUNTER_BARRAGE"),
            new WeaponHashEntry("HunterCannon", "VEHICLE_WEAPON_HUNTER_CANNON"),
            new WeaponHashEntry("HunterMinigun", "VEHICLE_WEAPON_HUNTER_MG"),
            new WeaponHashEntry("HunterMissile", "VEHICLE_WEAPON_HUNTER_MISSILE"),
            new WeaponHashEntry("Impaler250Cal", "VEHICLE_WEAPON_IMPALER2_50CAL"),
            new WeaponHashEntry("Impaler350CalLaser", "VEHICLE_WEAPON_IMPALER3_50CAL_LASER"),
            new WeaponHashEntry("Imperator250CalLaser", "VEHICLE_WEAPON_IMPERATOR2_50CAL_LASER"),
            new WeaponHashEntry("Imperator50Cal", "VEHICLE_WEAPON_IMPERATOR_50CAL"),
            new WeaponHashEntry("Insurgent", "VEHICLE_WEAPON_TURRET_INSURGENT"),
            new WeaponHashEntry("InsurgentMinigun", "VEHICLE_WEAPON_INSURGENT_MINIGUN"),
            new WeaponHashEntry("Issi450Cal", "VEHICLE_WEAPON_ISSI4_50CAL"),
            new WeaponHashEntry("Issi550CalLaser", "VEHICLE_WEAPON_ISSI5_50CAL_LASER"),
            new WeaponHashEntry("Jb700Minigun", "VEHICLE_WEAPON_JB700_MG"),
            new WeaponHashEntry("KhanjaliCannon", "VEHICLE_WEAPON_KHANJALI_CANNON"),
            new WeaponHashEntry("KhanjaliCannonHeavy", "VEHICLE_WEAPON_KHANJALI_CANNON_HEAVY"),
            new WeaponHashEntry("KhanjaliGrenadeLauncher", "VEHICLE_WEAPON_KHANJALI_GL"),
            new WeaponHashEntry("KhanjaliMinigun", "VEHICLE_WEAPON_KHANJALI_MG"),
            new WeaponHashEntry("KosatkaTorpedo", "VEHICLE_WEAPON_KOSATKA_TORPEDO"),
            new WeaponHashEntry("MenacerMinigun", "VEHICLE_WEAPON_MENACER_MG"),
            new WeaponHashEntry("MicrolightMinigun", "VEHICLE_WEAPON_MICROLIGHT_MG"),
            new WeaponHashEntry("MobileOpsCannon", "VEHICLE_WEAPON_MOBILEOPS_CANNON"),
            new WeaponHashEntry("MogulDualNose", "VEHICLE_WEAPON_MOGUL_DUALNOSE"),
            new WeaponHashEntry("MogulDualTurret", "VEHICLE_WEAPON_MOGUL_DUALTURRET"),
            new WeaponHashEntry("MogulNose", "VEHICLE_WEAPON_MOGUL_NOSE"),
            new WeaponHashEntry("MogulTurret", "VEHICLE_WEAPON_MOGUL_TURRET"),
            new WeaponHashEntry("Monster3Glkin", "VEHICLE_WEAPON_MONSTER3_GLKIN"),
            new WeaponHashEntry("MortarKinetic", "VEHICLE_WEAPON_MORTAR_KINETIC"),
            new WeaponHashEntry("Mule4Minigun", "VEHICLE_WEAPON_MULE4_MG"),
            new WeaponHashEntry("Mule4Missile", "VEHICLE_WEAPON_MULE4_MISSILE"),
            new WeaponHashEntry("Mule4TurretGrenadeLauncher", "VEHICLE_WEAPON_MULE4_TURRET_GL"),
            new WeaponHashEntry("NightsharkMinigun", "VEHICLE_WEAPON_NIGHTSHARK_MG"),
            new WeaponHashEntry("Oppressor2Cannon", "VEHICLE_WEAPON_OPPRESSOR2_CANNON"),
            new WeaponHashEntry("Oppressor2Minigun", "VEHICLE_WEAPON_OPPRESSOR2_MG"),
            new WeaponHashEntry("Oppressor2Missile", "VEHICLE_WEAPON_OPPRESSOR2_MISSILE"),
            new WeaponHashEntry("OppressorMinigun", "VEHICLE_WEAPON_OPPRESSOR_MG"),
            new WeaponHashEntry("OppressorMissile", "VEHICLE_WEAPON_OPPRESSOR_MISSILE"),
            new WeaponHashEntry("Paragon2Minigun", "VEHICLE_WEAPON_PARAGON2_MG"),
            new WeaponHashEntry("PatrolBoat50Cal", "VEHICLE_WEAPON_TURRET_PATROLBOAT_50CAL"),
            new WeaponHashEntry("PatrolBoatDualMG", "VEHICLE_WEAPON_PATROLBOAT_DUALMG"),
            new WeaponHashEntry("PlaneRocket", "VEHICLE_WEAPON_PLANE_ROCKET"),
            new WeaponHashEntry("PlayerBullet", "VEHICLE_WEAPON_PLAYER_BULLET"),
            new WeaponHashEntry("PlayerBuzzard", "VEHICLE_WEAPON_PLAYER_BUZZARD"),
            new WeaponHashEntry("PlayerHunter", "VEHICLE_WEAPON_PLAYER_HUNTER"),
            new WeaponHashEntry("PlayerLaser", "VEHICLE_WEAPON_PLAYER_LASER"),
            new WeaponHashEntry("PlayerLazer", "VEHICLE_WEAPON_PLAYER_LAZER"),
            new WeaponHashEntry("PlayerSavage", "VEHICLE_WEAPON_PLAYER_SAVAGE"),
            new WeaponHashEntry("Pounder2Barrage", "VEHICLE_WEAPON_POUNDER2_BARRAGE"),
            new WeaponHashEntry("Pounder2GrenadeLauncher", "VEHICLE_WEAPON_POUNDER2_GL"),
            new WeaponHashEntry("Pounder2Mini", "VEHICLE_WEAPON_POUNDER2_MINI"),
            new WeaponHashEntry("Pounder2Missile", "VEHICLE_WEAPON_POUNDER2_MISSILE"),
            new WeaponHashEntry("Radar", "VEHICLE_WEAPON_RADAR"),
            new WeaponHashEntry("RaijuCannons", "VEHICLE_WEAPON_RAIJU_CANNONS"),
            new WeaponHashEntry("RaijuMissiles", "VEHICLE_WEAPON_RAIJU_MISSILES"),
            new WeaponHashEntry("RCTankFlame", "VEHICLE_WEAPON_RCTANK_FLAME"),
            new WeaponHashEntry("RCTankGun", "VEHICLE_WEAPON_RCTANK_GUN"),
            new WeaponHashEntry("RCTankLazer", "VEHICLE_WEAPON_RCTANK_LAZER"),
            new WeaponHashEntry("RCTankRocket", "VEHICLE_WEAPON_RCTANK_ROCKET"),
            new WeaponHashEntry("RevolterMinigun", "VEHICLE_WEAPON_REVOLTER_MG"),
            new WeaponHashEntry("RogueCannon", "VEHICLE_WEAPON_ROGUE_CANNON"),
            new WeaponHashEntry("RogueMinigun", "VEHICLE_WEAPON_ROGUE_MG"),
            new WeaponHashEntry("RogueMissile", "VEHICLE_WEAPON_ROGUE_MISSILE"),
            new WeaponHashEntry("Rotors", "VEHICLE_WEAPON_ROTORS"),
            new WeaponHashEntry("RuinerBullet", "VEHICLE_WEAPON_RUINER_BULLET"),
            new WeaponHashEntry("RuinerRocket", "VEHICLE_WEAPON_RUINER_ROCKET"),
            new WeaponHashEntry("SavestraMinigun", "VEHICLE_WEAPON_SAVESTRA_MG"),
            new WeaponHashEntry("Scarab250CalLaser", "VEHICLE_WEAPON_SCARAB2_50CAL_LASER"),
            new WeaponHashEntry("Scarab50Cal", "VEHICLE_WEAPON_SCARAB_50CAL"),
            new WeaponHashEntry("ScramjetMinigun", "VEHICLE_WEAPON_SCRAMJET_MG"),
            new WeaponHashEntry("ScramjetMissile", "VEHICLE_WEAPON_SCRAMJET_MISSILE"),
            new WeaponHashEntry("SeabreezeMinigun", "VEHICLE_WEAPON_SEABREEZE_MG"),
            new WeaponHashEntry("SearchLight", "VEHICLE_WEAPON_SEARCHLIGHT"),
            new WeaponHashEntry("Slamvan450Cal", "VEHICLE_WEAPON_SLAMVAN4_50CAL"),
            new WeaponHashEntry("Slamvan550CalLaser", "VEHICLE_WEAPON_SLAMVAN5_50CAL_LASER"),
            new WeaponHashEntry("SpaceRocket", "VEHICLE_WEAPON_SPACE_ROCKET"),
            new WeaponHashEntry("Speedo4Minigun", "VEHICLE_WEAPON_SPEEDO4_MG"),
            new WeaponHashEntry("Speedo4TurretMini", "VEHICLE_WEAPON_SPEEDO4_TURRET_MINI"),
            new WeaponHashEntry("Speedo4TurretMinigun", "VEHICLE_WEAPON_SPEEDO4_TURRET_MG"),
            new WeaponHashEntry("StrikeforceBarrage", "VEHICLE_WEAPON_STRIKEFORCE_BARRAGE"),
            new WeaponHashEntry("StrikeforceCannon", "VEHICLE_WEAPON_STRIKEFORCE_CANNON"),
            new WeaponHashEntry("StrikeforceMissile", "VEHICLE_WEAPON_STRIKEFORCE_MISSILE"),
            new WeaponHashEntry("SubcarMinigun", "VEHICLE_WEAPON_SUBCAR_MG"),
            new WeaponHashEntry("SubcarMissile", "VEHICLE_WEAPON_SUBCAR_MISSILE"),
            new WeaponHashEntry("SubcarTorpedo", "VEHICLE_WEAPON_SUBCAR_TORPEDO"),
            new WeaponHashEntry("TampaDualMinigun", "VEHICLE_WEAPON_TAMPA_DUALMINIGUN"),
            new WeaponHashEntry("TampaFixedMinigun", "VEHICLE_WEAPON_TAMPA_FIXEDMINIGUN"),
            new WeaponHashEntry("TampaMissile", "VEHICLE_WEAPON_TAMPA_MISSILE"),
            new WeaponHashEntry("TampaMortar", "VEHICLE_WEAPON_TAMPA_MORTAR"),
            new WeaponHashEntry("Tank", "VEHICLE_WEAPON_TANK"),
            new WeaponHashEntry("Technical", "VEHICLE_WEAPON_TURRET_TECHNICAL"),
            new WeaponHashEntry("TechnicalMinigun", "VEHICLE_WEAPON_TECHNICAL_MINIGUN"),
            new WeaponHashEntry("ThrusterMinigun", "VEHICLE_WEAPON_THRUSTER_MG"),
            new WeaponHashEntry("ThrusterMissile", "VEHICLE_WEAPON_THRUSTER_MISSILE"),
            new WeaponHashEntry("TrailerDualAA", "VEHICLE_WEAPON_TRAILER_DUALAA"),
            new WeaponHashEntry("TrailerMissile", "VEHICLE_WEAPON_TRAILER_MISSILE"),
            new WeaponHashEntry("TrailerQuadMinigun", "VEHICLE_WEAPON_TRAILER_QUADMG"),
            new WeaponHashEntry("TulaDualMinigun", "VEHICLE_WEAPON_TULA_DUALMG"),
            new WeaponHashEntry("TulaMG", "VEHICLE_WEAPON_TULA_MG"),
            new WeaponHashEntry("TulaMinigun", "VEHICLE_WEAPON_TULA_MINIGUN"),
            new WeaponHashEntry("TulaNoseMinigun", "VEHICLE_WEAPON_TULA_NOSEMG"),
            new WeaponHashEntry("TurretBoxville", "VEHICLE_WEAPON_TURRET_BOXVILLE"),
            new WeaponHashEntry("TurretLimo", "VEHICLE_WEAPON_TURRET_LIMO"),
            new WeaponHashEntry("ValkyrieNoseTurret", "VEHICLE_WEAPON_NOSE_TURRET_VALKYRIE"),
            new WeaponHashEntry("ValkyrieTurret", "VEHICLE_WEAPON_TURRET_VALKYRIE"),
            new WeaponHashEntry("VigilanteMinigun", "VEHICLE_WEAPON_VIGILANTE_MG"),
            new WeaponHashEntry("VigilanteMissile", "VEHICLE_WEAPON_VIGILANTE_MISSILE"),
            new WeaponHashEntry("ViserisMinigun", "VEHICLE_WEAPON_VISERIS_MG"),
            new WeaponHashEntry("VolatolDualMinigun", "VEHICLE_WEAPON_VOLATOL_DUALMG"),
            new WeaponHashEntry("WaterCannon", "VEHICLE_WEAPON_WATER_CANNON"),
            new WeaponHashEntry("Zr380250CalLaser", "VEHICLE_WEAPON_ZR3802_50CAL_LASER"),
            new WeaponHashEntry("Zr38050Cal", "VEHICLE_WEAPON_ZR380_50CAL"),
        };

        // A newly grown *live* weapon slot seeds from this index instead, since the live setter treats a raw 0 as a genuine "no weapon" instruction
        // Seeding a slot with None here would leave it reading as unset before it can ever be reached to assign a real weapon
        // Resolved by name rather than a literal table position so it survives the table's own alphabetical ordering
        // Declared after WeaponHashTable, since field initializers run in declaration order
        private static readonly int DEFAULT_LIVE_WEAPON_SLOT_HASH_INDEX = Array.FindIndex(WeaponHashTable, entry => entry.NativeName == "VEHICLE_WEAPON_TANK");

        // The game's own "no weapon" convention is raw 0, not the API's VehicleWeaponHash.Invalid (0xFFFFFFFF)
        // Both the live display and the handling writer treat a slot as unset by this check, not by comparing against Invalid
        private static bool IsUnsetWeaponHash(VehicleWeaponHash hash) { return (uint)hash == 0; }

        private enum TypedWeaponHashKind { Refused, Table, Raw, Ambiguous }
        private class TypedWeaponHashResult
        {
            public TypedWeaponHashKind Kind;
            public int TableIndex;
            public uint RawHash;
            public List<string> Candidates;
        }

        // Order matters: A raw hash first, then an exact display or native name (with or without the native lead), then a start match that must pick exactly one weapon
        // None never matches, and a raw hash that equals a table weapon resolves to that weapon
        private static TypedWeaponHashResult ResolveTypedWeaponHash(string text)
        {
            TypedWeaponHashResult result = new TypedWeaponHashResult { Kind = TypedWeaponHashKind.Refused };
            string trimmed = text == null ? "" : text.Trim();
            if (trimmed.Length == 0) return result;
            bool isHexText = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            bool isDigitText = trimmed.Length > 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (!char.IsDigit(trimmed[i]))
                {
                    isDigitText = false;
                    break;
                }
            }
            if (isHexText || isDigitText)
            {
                uint rawHash;
                bool parsed = isHexText ? uint.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rawHash) : uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out rawHash);
                if (!parsed || rawHash == 0) return result;
                int rawTableIndex = Array.FindIndex(WeaponHashTable, entry => entry.Hash == rawHash);
                if (rawTableIndex > 0)
                {
                    result.Kind = TypedWeaponHashKind.Table;
                    result.TableIndex = rawTableIndex;
                }
                else
                {
                    result.Kind = TypedWeaponHashKind.Raw;
                    result.RawHash = rawHash;
                }
                return result;
            }
            for (int i = 1; i < WeaponHashTable.Length; i++)
            {
                if (WeaponHashNameMatches(WeaponHashTable[i], trimmed, true))
                {
                    result.Kind = TypedWeaponHashKind.Table;
                    result.TableIndex = i;
                    return result;
                }
            }
            if (trimmed.Length < WEAPON_HASH_MIN_PREFIX_LENGTH) return result;
            List<int> matches = new List<int>();
            for (int i = 1; i < WeaponHashTable.Length; i++) if (WeaponHashNameMatches(WeaponHashTable[i], trimmed, false)) matches.Add(i);
            if (matches.Count == 1)
            {
                result.Kind = TypedWeaponHashKind.Table;
                result.TableIndex = matches[0];
            }
            else if (matches.Count > 1)
            {
                result.Kind = TypedWeaponHashKind.Ambiguous;
                result.Candidates = new List<string>();
                for (int i = 0; i < matches.Count; i++) result.Candidates.Add(WeaponHashTable[matches[i]].DisplayName);
            }
            return result;
        }

        private static bool WeaponHashNameMatches(WeaponHashEntry entry, string typed, bool exact)
        {
            string shortNativeName = entry.NativeName.StartsWith(WEAPON_NATIVE_NAME_PREFIX, StringComparison.Ordinal) ? entry.NativeName.Substring(WEAPON_NATIVE_NAME_PREFIX.Length) : entry.NativeName;
            if (exact) return string.Equals(entry.DisplayName, typed, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.NativeName, typed, StringComparison.OrdinalIgnoreCase) || string.Equals(shortNativeName, typed, StringComparison.OrdinalIgnoreCase);
            return entry.DisplayName.StartsWith(typed, StringComparison.OrdinalIgnoreCase) || entry.NativeName.StartsWith(typed, StringComparison.OrdinalIgnoreCase) || shortNativeName.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
        }

        // LeftFront and RightFront are excluded deliberately, since the API's own VehicleSeat enum declares them as literal duplicate values of Driver and Passenger
        // They are not distinct seats, so a cycler offering both would let a selection collapse to the other's name the moment the display re-syncs
        // That happens on a menu reopen, reload, Undo or Redo, or a saved handling's raw value read back, since only the raw int survives a save
        private static readonly string[] VehicleSeatNames =
        {
            "None", "Any", "Driver", "Passenger", "LeftRear", "RightRear", "ExtraSeat1", "ExtraSeat2", "ExtraSeat3", "ExtraSeat4",
            "ExtraSeat5", "ExtraSeat6", "ExtraSeat7", "ExtraSeat8", "ExtraSeat9", "ExtraSeat10", "ExtraSeat11", "ExtraSeat12"
        };

        private static readonly string[] VehicleModTypeNames =
        {
            "None", "Spoilers", "FrontBumper", "RearBumper", "SideSkirt", "Exhaust", "Frame", "Grille", "Hood", "Fender", "RightFender", "Roof", "Engine", "Brakes", "Transmission", "Horns", "Suspension", "Armor", "FrontWheel", "RearWheel",
            "PlateHolder", "VanityPlates", "TrimDesign", "Ornaments", "Dashboard", "DialDesign", "DoorSpeakers", "Seats", "SteeringWheels", "ColumnShifterLevers", "Plaques", "Speakers", "Trunk", "Hydraulics", "EngineBlock", "AirFilter",
            "Struts", "ArchCover", "Aerials", "Trim", "Tank", "Windows", "Livery"
        };
    }
}
