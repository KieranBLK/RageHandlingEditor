#include "Eviction.h"
#include "Capture.h"
#include "Log.h"
#include "RequestQueue.h"
#include "Scan.h"
#include <windows.h>
#include <atomic>
#include <cstring>
#include <map>
#include <string>
#include <vector>

// Streaming manager patterns. The manager and remove patterns sit in several callers, so their matches must agree on one target, and the other 2 match once
// They locate the manager instance (a rip-relative address), the remove call, the ready-to-delete check, and the module-by-extension call
static const char* const MANAGER_PATTERN = "74 1A 8B 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? 41";
static const size_t MANAGER_ADDRESS_OFFSET = 11;
static const char* const REMOVE_OBJECT_PATTERN = "45 33 C0 03 D7 E8 ?? ?? ?? ?? 48 8B 03 48 8B CB";
static const size_t REMOVE_OBJECT_CALL_OFFSET = 5;
static const char* const READY_TO_DELETE_PATTERN = "89 54 24 10 48 83 EC 28 48 8B 01 41 81 E0 0E FF";
static const char* const MODULE_FROM_EXTENSION_PATTERN = "74 15 48 8D 50 01 48 8D";
static const size_t MODULE_FROM_EXTENSION_CALL_OFFSET = 13;

// Enhanced's patterns. The manager and remove patterns sit in several callers whose targets must agree, as above
// Enhanced compiles the module lookup by extension and the ready check inline, so its lookup pattern finds the extension map instead
// The map is keyed by the extension's hash, which HashModelName computes the same way
// The game's own hash function there is a leaf with no unwind entry, so it cannot be verified as a function start before a call
static const char* const ENHANCED_MANAGER_PATTERN = "48 8B 05 ?? ?? ?? ?? 89 D1 F6 44 C8 06 F1";
static const size_t ENHANCED_MANAGER_ADDRESS_OFFSET = 3;
static const char* const ENHANCED_REMOVE_OBJECT_PATTERN = "F6 44 C8 06 F1 75 ?? ?? 89 ?? 45 31 C0 E8";
static const size_t ENHANCED_REMOVE_OBJECT_CALL_OFFSET = 13;
static const char* const ENHANCED_EXTENSION_LOOKUP_PATTERN = "31 D2 E8 ?? ?? ?? ?? 89 C1 48 8B 1D ?? ?? ?? ?? 31 D2 F7 35 ?? ?? ?? ?? 8B 14 93 83 FA FF 74 ?? 48 8B 05 ?? ?? ?? ??";
static const size_t ENHANCED_MAP_BUCKETS_DISPLACEMENT_OFFSET = 12;
static const size_t ENHANCED_MAP_BUCKET_COUNT_DISPLACEMENT_OFFSET = 20;
static const size_t ENHANCED_MAP_ENTRIES_DISPLACEMENT_OFFSET = 35;

// Streaming manager layout: The entry array pointer, the entry count, and the module manager the extension lookup takes
// Enhanced keeps the entry count 8 bytes earlier
// Where the other edition holds its module manager, Enhanced holds the modules array pointer and the module count (a 16 bit word)
static const size_t MANAGER_ENTRIES_OFFSET = 0x0;
static const size_t MANAGER_ENTRY_COUNT_OFFSET = 0x18;
static const size_t ENHANCED_MANAGER_ENTRY_COUNT_OFFSET = 0x10;
static const size_t MANAGER_MODULE_MANAGER_OFFSET = 0x1B8;
static const size_t ENHANCED_MANAGER_MODULES_OFFSET = 0x1B0;
static const size_t ENHANCED_MANAGER_MODULE_COUNT_OFFSET = 0x1B8;

// Enhanced's extension map: The entry array, the bucket array, and the bucket count sit 8 bytes apart in that order
// Each 12 byte entry holds the extension's hash, its module index, and the next entry in the chain, and this value ends a chain
static const uintptr_t ENHANCED_MAP_GLOBAL_STRIDE = 8;
static const size_t MAP_ENTRY_SIZE = 12;
static const size_t MAP_ENTRY_VALUE_OFFSET = 4;
static const size_t MAP_ENTRY_NEXT_OFFSET = 8;
static const uint32_t MAP_CHAIN_END = 0xFFFFFFFF;

