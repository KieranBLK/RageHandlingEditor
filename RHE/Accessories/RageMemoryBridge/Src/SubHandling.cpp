#include "SubHandling.h"
#include "Log.h"
#include "RequestQueue.h"
#include "Scan.h"
#include <windows.h>
#include <intrin.h>
#include <atomic>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

// The parser instance is a global, loaded by a short function that forwards to a parser method
// Each pattern matches once, and the displacement of the global's load sits at the offset after it
static const char* const PARSER_PATTERN = "8B 51 08 4C 8B C1 48 8B 0D ?? ?? ?? ?? E9";
static const size_t PARSER_DISPLACEMENT_OFFSET = 9;
static const char* const ENHANCED_PARSER_PATTERN = "48 83 EC 38 48 8B 05 ?? ?? ?? ?? 8B 51 08 48 89 4C 24 30";
static const size_t ENHANCED_PARSER_DISPLACEMENT_OFFSET = 7;

// The handling pool's setup code, found by the hash constants it passes. Every other part sits at a fixed distance from it
// The original edition stores the pool pointer in a global, and Enhanced stores it encoded across 3 globals
static const char* const POOL_ANCHOR_PATTERN = "BA 01 46 AA 32 41 B8 01 00 00 00";
static const size_t POOL_STORE_OFFSET = 0x47;
static const size_t ENHANCED_ENCODED_STORE_OFFSET = 0xB7;
static const size_t ENHANCED_KEY_LOAD_OFFSET = 0x91;
static const size_t ENHANCED_VALID_STORE_OFFSET = 0xBE;

// The opcode bytes each anchored instruction must start with, checked before its displacement is read
static const uint8_t POOL_STORE_OPCODE[] = { 0x48, 0x89, 0x3D };
static const uint8_t ENHANCED_ENCODED_STORE_OPCODE[] = { 0x48, 0x89, 0x35 };
static const uint8_t ENHANCED_KEY_LOAD_OPCODE[] = { 0x48, 0x8B, 0x15 };
static const uint8_t ENHANCED_VALID_STORE_OPCODE[] = { 0xC6, 0x05 };

// The valid flag store ends in a 1 byte immediate after its displacement, which the rip-relative target counts past
static const size_t VALID_STORE_IMMEDIATE_SIZE = 1;

// The game's operator new is a tail jump with no unwind entry, so it is checked by its first bytes
// It is the call this far before the anchor
static const size_t NEW_CALL_DISTANCE = 0x14;
static const size_t ENHANCED_NEW_CALL_DISTANCE = 0x18;
static const uint8_t NEW_EXPECTED_BYTES[] = { 0xBA, 0x10, 0x00, 0x00, 0x00 };

// The game's operator delete, found through the callers that free a pointer. The original edition's call lands on a jump to the function
static const char* const DELETE_PATTERN = "74 09 48 8B 49 40 E8 ?? ?? ?? ?? 48 8D 05";
static const size_t DELETE_DISPLACEMENT_OFFSET = 7;
static const char* const ENHANCED_DELETE_PATTERN = "48 39 EF 75 EB 48 89 D9 E8 ?? ?? ?? ?? 45 85 F6";
static const size_t ENHANCED_DELETE_DISPLACEMENT_OFFSET = 9;

// The handling pool's layout: The slot size and the index of the first free slot, with -1 meaning the pool is full
static const size_t POOL_SLOT_SIZE_OFFSET = 0x14;
static const size_t POOL_FIRST_FREE_OFFSET = 0x18;
static const size_t ENHANCED_POOL_SLOT_SIZE_OFFSET = 0x1C;
static const size_t ENHANCED_POOL_FIRST_FREE_OFFSET = 0x20;
static const int32_t POOL_FULL_INDEX = -1;

// A slot size outside this range means the pool layout is not what was read, so nothing is cleared
static const uint32_t POOL_SLOT_SIZE_MINIMUM = 0x40;
static const uint32_t POOL_SLOT_SIZE_MAXIMUM = 0x2000;

// Enhanced's pool pointer decode, the same steps the game's own allocation functions run
// The key is rotated, its low 5 bits plus a base give the second rotation, and the result is inverted
static const int ENCODED_KEY_ROTATION = 0x1F;
static const int ENCODED_HALF_ROTATION = 0x20;
static const uint64_t ENCODED_SHIFT_MASK = 0x1F;
static const int ENCODED_SHIFT_BASE = 4;

// The handling entry's sub-handling array: The element pointer, the count (16 bit), and the capacity (16 bit)
static const size_t SUB_HANDLING_ARRAY_OFFSET = 0x158;
static const size_t ARRAY_COUNT_OFFSET = 0x8;
static const size_t ARRAY_CAPACITY_OFFSET = 0xA;

// A count above this means the array was not read correctly, since a handling entry holds a handful of categories
static const uint16_t ARRAY_COUNT_SANITY_LIMIT = 32;

// The parser's registry of classes by name hash: The map sits inside the parser, as an atMap with its bucket array, slot count, and chained entries
static const size_t PARSER_STRUCTURE_MAP_OFFSET = 0x30;
static const size_t MAP_BUCKETS_OFFSET = 0x0;
static const size_t MAP_SLOT_COUNT_OFFSET = 0x8;
static const size_t MAP_ENTRY_KEY_OFFSET = 0x0;
static const size_t MAP_ENTRY_VALUE_OFFSET = 0x8;
static const size_t MAP_ENTRY_NEXT_OFFSET = 0x10;

// A longer chain means the registry was not read correctly
static const int MAP_CHAIN_LIMIT = 4096;

