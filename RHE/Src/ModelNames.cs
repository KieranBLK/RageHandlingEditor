using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private readonly IModelNameResolver modelNames;

        // A handling struct's identity in game memory. Every vehicle model on a handling reads the same struct, so everything describing the handling is keyed by it
        // An address of 0 is an invalid key
        private struct HandlingKey : IEquatable<HandlingKey>
        {
            public readonly IntPtr Address;
            public HandlingKey(IntPtr address) { Address = address; }
            public bool IsValid { get { return Address != IntPtr.Zero; } }
            public bool Equals(HandlingKey other) { return Address == other.Address; }
            public override bool Equals(object obj) { return obj is HandlingKey && Equals((HandlingKey)obj); }
            public override int GetHashCode() { return Address.GetHashCode(); }
        }

        private static HandlingKey ResolveHandlingKey(HandlingData handling)
        {
            return handling != null && handling.IsValid ? new HandlingKey(handling.MemoryAddress) : new HandlingKey();
        }

        // The key of the vehicle's live handling, read from its own handling pointer, which another script can swap. Invalid for a missing vehicle or handling
        private static HandlingKey ResolveHandlingKey(Vehicle vehicle)
        {
            return Entity.IsNullOrNotExisting(vehicle) ? new HandlingKey() : ResolveHandlingKey(vehicle.HandlingData);
        }

        // The key of the handling a name hash names, invalid when the game's table holds none
        private static HandlingKey ResolveHandlingKeyFromNameHash(int nameHash)
        {
            return ResolveHandlingKey(HandlingData.GetByHash(nameHash));
        }

        private interface IModelNameResolver
        {
            string ResolveModelName(Vehicle vehicle);
            string ResolveHandlingName(Vehicle vehicle);
            bool ReadHandlingNameHash(HandlingKey key, out int nameHash);
            HandlingData ResolveHandlingFromName(string handlingName);
            HandlingData ResolveModelHandling(int modelHash);
            HandlingKey ResolveHandlingKeyFromName(string handlingName);
            string DescribeHandling(HandlingKey key);
            bool IsModelMetaDataMissing();
            bool RetryMissingModelMetaData();
            IEnumerable<KeyValuePair<int, string>> GetModelMetaDataNames();
        }

        // Holds the ModelMetaData file's maps and every name and hash cache, so each cache can only be filled or cleared by the lookups that own it
        private sealed class ModelNameResolver : IModelNameResolver
        {
            private readonly ILogger logger;
            // Model hash to model name and model hash to handling id, read once per script load, or once it appears
            private Dictionary<int, string> modelMetaDataNames = new Dictionary<int, string>();
            private Dictionary<int, string> modelMetaDataHandlingIds = new Dictionary<int, string>();
            private bool modelMetaDataLoaded;
            // True while the file was missing at the first name lookup
            // On a first launch the plugin can write it after the script starts, so the tick checks for it until it appears
            private bool modelMetaDataMissing;
            // Resolved names per model hash. Every input is fixed once the ModelMetaData file is read, so each model resolves and logs once per read
            private Dictionary<int, string> resolvedModelNames = new Dictionary<int, string>();
            private Dictionary<int, string> resolvedHandlingNames = new Dictionary<int, string>();
            // The name hash each key's struct stores for itself, read once and accepted only when the game's own lookup of that hash returns the same struct
            private Dictionary<HandlingKey, int> verifiedHandlingNameHashes = new Dictionary<HandlingKey, int>();
            // Keys whose name hash failed that check, so each warns once
            private HashSet<HandlingKey> unverifiedHandlingNameHashLogged = new HashSet<HandlingKey>();
            // The handling name ResolveHandlingName last resolved for each key, for log lines
            private Dictionary<HandlingKey, string> handlingKeyNames = new Dictionary<HandlingKey, string>();
            // The handling each saved handling's handlingName resolved to, ignoring case. Only a successful resolution is kept
            private Dictionary<string, HandlingData> resolvedNameHandlings = new Dictionary<string, HandlingData>(StringComparer.OrdinalIgnoreCase);

            public ModelNameResolver(ILogger resolverLogger)
            {
                logger = resolverLogger;
            }

            public bool IsModelMetaDataMissing() { return modelMetaDataMissing; }

            // Loads the file on the first read, like every name lookup
            public IEnumerable<KeyValuePair<int, string>> GetModelMetaDataNames()
            {
                if (!modelMetaDataLoaded) LoadModelMetaData();
                return modelMetaDataNames;
            }

            // Keeps only entries whose model name hashes back to their own hash, so a stale or hand-edited entry can never name the wrong model
            private void LoadModelMetaData()
            {
                modelMetaDataLoaded = true;
                if (!File.Exists(MODEL_METADATA_FILE_PATH))
                {
                    modelMetaDataMissing = true;
                    logger.Log(Dbg.Level.Info, Dbg.ModelMetaDataNotFound(MODEL_METADATA_FILE_PATH));
                    return;
                }
                int skipped = 0;
                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var document = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(MODEL_METADATA_FILE_PATH));
                    object models;
                    if (document == null || !document.TryGetValue("Models", out models) || !(models is IEnumerable))
                    {
                        logger.Log(Dbg.Level.Warn, Dbg.ModelMetaDataReadFailed("no Models array"));
                        return;
                    }
                    foreach (object item in (IEnumerable)models)
                    {
                        var entry = item as Dictionary<string, object>;
                        object hashValue, nameValue, handlingIdValue;
                        if (entry == null || !entry.TryGetValue("Hash", out hashValue) || !entry.TryGetValue("ModelName", out nameValue))
                        {
                            skipped++;
                            continue;
                        }
                        long hash;
                        try { hash = Convert.ToInt64(hashValue); }
                        catch (Exception)
                        {
                            skipped++;
                            continue;
                        }
                        string name = nameValue as string;
                        if (string.IsNullOrEmpty(name) || hash < 0 || hash > uint.MaxValue || GTA.StringHash.AtStringHash(name) != (uint)hash)
                        {
                            skipped++;
                            continue;
                        }
                        int modelHash = (int)(uint)hash;
                        modelMetaDataNames[modelHash] = name;
                        string handlingId = entry.TryGetValue("HandlingId", out handlingIdValue) ? handlingIdValue as string : null;
                        if (string.IsNullOrEmpty(handlingId)) continue;
                        modelMetaDataHandlingIds[modelHash] = handlingId;
                    }
                    logger.Log(Dbg.Level.Info, Dbg.ModelMetaDataLoaded(modelMetaDataNames.Count, modelMetaDataHandlingIds.Count, skipped));
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.ModelMetaDataReadFailed(ex.Message)); }
            }

            // Once the missing file exists, loads it and drops every name resolved without it, so the next lookup of each model uses the file's names
            // Returns True when it loaded the file, so the caller can drop its own name index too
            public bool RetryMissingModelMetaData()
            {
                if (!File.Exists(MODEL_METADATA_FILE_PATH)) return false;
                modelMetaDataMissing = false;
                LoadModelMetaData();
                resolvedModelNames.Clear();
                resolvedHandlingNames.Clear();
                return true;
            }

            // The vehicle's name everywhere it is shown, logged, or saved under by default, upper-cased
            // The first 2 sources are only trusted when the name hashes back to the model hash
            // The display name can be a cut-off label, since the game stores it in 11 characters, and the hash stands in when it is empty or unresolved
            public string ResolveModelName(Vehicle vehicle)
            {
                int modelHash = vehicle.Model.Hash;
                string name;
                if (resolvedModelNames.TryGetValue(modelHash, out name)) return name;
                if (!modelMetaDataLoaded) LoadModelMetaData();
                string source;
                string baseGameName = ((VehicleHash)(uint)modelHash).ToString();
                string displayName = vehicle.DisplayName;
                if (modelMetaDataNames.TryGetValue(modelHash, out name)) source = "ModelMetaData file";
                else if (GTA.StringHash.AtStringHash(baseGameName) == (uint)modelHash)
                {
                    name = baseGameName;
                    source = "base-game model list";
                }
                else if (!string.IsNullOrEmpty(displayName) && displayName != UNRESOLVED_DISPLAY_NAME)
                {
                    name = displayName;
                    source = "display name";
                }
                else
                {
                    name = modelHash.ToString("X8");
                    source = "model hash";
                }
                name = name.ToUpperInvariant();
                resolvedModelNames[modelHash] = name;
                logger.Log(Dbg.Level.Info, Dbg.ModelNameResolved(name, modelHash, source));
                return name;
            }

            // An export writes the file's handling id (upper-cased by the plugin) only when it resolves to this vehicle's own handling entry, otherwise the model name
            // That check means a stale file can never put another vehicle's name in an export
            public string ResolveHandlingName(Vehicle vehicle)
            {
                int modelHash = vehicle.Model.Hash;
                string name;
                if (resolvedHandlingNames.TryGetValue(modelHash, out name)) return name;
                if (!modelMetaDataLoaded) LoadModelMetaData();
                string source;
                string handlingId;
                if (modelMetaDataHandlingIds.TryGetValue(modelHash, out handlingId) && HandlingData.GetByHash((int)GTA.StringHash.AtStringHash(handlingId)).MemoryAddress == vehicle.HandlingData.MemoryAddress)
                {
                    name = handlingId;
                    source = "ModelMetaData file";
                }
                else
                {
                    name = ResolveModelName(vehicle);
                    source = "model name";
                }
                resolvedHandlingNames[modelHash] = name;
                HandlingKey key = ResolveHandlingKey(vehicle);
                if (key.IsValid) handlingKeyNames[key] = name;
                logger.Log(Dbg.Level.Info, Dbg.HandlingNameResolved(name, modelHash, source));
                return name;
            }

            // The name hash the key's struct stores right after its vtable pointer
            // A build below the raw offset floor reads 0 and a layout change reads another value, so neither verifies and that handling stays out of the reload state
            // In-session behavior never depends on it
            public bool ReadHandlingNameHash(HandlingKey key, out int nameHash)
            {
                if (!key.IsValid)
                {
                    nameHash = 0;
                    return false;
                }
                if (verifiedHandlingNameHashes.TryGetValue(key, out nameHash)) return true;
                int rawHash = RawMemory.ReadRawInt(key.Address, HandlingDataLayout.HandlingName);
                if (rawHash != 0 && HandlingData.GetByHash(rawHash).MemoryAddress == key.Address)
                {
                    verifiedHandlingNameHashes[key] = rawHash;
                    nameHash = rawHash;
                    return true;
                }
                nameHash = 0;
                if (unverifiedHandlingNameHashLogged.Add(key))
                {
                    string name;
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingNameHashUnverified(handlingKeyNames.TryGetValue(key, out name) ? name : "UNNAMED", key.Address.ToInt64(), rawHash));
                }
                return false;
            }

            // The handling a saved handling's handlingName names, or null when none resolves
            // The game's handling table comes first, since that is what the element means, then for a name that is a model, that vehicle model's handling
            public HandlingData ResolveHandlingFromName(string handlingName)
            {
                if (string.IsNullOrEmpty(handlingName)) return null;
                HandlingData handling;
                if (resolvedNameHandlings.TryGetValue(handlingName, out handling)) return handling;
                handling = HandlingData.GetByHash((int)GTA.StringHash.AtStringHash(handlingName));
                if (!handling.IsValid)
                {
                    handling = ResolveModelHandling((int)GTA.StringHash.AtStringHash(handlingName));
                    if (handling == null) return null;
                }
                resolvedNameHandlings[handlingName] = handling;
                return handling;
            }

            // A model's handling, or null when it has none, read without the API's model lookup for any model the ModelMetaData file lists
            // The API indexes the handling array by the model's stored index with no range check, so an add-on whose handling id names no loaded entry crashes the game
            // A listed model resolves by its handling id through the game's lookup by name instead, which returns nothing for a missing entry
            // The model's stored index comes from that same id, so both agree whenever the entry exists. A base game model with no file entry always has one
            public HandlingData ResolveModelHandling(int modelHash)
            {
                if (!modelMetaDataLoaded) LoadModelMetaData();
                var model = new Model(modelHash);
                if (!model.IsValid || !model.IsVehicle) return null;
                string handlingId;
                HandlingData handling = modelMetaDataHandlingIds.TryGetValue(modelHash, out handlingId) ? HandlingData.GetByHash((int)GTA.StringHash.AtStringHash(handlingId)) : HandlingData.GetByVehicleModel(model);
                return handling.IsValid ? handling : null;
            }

            public HandlingKey ResolveHandlingKeyFromName(string handlingName)
            {
                return ResolveHandlingKey(ResolveHandlingFromName(handlingName));
            }

            // The name ResolveHandlingName last resolved for the handling, otherwise its verified name hash in hex, so a log never shows a bare address
            public string DescribeHandling(HandlingKey key)
            {
                string name;
                if (handlingKeyNames.TryGetValue(key, out name)) return name;
                int nameHash;
                return ReadHandlingNameHash(key, out nameHash) ? "0x" + nameHash.ToString("X8") : "UNNAMED";
            }
        }
    }
}