// Enhanced's inline ready check, the same rule as the function the other edition calls with no flags passed
// The entry's flags (state bits 16 to 23) must share no bit with the mask, its dependent count (bits 2 to 15) must be 0, and the module must hold no references
static const uint32_t STATE_FLAGS_SHIFT = 16;
static const uint32_t STATE_BLOCKING_FLAGS = 0xF1;
static const uint32_t STATE_DEPENDENTS_SHIFT = 2;
static const uint32_t STATE_DEPENDENTS_MASK = 0x3FFF;
static const size_t MODULE_GET_NUM_REFS_SLOT = 25;

// Each 8 byte entry holds a handle, then a word with the status in bits 0 to 1. Status 0 is not loaded and 1 is loaded
static const size_t ENTRY_SIZE = 8;
static const size_t ENTRY_STATE_OFFSET = 4;
static const uint32_t ENTRY_STATUS_MASK = 0x3;
static const uint32_t STATUS_NOT_LOADED = 0;
static const uint32_t STATUS_LOADED = 1;

// A module's first global streaming index sits right after its vtable pointer
static const size_t MODULE_BASE_INDEX_OFFSET = 0x8;

// The module vtable slot of FindSlot on current builds, which insert 6 slots ahead of the older layout
static const size_t MODULE_FIND_SLOT_SLOT = 8;

// The fragment module's file extension, and the slot value that means no slot
static const char* const FRAGMENT_EXTENSION = "yft";
static const uint32_t INVALID_SLOT = 0xFFFFFFFF;

typedef void*(__fastcall* ModuleFromExtensionFunction)(void* moduleManager, const char* extension);
typedef bool(__fastcall* ReadyToDeleteFunction)(void* manager, uint32_t index, int flags);
typedef bool(__fastcall* RemoveObjectFunction)(void* manager, uint32_t index, bool skipCompleteRpfCheck);
typedef uint32_t*(__fastcall* FindSlotFunction)(void* module, uint32_t* slot, const char* name);
typedef int(__fastcall* GetNumRefsFunction)(void* module, uint32_t slot);

// The exports mark requests from a caller's thread and the script loop takes them, which the queue guards with its own mutex, keyed by model hash
static RequestQueue<uint32_t, EvictionOutcome> evictionQueue(EvictionOutcome::Pending, EvictionOutcome::Canceled);

// The setup's state is written once by the script loop and may be read from any thread, so it is atomic and needs no lock of its own
static std::atomic<bool> resolveFinished(false);
static std::atomic<bool> resolved(false);

// Touched only by the script thread: The last outcome each model logged during its current retry streak
static std::map<uint32_t, EvictionOutcome> loggedOutcomes;

// The scans write these once on the attach path and ResolveEviction the fragment module, all before any request is taken, and they are only read afterward
// moduleFromExtension is the original edition's module lookup, and extensionMapEntries is Enhanced's extension map global
static bool patternsFound = false;
static uint8_t* manager = nullptr;
static ReadyToDeleteFunction readyToDelete = nullptr;
static RemoveObjectFunction removeObject = nullptr;
static ModuleFromExtensionFunction moduleFromExtension = nullptr;
static uintptr_t extensionMapEntries = 0;
static void* fragmentModule = nullptr;
static size_t managerEntryCountOffset = MANAGER_ENTRY_COUNT_OFFSET;
static bool inlineReadyCheck = false;

// Holds no C++ objects, since a structured exception frame cannot share a function with them
static void* GuardedModuleFromExtension(ModuleFromExtensionFunction function, void* moduleManager)
{
    __try
    {
        return function(moduleManager, FRAGMENT_EXTENSION);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }
}

// Enhanced's inline module lookup, read the same way the game's own callers walk it: The extension's hash picks a bucket, and the chain gives the module index
// Holds no C++ objects, since a structured exception frame cannot share a function with them
static void* GuardedEnhancedModuleLookup(uint32_t hash, uintptr_t mapEntriesGlobal, uint8_t* streamingManager)
{
    __try
    {
        const uint8_t* entries = *reinterpret_cast<const uint8_t* const*>(mapEntriesGlobal);
        const uint32_t* buckets = *reinterpret_cast<const uint32_t* const*>(mapEntriesGlobal + ENHANCED_MAP_GLOBAL_STRIDE);
        uint32_t bucketCount = *reinterpret_cast<const uint32_t*>(mapEntriesGlobal + 2 * ENHANCED_MAP_GLOBAL_STRIDE);
        if (entries == nullptr || buckets == nullptr || bucketCount == 0) return nullptr;
        for (uint32_t index = buckets[hash % bucketCount]; index != MAP_CHAIN_END;)
        {
            const uint8_t* entry = entries + static_cast<size_t>(index) * MAP_ENTRY_SIZE;
            if (*reinterpret_cast<const uint32_t*>(entry) != hash)
            {
                index = *reinterpret_cast<const uint32_t*>(entry + MAP_ENTRY_NEXT_OFFSET);
                continue;
            }
            uint32_t moduleIndex = *reinterpret_cast<const uint32_t*>(entry + MAP_ENTRY_VALUE_OFFSET);
            uint16_t moduleCount = *reinterpret_cast<const uint16_t*>(streamingManager + ENHANCED_MANAGER_MODULE_COUNT_OFFSET);
            if (moduleIndex >= moduleCount) return nullptr;
            void* const* modules = *reinterpret_cast<void* const* const*>(streamingManager + ENHANCED_MANAGER_MODULES_OFFSET);
            return modules != nullptr ? modules[moduleIndex] : nullptr;
        }
        return nullptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }
}