// A registered class's factory function and placement constructor, each stored in its structure as a delegate: A bound object, then the function
// The game calls the function on its own only while the bound object is null, so a class with one set is never called here
static const size_t STRUCTURE_FACTORY_TARGET_OFFSET = 0x48;
static const size_t STRUCTURE_FACTORY_OFFSET = 0x50;
static const size_t STRUCTURE_CONSTRUCTOR_TARGET_OFFSET = 0x58;
static const size_t STRUCTURE_CONSTRUCTOR_OFFSET = 0x60;

// The vtable slots of a sub-handling object: The deleting destructor, and the function that returns its handling type
static const size_t VTABLE_DESTRUCTOR_SLOT = 0;
static const size_t VTABLE_HANDLING_TYPE_SLOT = 2;
static const unsigned int DESTRUCTOR_DELETE_FLAG = 1;

// Flying and VerticalFlying share one class and differ by this field, which the handling type function returns
static const size_t FLYING_HANDLING_TYPE_OFFSET = 0xC8;

// The handling type each category's object returns
static const int HANDLING_TYPE_BIKE = 0;
static const int HANDLING_TYPE_FLYING = 1;
static const int HANDLING_TYPE_VERTICAL_FLYING = 2;
static const int HANDLING_TYPE_BOAT = 3;
static const int HANDLING_TYPE_SEAPLANE = 4;
static const int HANDLING_TYPE_SUBMARINE = 5;
static const int HANDLING_TYPE_TRAILER = 7;
static const int HANDLING_TYPE_CAR = 8;
static const int HANDLING_TYPE_WEAPON = 9;
static const int HANDLING_TYPE_SPECIAL_FLIGHT = 10;

// A Car object's AdvancedData array uses the same array layout as the handling entry's, at this offset in the object
// Each element is a vtable pointer followed by Slot, Index, and Value, so the stride is 0x18 and the entry's own data starts after the pointer
static const char* const ADVANCED_DATA_CLASS_NAME = "CAdvancedData";
static const size_t ADVANCED_DATA_ARRAY_OFFSET = 0x40;
static const size_t ADVANCED_DATA_ELEMENT_STRIDE = 0x18;
static const size_t ADVANCED_DATA_ENTRY_DATA_OFFSET = 0x8;

// The most entries an array may hold, which a larger request or a larger array read is refused past. The script clamps to the same count
static const int ADVANCED_DATA_ENTRY_LIMIT = 64;

typedef void*(__fastcall* AllocateFunction)(size_t size);
typedef void(__fastcall* FreeFunction)(void* memory);
typedef void*(__fastcall* FactoryFunction)();
typedef void(__fastcall* ConstructorFunction)(void* memory);
typedef int(__fastcall* HandlingTypeFunction)(void* object);
typedef void*(__fastcall* DestructorFunction)(void* object, unsigned int flags);

// The fixed facts of each category. The number a caller sends is the row's position, which the static assert below holds
struct CategoryFacts
{
    SubHandlingCategory number;
    const char* name;
    const char* className;
    int handlingType;
    bool setsHandlingType;
};

static constexpr CategoryFacts CATEGORY_FACTS[] = {
    { SubHandlingCategory::Car, "Car", "CCarHandlingData", HANDLING_TYPE_CAR, false },
    { SubHandlingCategory::Bike, "Bike", "CBikeHandlingData", HANDLING_TYPE_BIKE, false },
    { SubHandlingCategory::Flying, "Flying", "CFlyingHandlingData", HANDLING_TYPE_FLYING, true },
    { SubHandlingCategory::VerticalFlying, "VerticalFlying", "CFlyingHandlingData", HANDLING_TYPE_VERTICAL_FLYING, true },
    { SubHandlingCategory::SpecialFlight, "SpecialFlight", "CSpecialFlightHandlingData", HANDLING_TYPE_SPECIAL_FLIGHT, false },
    { SubHandlingCategory::Boat, "Boat", "CBoatHandlingData", HANDLING_TYPE_BOAT, false },
    { SubHandlingCategory::SeaPlane, "SeaPlane", "CSeaPlaneHandlingData", HANDLING_TYPE_SEAPLANE, false },
    { SubHandlingCategory::Submarine, "Submarine", "CSubmarineHandlingData", HANDLING_TYPE_SUBMARINE, false },
    { SubHandlingCategory::Trailer, "Trailer", "CTrailerHandlingData", HANDLING_TYPE_TRAILER, false },
    { SubHandlingCategory::Weapon, "Weapon", "CVehicleWeaponHandlingData", HANDLING_TYPE_WEAPON, false }
};
static constexpr int CATEGORY_COUNT = static_cast<int>(sizeof(CATEGORY_FACTS) / sizeof(CATEGORY_FACTS[0]));

inline constexpr bool AreCategoryNumbersInOrder()
{
    for (int i = 0; i < CATEGORY_COUNT; i++)
    {
        if (static_cast<int>(CATEGORY_FACTS[i].number) != i) return false;
    }
    return true;
}

static_assert(AreCategoryNumbersInOrder(), "A category's number must equal its position in CATEGORY_FACTS, since a caller indexes the table by it");

// What the setup resolves for each category, in the order of CATEGORY_FACTS
struct CategoryState
{
    bool available;
    uint8_t* structure;
    void* factory;
    void* constructor;
};

static CategoryState categoryStates[CATEGORY_COUNT] = {};

// A request's key is the handling or Car object's address and a selector: The category, or the capacity asked for
typedef std::pair<uintptr_t, int> RequestKey;
typedef RequestQueue<RequestKey, SubHandlingOutcome> RequestMap;