// The first half both editions share: The streaming manager and the RemoveObject call, each found by a pattern whose matches agree on one target
// The call target must also be a registered function start
struct SharedParts
{
    uintptr_t managerAddress;
    uintptr_t removeTarget;
};

static bool ResolveSharedParts(const char* managerPattern, size_t managerOffset, const char* removePattern, size_t removeCallOffset, SharedParts& parts, std::string& failure)
{
    std::string partFailure;
    parts.managerAddress = ScanAgreeingTarget(managerPattern, managerOffset, partFailure);
    if (parts.managerAddress == 0)
    {
        failure = Dbg::EvictionUnavailable("Manager", partFailure);
        return false;
    }
    parts.removeTarget = ScanAgreeingTarget(removePattern, removeCallOffset + REL32_DISPLACEMENT_OFFSET, partFailure);
    if (parts.removeTarget == 0 || !IsRegisteredFunctionStart(parts.removeTarget))
    {
        failure = Dbg::EvictionUnavailable("RemoveObject", parts.removeTarget == 0 ? partFailure : Dbg::NotFunctionStart());
        return false;
    }
    return true;
}

// The ready block both editions log: The 2 shared parts, then the part that differs, then the fragment module
static void LogEvictionReady(const char* thirdPartName, uintptr_t thirdPartAddress)
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    LogInfo(Dbg::EvictionReadyRoot());
    LogInfo(Dbg::EvictionReadyPart("Manager", reinterpret_cast<uintptr_t>(manager) - moduleBase));
    LogInfo(Dbg::EvictionReadyPart("RemoveObject", reinterpret_cast<uintptr_t>(removeObject) - moduleBase));
    LogInfo(Dbg::EvictionReadyPart(thirdPartName, thirdPartAddress - moduleBase));
    LogInfo(Dbg::EvictionReadyFragmentModule(reinterpret_cast<uintptr_t>(fragmentModule)));
}

static bool ScanEnhancedParts(std::string& failure)
{
    std::string partFailure;
    SharedParts parts = {};
    if (!ResolveSharedParts(ENHANCED_MANAGER_PATTERN, ENHANCED_MANAGER_ADDRESS_OFFSET, ENHANCED_REMOVE_OBJECT_PATTERN, ENHANCED_REMOVE_OBJECT_CALL_OFFSET, parts, failure)) return false;
    uintptr_t lookupMatch = ScanSingleMatch(ENHANCED_EXTENSION_LOOKUP_PATTERN, partFailure);
    if (lookupMatch == 0)
    {
        failure = Dbg::EvictionUnavailable("ModuleLookup", partFailure);
        return false;
    }
    uintptr_t mapEntries = ReadRelativeTarget(lookupMatch + ENHANCED_MAP_ENTRIES_DISPLACEMENT_OFFSET);
    uintptr_t mapBuckets = ReadRelativeTarget(lookupMatch + ENHANCED_MAP_BUCKETS_DISPLACEMENT_OFFSET);
    uintptr_t mapBucketCount = ReadRelativeTarget(lookupMatch + ENHANCED_MAP_BUCKET_COUNT_DISPLACEMENT_OFFSET);
    if (mapBuckets != mapEntries + ENHANCED_MAP_GLOBAL_STRIDE || mapBucketCount != mapEntries + 2 * ENHANCED_MAP_GLOBAL_STRIDE)
    {
        failure = Dbg::EvictionUnavailable("ExtensionMap", Dbg::MapLayoutMismatch());
        return false;
    }
    manager = reinterpret_cast<uint8_t*>(parts.managerAddress);
    removeObject = reinterpret_cast<RemoveObjectFunction>(parts.removeTarget);
    extensionMapEntries = mapEntries;
    managerEntryCountOffset = ENHANCED_MANAGER_ENTRY_COUNT_OFFSET;
    inlineReadyCheck = true;
    return true;
}

static bool ScanParts(std::string& failure)
{
    if (IsEnhancedEdition()) return ScanEnhancedParts(failure);
    std::string partFailure;
    SharedParts parts = {};
    if (!ResolveSharedParts(MANAGER_PATTERN, MANAGER_ADDRESS_OFFSET, REMOVE_OBJECT_PATTERN, REMOVE_OBJECT_CALL_OFFSET, parts, failure)) return false;
    uintptr_t readyTarget = ScanSingleMatch(READY_TO_DELETE_PATTERN, partFailure);
    if (readyTarget == 0 || !IsRegisteredFunctionStart(readyTarget))
    {
        failure = Dbg::EvictionUnavailable("IsObjectReadyToDelete", readyTarget == 0 ? partFailure : Dbg::NotFunctionStart());
        return false;
    }
    uintptr_t extensionMatch = ScanSingleMatch(MODULE_FROM_EXTENSION_PATTERN, partFailure);
    if (extensionMatch == 0)
    {
        failure = Dbg::EvictionUnavailable("ModuleLookup", partFailure);
        return false;
    }
    // The pattern ends before the call, so the call opcode is checked before its displacement is read
    if (!GuardedBytesEqual(extensionMatch + MODULE_FROM_EXTENSION_CALL_OFFSET, &CALL_OPCODE, sizeof(CALL_OPCODE)))
    {
        failure = Dbg::EvictionUnavailable("ModuleLookup", Dbg::NotCallInstruction());
        return false;
    }
    uintptr_t extensionTarget = ReadRelativeTarget(extensionMatch + MODULE_FROM_EXTENSION_CALL_OFFSET + REL32_DISPLACEMENT_OFFSET);
    if (!IsRegisteredFunctionStart(extensionTarget))
    {
        failure = Dbg::EvictionUnavailable("ModuleLookup", Dbg::NotFunctionStart());
        return false;
    }
    manager = reinterpret_cast<uint8_t*>(parts.managerAddress);
    removeObject = reinterpret_cast<RemoveObjectFunction>(parts.removeTarget);
    readyToDelete = reinterpret_cast<ReadyToDeleteFunction>(readyTarget);
    moduleFromExtension = reinterpret_cast<ModuleFromExtensionFunction>(extensionTarget);
    return true;
}

// The lookup calls into or reads the game's streaming state, not its code, so it runs on the script loop, where the game has finished loading
static bool ResolveFragmentModule(std::string& failure)
{
    bool enhanced = IsEnhancedEdition();
    void* module = enhanced ? GuardedEnhancedModuleLookup(HashModelName(FRAGMENT_EXTENSION), extensionMapEntries, manager) : GuardedModuleFromExtension(moduleFromExtension, manager + MANAGER_MODULE_MANAGER_OFFSET);
    if (module == nullptr)
    {
        failure = Dbg::EvictionUnavailable("FragmentModule", Dbg::ModuleLookupFailed());
        return false;
    }
    fragmentModule = module;
    if (enhanced) LogEvictionReady("ExtensionMap", extensionMapEntries);
    else LogEvictionReady("IsObjectReadyToDelete", reinterpret_cast<uintptr_t>(readyToDelete));
    return true;
}

void ScanEvictionPatterns(bool buildVerified)
{
    if (!buildVerified) return;
    std::string failure;
    patternsFound = ScanParts(failure);
    if (!patternsFound) LogError(failure);
}

void ResolveEviction()
{
    bool partsResolved = false;
    if (patternsFound)
    {
        std::string failure;
        partsResolved = ResolveFragmentModule(failure);
        if (!partsResolved) LogError(failure);
    }
    resolved = partsResolved;
    resolveFinished = true;
}