// The exports mark requests from a caller's thread and the script loop takes them, which each queue guards with its own mutex
static RequestMap creationRequests(SubHandlingOutcome::Pending, SubHandlingOutcome::Canceled);
static RequestMap deletionRequests(SubHandlingOutcome::Pending, SubHandlingOutcome::Canceled);
static RequestMap advancedDataRequests(SubHandlingOutcome::Pending, SubHandlingOutcome::Canceled);

// The setup's state is written once by the script loop and may be read from any thread, so it is atomic and needs no lock of its own
static std::atomic<bool> resolveFinished(false);
static std::atomic<bool> resolved(false);

// The scans write the pool, allocator, and parser global once on the attach path and ResolveSubHandling the rest, all before any request is taken
// They are only read afterward
static bool patternsFound = false;
static uintptr_t parserGlobal = 0;
static bool enhancedPool = false;
static uintptr_t poolGlobal = 0;
static uintptr_t encodedPoolGlobal = 0;
static uintptr_t poolKeyGlobal = 0;
static uintptr_t poolValidGlobal = 0;
static AllocateFunction gameAllocate = nullptr;
static FreeFunction gameFree = nullptr;
static bool advancedDataAvailable = false;
static uintptr_t advancedDataStructure = 0;
static void* advancedDataConstructor = nullptr;

static uint8_t* GuardedReadPointer(uintptr_t address)
{
    __try
    {
        return *reinterpret_cast<uint8_t* const*>(address);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }
}

// The registry keys a class by the hash of its exact name, so unlike a model name hash no letter is lowercased first
static uint32_t HashClassName(const char* name)
{
    uint32_t hash = 0;
    for (const char* character = name; *character != '\0'; character++)
    {
        hash += static_cast<uint8_t>(*character);
        hash += hash << 10;
        hash ^= hash >> 6;
    }
    hash += hash << 3;
    hash ^= hash >> 11;
    hash += hash << 15;
    return hash;
}

// The registry is keyed by the class name's hash, which picks a bucket by its remainder over the slot count
static uint8_t* GuardedFindStructure(uint8_t* parser, uint32_t hash)
{
    __try
    {
        const uint8_t* map = parser + PARSER_STRUCTURE_MAP_OFFSET;
        uint8_t* const* buckets = *reinterpret_cast<uint8_t* const* const*>(map + MAP_BUCKETS_OFFSET);
        uint16_t slotCount = *reinterpret_cast<const uint16_t*>(map + MAP_SLOT_COUNT_OFFSET);
        if (buckets == nullptr || slotCount == 0) return nullptr;
        const uint8_t* entry = buckets[hash % slotCount];
        for (int steps = 0; entry != nullptr && steps < MAP_CHAIN_LIMIT; steps++)
        {
            if (*reinterpret_cast<const uint32_t*>(entry + MAP_ENTRY_KEY_OFFSET) == hash) return *reinterpret_cast<uint8_t* const*>(entry + MAP_ENTRY_VALUE_OFFSET);
            entry = *reinterpret_cast<const uint8_t* const*>(entry + MAP_ENTRY_NEXT_OFFSET);
        }
        return nullptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }
}

static bool GuardedReadClassFunctions(const uint8_t* structure, void** factory, void** constructor, bool* boundToObject)
{
    __try
    {
        *factory = *reinterpret_cast<void* const*>(structure + STRUCTURE_FACTORY_OFFSET);
        *constructor = *reinterpret_cast<void* const*>(structure + STRUCTURE_CONSTRUCTOR_OFFSET);
        *boundToObject = *reinterpret_cast<void* const*>(structure + STRUCTURE_FACTORY_TARGET_OFFSET) != nullptr || *reinterpret_cast<void* const*>(structure + STRUCTURE_CONSTRUCTOR_TARGET_OFFSET) != nullptr;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// Reads the game's own pool pointer: Directly on the original edition, decoded from 3 globals on Enhanced
// Called only inside a guarded attempt, so a bad global faults into Faulted
static uint8_t* DecodePool()
{
    if (!enhancedPool) return *reinterpret_cast<uint8_t* const*>(poolGlobal);
    if ((*reinterpret_cast<const volatile uint8_t*>(poolValidGlobal) & 1) == 0) return nullptr;
    uint64_t key = _rotl64(*reinterpret_cast<const uint64_t*>(poolKeyGlobal), ENCODED_KEY_ROTATION);
    uint64_t value = *reinterpret_cast<const uint64_t*>(encodedPoolGlobal) ^ key;
    value = _rotl64(value, ENCODED_HALF_ROTATION);
    value = _rotl64(value, static_cast<int>((key & ENCODED_SHIFT_MASK) + ENCODED_SHIFT_BASE));
    return reinterpret_cast<uint8_t*>(~value);
}

static bool ResolveAllocator(uintptr_t anchor, std::string& failure)
{
    uintptr_t callAddress = anchor - (enhancedPool ? ENHANCED_NEW_CALL_DISTANCE : NEW_CALL_DISTANCE);
    if (!GuardedBytesEqual(callAddress, &CALL_OPCODE, 1))
    {
        failure = Dbg::SubHandlingUnavailable("New", Dbg::SubHandlingBytesMismatch());
        return false;
    }
    uintptr_t target = ReadRelativeTarget(callAddress + REL32_DISPLACEMENT_OFFSET);
    if (!GuardedBytesEqual(target, NEW_EXPECTED_BYTES, sizeof(NEW_EXPECTED_BYTES)))
    {
        failure = Dbg::SubHandlingUnavailable("New", Dbg::SubHandlingNewMismatch());
        return false;
    }
    gameAllocate = reinterpret_cast<AllocateFunction>(target);
    return true;
}

// The original edition's call lands on a jump, and Enhanced's lands on the function itself
static bool ResolveFree(std::string& failure)
{
    std::string partFailure;
    uintptr_t target = ScanAgreeingTarget(enhancedPool ? ENHANCED_DELETE_PATTERN : DELETE_PATTERN, enhancedPool ? ENHANCED_DELETE_DISPLACEMENT_OFFSET : DELETE_DISPLACEMENT_OFFSET, partFailure);
    if (target == 0)
    {
        failure = Dbg::SubHandlingUnavailable("Delete", partFailure);
        return false;
    }
    if (!enhancedPool)
    {
        if (!GuardedBytesEqual(target, &JUMP_OPCODE, 1))
        {
            failure = Dbg::SubHandlingUnavailable("Delete", Dbg::SubHandlingNotJump());
            return false;
        }
        target = ReadRelativeTarget(target + REL32_DISPLACEMENT_OFFSET);
    }
    if (!IsRegisteredFunctionStart(target))
    {
        failure = Dbg::SubHandlingUnavailable("Delete", Dbg::NotFunctionStart());
        return false;
    }
    gameFree = reinterpret_cast<FreeFunction>(target);
    return true;
}

static bool ResolvePool(uintptr_t anchor, std::string& failure)
{
    if (!enhancedPool)
    {
        uintptr_t store = anchor + POOL_STORE_OFFSET;
        if (!GuardedBytesEqual(store, POOL_STORE_OPCODE, sizeof(POOL_STORE_OPCODE)))
        {
            failure = Dbg::SubHandlingUnavailable("Pool", Dbg::SubHandlingBytesMismatch());
            return false;
        }
        poolGlobal = ReadRelativeTarget(store + sizeof(POOL_STORE_OPCODE));
        if (poolGlobal == 0)
        {
            failure = Dbg::SubHandlingUnavailable("Pool", Dbg::DisplacementUnreadable());
            return false;
        }
        return true;
    }
    uintptr_t encodedStore = anchor + ENHANCED_ENCODED_STORE_OFFSET;
    uintptr_t keyLoad = anchor + ENHANCED_KEY_LOAD_OFFSET;
    uintptr_t validStore = anchor + ENHANCED_VALID_STORE_OFFSET;
    if (!GuardedBytesEqual(encodedStore, ENHANCED_ENCODED_STORE_OPCODE, sizeof(ENHANCED_ENCODED_STORE_OPCODE)) ||
        !GuardedBytesEqual(keyLoad, ENHANCED_KEY_LOAD_OPCODE, sizeof(ENHANCED_KEY_LOAD_OPCODE)) ||
        !GuardedBytesEqual(validStore, ENHANCED_VALID_STORE_OPCODE, sizeof(ENHANCED_VALID_STORE_OPCODE)))
    {
        failure = Dbg::SubHandlingUnavailable("Pool", Dbg::SubHandlingBytesMismatch());
        return false;
    }
    encodedPoolGlobal = ReadRelativeTarget(encodedStore + sizeof(ENHANCED_ENCODED_STORE_OPCODE));
    poolKeyGlobal = ReadRelativeTarget(keyLoad + sizeof(ENHANCED_KEY_LOAD_OPCODE));
    uintptr_t validTarget = ReadRelativeTarget(validStore + sizeof(ENHANCED_VALID_STORE_OPCODE));
    if (encodedPoolGlobal == 0 || poolKeyGlobal == 0 || validTarget == 0)
    {
        failure = Dbg::SubHandlingUnavailable("Pool", Dbg::DisplacementUnreadable());
        return false;
    }
    poolValidGlobal = validTarget + VALID_STORE_IMMEDIATE_SIZE;
    return true;
}

// Found the same way as a category's class and called as a plain function, but only its placement constructor is used
// The constructor is a leaf with no unwind entry, so the attempt checks it through the vtable comparison
static bool ResolveAdvancedData(uint8_t* parser, std::string& failure)
{
    uint8_t* structure = GuardedFindStructure(parser, HashClassName(ADVANCED_DATA_CLASS_NAME));
    if (structure == nullptr)
    {
        failure = Dbg::SubHandlingStructureMissing();
        return false;
    }
    void* factory = nullptr;
    void* constructor = nullptr;
    bool boundToObject = false;
    if (!GuardedReadClassFunctions(structure, &factory, &constructor, &boundToObject))
    {
        failure = Dbg::SubHandlingClassUnreadable();
        return false;
    }
    if (boundToObject)
    {
        failure = Dbg::SubHandlingDelegateBound();
        return false;
    }
    if (constructor == nullptr)
    {
        failure = Dbg::SubHandlingConstructorMissing();
        return false;
    }
    advancedDataStructure = reinterpret_cast<uintptr_t>(structure);
    advancedDataConstructor = constructor;
    return true;
}

// Resolves one category's class, factory, and placement constructor. Returns an empty string once the category is available, otherwise the failure line
static std::string ResolveCategory(uint8_t* parser, int index)
{
    const CategoryFacts& facts = CATEGORY_FACTS[index];
    CategoryState& state = categoryStates[index];
    state.structure = GuardedFindStructure(parser, HashClassName(facts.className));
    if (state.structure == nullptr) return Dbg::SubHandlingCategoryUnavailable(facts.name, Dbg::SubHandlingStructureMissing());
    bool boundToObject = false;
    bool readable = GuardedReadClassFunctions(state.structure, &state.factory, &state.constructor, &boundToObject);
    if (!readable || !IsRegisteredFunctionStart(reinterpret_cast<uintptr_t>(state.factory))) return Dbg::SubHandlingCategoryUnavailable(facts.name, Dbg::NotFunctionStart());
    if (boundToObject) return Dbg::SubHandlingCategoryUnavailable(facts.name, Dbg::SubHandlingDelegateBound());
    if (state.constructor == nullptr) return Dbg::SubHandlingCategoryUnavailable(facts.name, Dbg::SubHandlingConstructorMissing());
    state.available = true;
    return std::string();
}

// The ready block: The parser, the pool, the allocator and free functions, each available category, and AdvancedData, then a warning for each part that is not
static void LogSubHandlingReady(uint8_t* parser, const std::vector<std::string>& categoryFailures)
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    LogInfo(Dbg::SubHandlingReadyRoot());
    LogInfo(Dbg::SubHandlingReadyParser(reinterpret_cast<uintptr_t>(parser)));
    LogInfo(Dbg::SubHandlingReadyPart("Pool", (enhancedPool ? encodedPoolGlobal : poolGlobal) - moduleBase));
    LogInfo(Dbg::SubHandlingReadyPart("New", reinterpret_cast<uintptr_t>(gameAllocate) - moduleBase));
    LogInfo(Dbg::SubHandlingReadyPart("Delete", reinterpret_cast<uintptr_t>(gameFree) - moduleBase));
    for (int i = 0; i < CATEGORY_COUNT; i++)
    {
        const CategoryState& state = categoryStates[i];
        if (state.available) LogInfo(Dbg::SubHandlingReadyCategory(CATEGORY_FACTS[i].name, reinterpret_cast<uintptr_t>(state.structure), reinterpret_cast<uintptr_t>(state.factory) - moduleBase));
    }
    if (advancedDataAvailable) LogInfo(Dbg::SubHandlingReadyAdvancedData(advancedDataStructure, reinterpret_cast<uintptr_t>(advancedDataConstructor) - moduleBase));
    for (const std::string& line : categoryFailures) LogWarn(line);
}

static bool ScanParts(std::string& failure)
{
    enhancedPool = IsEnhancedEdition();
    std::string partFailure;
    uintptr_t parserMatch = ScanSingleMatch(enhancedPool ? ENHANCED_PARSER_PATTERN : PARSER_PATTERN, partFailure);
    if (parserMatch == 0)
    {
        failure = Dbg::SubHandlingUnavailable("Parser", partFailure);
        return false;
    }
    parserGlobal = ReadRelativeTarget(parserMatch + (enhancedPool ? ENHANCED_PARSER_DISPLACEMENT_OFFSET : PARSER_DISPLACEMENT_OFFSET));
    uintptr_t anchor = ScanSingleMatch(POOL_ANCHOR_PATTERN, partFailure);
    if (anchor == 0)
    {
        failure = Dbg::SubHandlingUnavailable("Pool", partFailure);
        return false;
    }
    return ResolvePool(anchor, failure) && ResolveAllocator(anchor, failure) && ResolveFree(failure);
}

// The parser instance and its registry are game state, not code, so they are read on the script loop, where the game has finished loading
static bool ResolveParts(std::string& failure)
{
    uint8_t* parser = GuardedReadPointer(parserGlobal);
    if (parser == nullptr)
    {
        failure = Dbg::SubHandlingUnavailable("Parser", Dbg::SubHandlingParserUnreadable());
        return false;
    }
    std::vector<std::string> categoryFailures;
    int availableCount = 0;
    for (int i = 0; i < CATEGORY_COUNT; i++)
    {
        std::string categoryFailure = ResolveCategory(parser, i);
        if (categoryFailure.empty()) availableCount++;
        else categoryFailures.push_back(categoryFailure);
    }
    if (availableCount == 0)
    {
        failure = Dbg::SubHandlingUnavailable("Categories", Dbg::SubHandlingStructureMissing());
        return false;
    }
    std::string advancedDataFailure;
    advancedDataAvailable = ResolveAdvancedData(parser, advancedDataFailure);
    if (!advancedDataAvailable) categoryFailures.push_back(Dbg::SubHandlingCategoryUnavailable("AdvancedData", advancedDataFailure));
    LogSubHandlingReady(parser, categoryFailures);
    return true;
}

void ScanSubHandlingPatterns(bool buildVerified)
{
    if (!buildVerified) return;
    std::string failure;
    patternsFound = ScanParts(failure);
    if (!patternsFound) LogError(failure);
}

void ResolveSubHandling()
{
    bool partsResolved = false;
    if (patternsFound)
    {
        std::string failure;
        partsResolved = ResolveParts(failure);
        if (!partsResolved) LogError(failure);
    }
    resolved = partsResolved;
    resolveFinished = true;
}

// A plugin that could not even pin itself never reaches the script loop, so its setup is marked finished and unresolved here
void AbandonSubHandling()
{
    resolved = false;
    resolveFinished = true;
}

int ReadSubHandlingSetupState()
{
    if (!resolveFinished) return static_cast<int>(SubHandlingSetupState::Pending);
    return static_cast<int>(resolved ? SubHandlingSetupState::Ready : SubHandlingSetupState::Unavailable);
}

// An array's element pointer, count, and capacity, read once so the guarded functions apply the same sanity test
struct ArrayView
{
    uint8_t* elements;
    int count;
    int capacity;
};

// Reads the array at the address. Returns False when its count is above the limit, its capacity is below its count, or a capacity has no element pointer
// Called only inside a guarded block, so a fault in the read is that function's own Faulted
static bool ReadArrayView(uint8_t* array, int countLimit, ArrayView& view)
{
    view.count = *reinterpret_cast<uint16_t*>(array + ARRAY_COUNT_OFFSET);
    view.capacity = *reinterpret_cast<uint16_t*>(array + ARRAY_CAPACITY_OFFSET);
    view.elements = *reinterpret_cast<uint8_t**>(array);
    return view.count <= countLimit && view.capacity >= view.count && (view.capacity == 0 || view.elements != nullptr);
}

// Puts the factory's own vtable back and frees the object through its own destructor, which returns its slot to the game's pool
// Called only inside a guarded block
static void DestroyBuiltObject(void* object, void* recordedVtable)
{
    *reinterpret_cast<void**>(object) = recordedVtable;
    reinterpret_cast<DestructorFunction>((*reinterpret_cast<void***>(object))[VTABLE_DESTRUCTOR_SLOT])(object, DESTRUCTOR_DELETE_FLAG);
}

// Frees game memory in its own guard, so a fault while freeing is dropped and never changes the outcome of the attempt around it
// A leaked buffer is the cost of a fault here, and a caller that already committed its change keeps reporting it
// Holds no C++ objects, since a structured exception frame cannot share a function with them
static void GuardedFree(void* memory)
{
    __try
    {
        gameFree(memory);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return;
    }
}

// Runs an object's deleting destructor in its own guard and returns whether it completed
// Holds no C++ objects, since a structured exception frame cannot share a function with them
static bool GuardedDestroy(void* element, void* destructor)
{
    __try
    {
        reinterpret_cast<DestructorFunction>(destructor)(element, DESTRUCTOR_DELETE_FLAG);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// Every game call of one attempt, guarded so a fault reads Faulted instead of crashing the game
// The object is built and verified before the array grows, and stored in its slot before the count rises, so no reader sees an incomplete object
// Once the array holds the object the attempt reads Created, and the old buffer is freed in its own guard where a fault only leaks it
// A fault inside the placement constructor still leaks that one pool slot, since running a destructor over memory in an unknown state is worse
static SubHandlingOutcome GuardedCreate(uint8_t* handling, int category)
{
    const CategoryFacts& facts = CATEGORY_FACTS[category];
    const CategoryState& state = categoryStates[category];
    __try
    {
        uint8_t* array = handling + SUB_HANDLING_ARRAY_OFFSET;
        ArrayView view;
        if (!ReadArrayView(array, ARRAY_COUNT_SANITY_LIMIT, view)) return SubHandlingOutcome::Faulted;
        void** elements = reinterpret_cast<void**>(view.elements);
        int slotIndex = -1;
        for (int i = 0; i < view.count; i++)
        {
            void* element = elements[i];
            if (element == nullptr)
            {
                if (slotIndex < 0) slotIndex = i;
                continue;
            }
            void** vtable = *reinterpret_cast<void***>(element);
            if (reinterpret_cast<HandlingTypeFunction>(vtable[VTABLE_HANDLING_TYPE_SLOT])(element) == facts.handlingType) return SubHandlingOutcome::AlreadyPresent;
        }
        // The pool is read before the factory runs, since a full pool traps on Enhanced instead of returning null
        uint8_t* pool = DecodePool();
        if (pool == nullptr) return SubHandlingOutcome::Faulted;
        if (*reinterpret_cast<int32_t*>(pool + (enhancedPool ? ENHANCED_POOL_FIRST_FREE_OFFSET : POOL_FIRST_FREE_OFFSET)) == POOL_FULL_INDEX) return SubHandlingOutcome::PoolFull;
        uint32_t slotSize = *reinterpret_cast<uint32_t*>(pool + (enhancedPool ? ENHANCED_POOL_SLOT_SIZE_OFFSET : POOL_SLOT_SIZE_OFFSET));
        if (slotSize < POOL_SLOT_SIZE_MINIMUM || slotSize > POOL_SLOT_SIZE_MAXIMUM) return SubHandlingOutcome::Faulted;
        // The factory leaves some fields unset and a plain clear afterward would wipe members with their own vtables
        // So the slot is cleared first, and the class's placement constructor runs over the clear memory
        void* object = reinterpret_cast<FactoryFunction>(state.factory)();
        if (object == nullptr) return SubHandlingOutcome::Faulted;
        void* recordedVtable = *reinterpret_cast<void**>(object);
        memset(object, 0, slotSize);
        reinterpret_cast<ConstructorFunction>(state.constructor)(object);
        if (facts.setsHandlingType) *reinterpret_cast<int32_t*>(static_cast<uint8_t*>(object) + FLYING_HANDLING_TYPE_OFFSET) = facts.handlingType;
        void** vtable = *reinterpret_cast<void***>(object);
        if (static_cast<void*>(vtable) != recordedVtable || reinterpret_cast<HandlingTypeFunction>(vtable[VTABLE_HANDLING_TYPE_SLOT])(object) != facts.handlingType)
        {
            // The constructor did not rebuild the object the factory made, so the array is left as it was
            DestroyBuiltObject(object, recordedVtable);
            return SubHandlingOutcome::Faulted;
        }
        if (slotIndex >= 0)
        {
            elements[slotIndex] = object;
            return SubHandlingOutcome::Created;
        }
        // No empty slot, so the array grows by one. Spare capacity is used first, otherwise the game's allocator makes a larger buffer
        slotIndex = view.count;
        void** oldElements = nullptr;
        if (view.count < view.capacity) elements[slotIndex] = object;
        else
        {
            void** buffer = static_cast<void**>(gameAllocate(static_cast<size_t>(view.count + 1) * sizeof(void*)));
            if (buffer == nullptr)
            {
                DestroyBuiltObject(object, recordedVtable);
                return SubHandlingOutcome::Faulted;
            }
            for (int i = 0; i < view.count; i++) buffer[i] = elements[i];
            buffer[slotIndex] = object;
            *reinterpret_cast<void***>(array) = buffer;
            *reinterpret_cast<uint16_t*>(array + ARRAY_CAPACITY_OFFSET) = static_cast<uint16_t>(view.count + 1);
            oldElements = elements;
        }
        *reinterpret_cast<uint16_t*>(array + ARRAY_COUNT_OFFSET) = static_cast<uint16_t>(view.count + 1);
        if (oldElements != nullptr) GuardedFree(static_cast<void*>(oldElements));
        return SubHandlingOutcome::Created;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return SubHandlingOutcome::Faulted;
    }
}

// The object's own deleting destructor returns its slot to the game's pool, so nothing else is freed here
// The array slot is emptied first, so no reader is handed an object that is being destroyed, and the count stays as it was like a stock entry's empty slot
// The category is gone once the slot is empty, so a faulting destructor still reads Deleted and sets destructorFaulted for the caller to log
static SubHandlingOutcome GuardedDelete(uint8_t* handling, int category, bool* destructorFaulted)
{
    const CategoryFacts& facts = CATEGORY_FACTS[category];
    __try
    {
        ArrayView view;
        if (!ReadArrayView(handling + SUB_HANDLING_ARRAY_OFFSET, ARRAY_COUNT_SANITY_LIMIT, view)) return SubHandlingOutcome::Faulted;
        void** elements = reinterpret_cast<void**>(view.elements);
        for (int i = 0; i < view.count; i++)
        {
            void* element = elements[i];
            if (element == nullptr) continue;
            void** vtable = *reinterpret_cast<void***>(element);
            if (reinterpret_cast<HandlingTypeFunction>(vtable[VTABLE_HANDLING_TYPE_SLOT])(element) != facts.handlingType) continue;
            void* destructor = vtable[VTABLE_DESTRUCTOR_SLOT];
            elements[i] = nullptr;
            *destructorFaulted = !GuardedDestroy(element, destructor);
            return SubHandlingOutcome::Deleted;
        }
        return SubHandlingOutcome::NotPresent;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return SubHandlingOutcome::Faulted;
    }
}

// Grows a Car object's AdvancedData array to hold at least the requested number of entries, leaving its count as it was
// The array's storage is plain bytes the game constructs in place, so a new buffer is cleared and every element is constructed over it
// The pointer is stored before the capacity, so a reader never sees a capacity the buffer does not hold, and the old buffer is freed last in its own guard
// A fault before the swap frees the new buffer, and one after it only leaks the old one, since the array holds the new one by then
static SubHandlingOutcome GuardedGrowAdvancedData(uint8_t* carData, int requested)
{
    uint8_t* volatile newBuffer = nullptr;
    volatile bool pointerSwapped = false;
    __try
    {
        uint8_t* array = carData + ADVANCED_DATA_ARRAY_OFFSET;
        ArrayView view;
        if (!ReadArrayView(array, ADVANCED_DATA_ENTRY_LIMIT, view) || view.capacity > ADVANCED_DATA_ENTRY_LIMIT) return SubHandlingOutcome::Faulted;
        if (view.capacity >= requested) return SubHandlingOutcome::AlreadyPresent;
        size_t bytes = static_cast<size_t>(requested) * ADVANCED_DATA_ELEMENT_STRIDE;
        uint8_t* buffer = static_cast<uint8_t*>(gameAllocate(bytes));
        if (buffer == nullptr) return SubHandlingOutcome::Faulted;
        newBuffer = buffer;
        memset(buffer, 0, bytes);
        // The game constructs every element up to the capacity, not only the counted ones, so any existing element carries the vtable a new one must match
        void* expectedVtable = view.capacity > 0 ? *reinterpret_cast<void**>(view.elements) : nullptr;
        bool vtablesMatch = true;
        for (int i = 0; i < requested; i++)
        {
            uint8_t* element = buffer + static_cast<size_t>(i) * ADVANCED_DATA_ELEMENT_STRIDE;
            reinterpret_cast<ConstructorFunction>(advancedDataConstructor)(element);
            void* vtable = *reinterpret_cast<void**>(element);
            if (expectedVtable == nullptr) expectedVtable = vtable;
            if (vtable == nullptr || vtable != expectedVtable) vtablesMatch = false;
        }
        if (!vtablesMatch)
        {
            newBuffer = nullptr;
            GuardedFree(buffer);
            return SubHandlingOutcome::Faulted;
        }
        for (int i = 0; i < view.count; i++) memcpy(buffer + static_cast<size_t>(i) * ADVANCED_DATA_ELEMENT_STRIDE + ADVANCED_DATA_ENTRY_DATA_OFFSET, view.elements + static_cast<size_t>(i) * ADVANCED_DATA_ELEMENT_STRIDE + ADVANCED_DATA_ENTRY_DATA_OFFSET, ADVANCED_DATA_ELEMENT_STRIDE - ADVANCED_DATA_ENTRY_DATA_OFFSET);
        *reinterpret_cast<uint8_t**>(array) = buffer;
        pointerSwapped = true;
        *reinterpret_cast<uint16_t*>(array + ARRAY_CAPACITY_OFFSET) = static_cast<uint16_t>(requested);
        if (view.elements != nullptr) GuardedFree(view.elements);
        return SubHandlingOutcome::Created;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (newBuffer != nullptr && !pointerSwapped) GuardedFree(newBuffer);
        return SubHandlingOutcome::Faulted;
    }
}

static std::string OutcomeName(SubHandlingOutcome outcome)
{
    switch (outcome)
    {
        case SubHandlingOutcome::Pending: return "Pending";
        case SubHandlingOutcome::Created: return "Created";
        case SubHandlingOutcome::AlreadyPresent: return "AlreadyPresent";
        case SubHandlingOutcome::PoolFull: return "PoolFull";
        case SubHandlingOutcome::Faulted: return "Faulted";
        case SubHandlingOutcome::Unavailable: return "Unavailable";
        case SubHandlingOutcome::Deleted: return "Deleted";
        case SubHandlingOutcome::NotPresent: return "NotPresent";
        case SubHandlingOutcome::Canceled: return "Canceled";
    }
    return "Unknown";
}

// A request's second key is the category for a creation or deletion, and the requested capacity for an AdvancedData growth
// The attempt returns Unavailable itself when the setup failed or the key is out of range
typedef SubHandlingOutcome (*AttemptFunction)(uint8_t* address, int selector);
typedef std::string (*DescribeFunction)(uintptr_t address, int selector, const std::string& outcomeName);

static SubHandlingOutcome AttemptCreation(uint8_t* handling, int category)
{
    return resolved && categoryStates[category].available ? GuardedCreate(handling, category) : SubHandlingOutcome::Unavailable;
}

static SubHandlingOutcome AttemptDeletion(uint8_t* handling, int category)
{
    if (!resolved || !categoryStates[category].available) return SubHandlingOutcome::Unavailable;
    bool destructorFaulted = false;
    SubHandlingOutcome outcome = GuardedDelete(handling, category, &destructorFaulted);
    if (destructorFaulted) LogWarn(Dbg::SubHandlingDestructorFaulted(reinterpret_cast<uintptr_t>(handling), CATEGORY_FACTS[category].name));
    return outcome;
}

static SubHandlingOutcome AttemptAdvancedDataGrowth(uint8_t* carData, int capacity)
{
    return resolved && advancedDataAvailable && capacity >= 1 && capacity <= ADVANCED_DATA_ENTRY_LIMIT ? GuardedGrowAdvancedData(carData, capacity) : SubHandlingOutcome::Unavailable;
}

static std::string DescribeCreation(uintptr_t handling, int category, const std::string& outcomeName) { return Dbg::SubHandlingOutcomeLine(handling, CATEGORY_FACTS[category].name, outcomeName); }
static std::string DescribeDeletion(uintptr_t handling, int category, const std::string& outcomeName) { return Dbg::SubHandlingDeletionOutcomeLine(handling, CATEGORY_FACTS[category].name, outcomeName); }
static std::string DescribeAdvancedDataGrowth(uintptr_t carData, int capacity, const std::string& outcomeName) { return Dbg::SubHandlingAdvancedDataOutcomeLine(carData, capacity, outcomeName); }

static void LogOutcome(const std::string& line, SubHandlingOutcome outcome)
{
    if (outcome == SubHandlingOutcome::Faulted) LogError(line);
    else if (outcome == SubHandlingOutcome::PoolFull) LogWarn(line);
    else LogInfo(line);
}

// Takes every request marked in one queue and runs its attempt, logging every attempt's outcome
static void ProcessRequestMap(RequestMap& queue, AttemptFunction attempt, DescribeFunction describe)
{
    for (const RequestKey& key : queue.TakeMarked())
    {
        SubHandlingOutcome outcome = attempt(reinterpret_cast<uint8_t*>(key.first), key.second);
        queue.Finish(key, outcome);
        LogOutcome(describe(key.first, key.second, OutcomeName(outcome)), outcome);
    }
}

void ProcessSubHandlingRequests()
{
    if (!resolveFinished) return;
    // A growth runs before a deletion, so one in the same frame as its Car's deletion grows the live object instead of one the deletion destroyed
    ProcessRequestMap(creationRequests, AttemptCreation, DescribeCreation);
    ProcessRequestMap(advancedDataRequests, AttemptAdvancedDataGrowth, DescribeAdvancedDataGrowth);
    ProcessRequestMap(deletionRequests, AttemptDeletion, DescribeDeletion);
}

static int MarkRequest(RequestMap& queue, uintptr_t address, int selector, bool selectorValid)
{
    if (address == 0 || !selectorValid) return static_cast<int>(SubHandlingOutcome::Unavailable);
    queue.Mark({ address, selector });
    return static_cast<int>(SubHandlingOutcome::Pending);
}

static int ReadRequestOutcome(RequestMap& queue, uintptr_t address, int selector) { return static_cast<int>(queue.Read({ address, selector })); }
static int CancelRequest(RequestMap& queue, uintptr_t address, int selector) { return static_cast<int>(queue.Cancel({ address, selector })); }

static bool IsCategoryNumberValid(int category) { return category >= 0 && category < CATEGORY_COUNT; }

int RequestSubHandlingCreation(uintptr_t handlingAddress, int category) { return MarkRequest(creationRequests, handlingAddress, category, IsCategoryNumberValid(category)); }
int ReadSubHandlingCreationOutcome(uintptr_t handlingAddress, int category) { return ReadRequestOutcome(creationRequests, handlingAddress, category); }
int CancelSubHandlingCreation(uintptr_t handlingAddress, int category) { return CancelRequest(creationRequests, handlingAddress, category); }
int RequestSubHandlingDeletion(uintptr_t handlingAddress, int category) { return MarkRequest(deletionRequests, handlingAddress, category, IsCategoryNumberValid(category)); }
int ReadSubHandlingDeletionOutcome(uintptr_t handlingAddress, int category) { return ReadRequestOutcome(deletionRequests, handlingAddress, category); }
int CancelSubHandlingDeletion(uintptr_t handlingAddress, int category) { return CancelRequest(deletionRequests, handlingAddress, category); }
int RequestAdvancedDataCapacity(uintptr_t carDataAddress, int capacity) { return MarkRequest(advancedDataRequests, carDataAddress, capacity, true); }
int ReadAdvancedDataCapacityOutcome(uintptr_t carDataAddress, int capacity) { return ReadRequestOutcome(advancedDataRequests, carDataAddress, capacity); }
int CancelAdvancedDataCapacity(uintptr_t carDataAddress, int capacity) { return CancelRequest(advancedDataRequests, carDataAddress, capacity); }