// Every game call of one attempt, guarded so a fault reads Faulted instead of crashing the game
// The ready check and the removal run in one frame, the order the game's own memory eviction uses, so no dependent can appear between them
static EvictionOutcome GuardedEvict(const char* modelName)
{
    __try
    {
        void** vtable = *reinterpret_cast<void***>(fragmentModule);
        uint32_t slot = INVALID_SLOT;
        reinterpret_cast<FindSlotFunction>(vtable[MODULE_FIND_SLOT_SLOT])(fragmentModule, &slot, modelName);
        if (slot == INVALID_SLOT) return EvictionOutcome::NoSlot;
        uint32_t baseIndex = *reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(fragmentModule) + MODULE_BASE_INDEX_OFFSET);
        uint32_t entryCount = *reinterpret_cast<uint32_t*>(manager + managerEntryCountOffset);
        uint32_t globalIndex = baseIndex + slot;
        if (globalIndex >= entryCount) return EvictionOutcome::NoSlot;
        uint32_t* state = reinterpret_cast<uint32_t*>(*reinterpret_cast<uint8_t**>(manager + MANAGER_ENTRIES_OFFSET) + static_cast<size_t>(globalIndex) * ENTRY_SIZE + ENTRY_STATE_OFFSET);
        uint32_t status = *state & ENTRY_STATUS_MASK;
        if (status == STATUS_NOT_LOADED) return EvictionOutcome::NotLoaded;
        if (status != STATUS_LOADED) return EvictionOutcome::NotReady;
        if (inlineReadyCheck)
        {
            uint32_t stateValue = *state;
            bool blocked = ((stateValue >> STATE_FLAGS_SHIFT) & STATE_BLOCKING_FLAGS) != 0 || ((stateValue >> STATE_DEPENDENTS_SHIFT) & STATE_DEPENDENTS_MASK) != 0;
            if (blocked || reinterpret_cast<GetNumRefsFunction>(vtable[MODULE_GET_NUM_REFS_SLOT])(fragmentModule, slot) != 0) return EvictionOutcome::NotReady;
        }
        else if (!readyToDelete(manager, globalIndex, 0)) return EvictionOutcome::NotReady;
        removeObject(manager, globalIndex, false);
        return (*state & ENTRY_STATUS_MASK) == STATUS_NOT_LOADED ? EvictionOutcome::Removed : EvictionOutcome::Refused;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return EvictionOutcome::Faulted;
    }
}

static std::string OutcomeName(EvictionOutcome outcome)
{
    switch (outcome)
    {
        case EvictionOutcome::Pending: return "Pending";
        case EvictionOutcome::Removed: return "Removed";
        case EvictionOutcome::NotLoaded: return "NotLoaded";
        case EvictionOutcome::NotReady: return "NotReady";
        case EvictionOutcome::Refused: return "Refused";
        case EvictionOutcome::UnknownModel: return "UnknownModel";
        case EvictionOutcome::NoSlot: return "NoSlot";
        case EvictionOutcome::Faulted: return "Faulted";
        case EvictionOutcome::Unavailable: return "Unavailable";
        case EvictionOutcome::Canceled: return "Canceled";
    }
    return "Unknown";
}

static void LogOutcome(uint32_t hash, const std::string& modelName, EvictionOutcome outcome)
{
    bool isError = outcome == EvictionOutcome::Refused || outcome == EvictionOutcome::UnknownModel || outcome == EvictionOutcome::NoSlot || outcome == EvictionOutcome::Faulted;
    if (isError) LogError(Dbg::EvictionOutcomeLine(modelName, hash, OutcomeName(outcome)));
    else LogInfo(Dbg::EvictionOutcomeLine(modelName, hash, OutcomeName(outcome)));
}

// A retry streak (NotReady or Refused) logs once per change, and any other outcome ends the streak, so the next unload of the same model logs its own result
static bool RecordOutcomeForLog(uint32_t hash, EvictionOutcome outcome)
{
    bool isRetry = outcome == EvictionOutcome::NotReady || outcome == EvictionOutcome::Refused;
    auto logged = loggedOutcomes.find(hash);
    bool changed = logged == loggedOutcomes.end() || logged->second != outcome;
    if (isRetry) loggedOutcomes[hash] = outcome;
    else if (logged != loggedOutcomes.end()) loggedOutcomes.erase(logged);
    return changed;
}

void ProcessEvictionRequests()
{
    if (!resolveFinished) return;
    for (uint32_t hash : evictionQueue.TakeMarked())
    {
        std::string modelName;
        EvictionOutcome outcome = EvictionOutcome::Unavailable;
        if (resolved) outcome = FindCapturedModelName(hash, modelName) ? GuardedEvict(modelName.c_str()) : EvictionOutcome::UnknownModel;
        evictionQueue.Finish(hash, outcome);
        if (RecordOutcomeForLog(hash, outcome)) LogOutcome(hash, modelName, outcome);
    }
}

int RequestFragmentEviction(uint32_t modelHash)
{
    evictionQueue.Mark(modelHash);
    return static_cast<int>(EvictionOutcome::Pending);
}

int ReadFragmentEvictionOutcome(uint32_t modelHash) { return static_cast<int>(evictionQueue.Read(modelHash)); }
int CancelFragmentEviction(uint32_t modelHash) { return static_cast<int>(evictionQueue.Cancel(modelHash)); }
