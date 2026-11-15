#include "Hook.h"
#include "GearRatios.h"
#include "Log.h"
#include "Scan.h"
#include <windows.h>
#include <cstring>
#include <vector>

// The reverse ratio (1 / -0.3) stored into the ratio array's first slot, an instruction only the gear ratio function holds
// The exact pattern is tried first, then the loose one with the array offset as a wildcard, since a layout change shifts that byte first
static const char* const ANCHOR_PATTERN = "C7 41 0C 55 55 55 C0";
static const char* const ANCHOR_PATTERN_LOOSE = "C7 41 ?? 55 55 55 C0";

// The store's displacement byte, this far into the anchor, is the ratio array's offset inside the transmission
static const size_t ANCHOR_ARRAY_OFFSET_BYTE = 2;

// The bytes moved into the trampoline, one prologue per edition, and none of their instructions reads the instruction pointer, so they run unchanged there
// The original edition opens with mov rax, rsp then mov [rax + 8], rbx, and Enhanced opens with 6 register pushes (r15, r14, rsi, rdi, rbp, rbx)
// A function that opens with any other bytes is never patched, since moving an unknown instruction could change what it does
static const char LEGACY_PROLOGUE_PATTERN[] = "48 8B C4 48 89 58 08";
static const size_t LEGACY_PROLOGUE_SIZE = 7;
static const char ENHANCED_PROLOGUE_PATTERN[] = "41 57 41 56 56 57 55 53";
static const size_t ENHANCED_PROLOGUE_SIZE = 8;
static const size_t MAX_PROLOGUE_SIZE = 8;

// Each byte of a pattern takes 2 digits and a space, and the terminator replaces the last space, so a pattern's length is 3 characters per byte
// A pattern edited without its size const then fails to compile instead of reading past the checked range
static_assert(sizeof(LEGACY_PROLOGUE_PATTERN) == 3 * LEGACY_PROLOGUE_SIZE, "LEGACY_PROLOGUE_SIZE does not match LEGACY_PROLOGUE_PATTERN");
static_assert(sizeof(ENHANCED_PROLOGUE_PATTERN) == 3 * ENHANCED_PROLOGUE_SIZE, "ENHANCED_PROLOGUE_SIZE does not match ENHANCED_PROLOGUE_PATTERN");
static_assert(LEGACY_PROLOGUE_SIZE <= MAX_PROLOGUE_SIZE && ENHANCED_PROLOGUE_SIZE <= MAX_PROLOGUE_SIZE, "MAX_PROLOGUE_SIZE is smaller than a prologue");

// The stub's trampoline follows its jump and target at TRAMPOLINE_OFFSET: The moved prologue, then an absolute jump back to the first instruction after it
static const size_t TRAMPOLINE_OFFSET = 16;

// The retail fill takes a fifth argument the source does not have. With it set, top gear is 0.8 instead of 0.9 and the progression factor changes
typedef void(__fastcall* CalculateGearRatiosFunction)(void* transmission, float maxVelocity, int gearCount, void* vehicle, bool alternateRatios);

static CalculateGearRatiosFunction originalCalculateGearRatios = nullptr;
static size_t ratioArrayOffset = 0;
static volatile LONG hookActive = 0;

static void __fastcall CalculateGearRatiosHook(void* transmission, float maxVelocity, int gearCount, void* vehicle, bool alternateRatios)
{
    int usedGearCount = ClampGearCount(gearCount);
    originalCalculateGearRatios(transmission, maxVelocity, usedGearCount, vehicle, alternateRatios);
    CorrectGearRatios(static_cast<uint8_t*>(transmission), ratioArrayOffset, usedGearCount, alternateRatios, vehicle);
}

static uintptr_t FindAnchor(const char* exactPattern, const char* loosePattern)
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    std::vector<uintptr_t> matches = ScanModule(exactPattern);
    if (matches.size() == 1)
    {
        LogInfo(Dbg::AnchorFoundExact(matches[0], matches[0] - moduleBase));
        return matches[0];
    }
    std::vector<uintptr_t> looseMatches = ScanModule(loosePattern);
    if (looseMatches.size() == 1)
    {
        LogWarn(Dbg::AnchorFoundLoose(looseMatches[0], looseMatches[0] - moduleBase, matches.size()));
        return looseMatches[0];
    }
    LogError(Dbg::AnchorNotFound(matches.size(), looseMatches.size()));
    return 0;
}

// Writes the bytes over protected code, then puts the protection back and flushes the instruction cache. Returns False when the protection cannot be changed
static bool WriteCode(uint8_t* address, const void* bytes, size_t size)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(address, size, PAGE_EXECUTE_READWRITE, &oldProtect)) return false;
    memcpy(address, bytes, size);
    VirtualProtect(address, size, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), address, size);
    return true;
}

// Writes the stub's absolute jump and its target, which the jump reads through jmp qword ptr [rip + 2]
// The 2 bytes between the jump and its target are padding
static void WriteStubJump(uint8_t* stub, uintptr_t target)
{
    const uint8_t stubJump[] = { 0xFF, 0x25, 0x02, 0x00, 0x00, 0x00 };
    memcpy(stub, stubJump, sizeof(stubJump));
    memcpy(stub + STUB_TARGET_OFFSET, &target, sizeof(target));
}

// The stub page is allocated read and write, so it only becomes executable once everything in it is written
static bool MakeStubExecutable(uint8_t* stub)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(stub, STUB_SIZE, PAGE_EXECUTE_READ, &oldProtect)) return false;
    FlushInstructionCache(GetCurrentProcess(), stub, STUB_SIZE);
    return true;
}

bool PinPluginModule(HMODULE module)
{
    HMODULE pinned = nullptr;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCSTR>(module), &pinned)) return true;
    LogError(Dbg::ModulePinFailed());
    return false;
}

// What the install resolves before it changes anything: The function's entry, how many of its opening bytes move, and the ratio array's offset
struct HookTarget
{
    uintptr_t entry;
    size_t movedSize;
    size_t arrayOffset;
};

static bool ResolveHookTarget(HookTarget& target)
{
    bool enhanced = IsEnhancedEdition();
    const char* prologuePattern = enhanced ? ENHANCED_PROLOGUE_PATTERN : LEGACY_PROLOGUE_PATTERN;
    size_t editionPrologueSize = enhanced ? ENHANCED_PROLOGUE_SIZE : LEGACY_PROLOGUE_SIZE;
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    uintptr_t anchor = FindAnchor(ANCHOR_PATTERN, ANCHOR_PATTERN_LOOSE);
    if (anchor == 0) return false;
    DWORD64 imageBase = 0;
    PRUNTIME_FUNCTION fragment = RtlLookupFunctionEntry(anchor, &imageBase, nullptr);
    if (fragment == nullptr)
    {
        LogError(Dbg::AnchorOutsideFunction(anchor - moduleBase));
        return false;
    }
    uintptr_t entry = static_cast<uintptr_t>(imageBase) + ResolvePrimaryEntry(fragment, static_cast<uintptr_t>(imageBase))->BeginAddress;
    if (!IsReadableRange(entry, editionPrologueSize) || !MatchesPattern(reinterpret_cast<const uint8_t*>(entry), prologuePattern))
    {
        LogError(Dbg::PrologueMismatch(entry - moduleBase));
        return false;
    }
    target.entry = entry;
    target.movedSize = editionPrologueSize;
    target.arrayOffset = *reinterpret_cast<const uint8_t*>(anchor + ANCHOR_ARRAY_OFFSET_BYTE);
    return true;
}

// Returns the sealed stub, or nullptr after logging the failure and releasing its own page
static uint8_t* BuildStub(const HookTarget& target)
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    uintptr_t entryJumpEnd = target.entry + REL32_INSTRUCTION_SIZE;
    uint8_t* stub = AllocateStubNear(moduleBase, entryJumpEnd, STUB_SIZE);
    if (stub == nullptr)
    {
        LogError(Dbg::StubNotAllocated(target.entry - moduleBase));
        return nullptr;
    }
    int64_t distance = static_cast<int64_t>(reinterpret_cast<uintptr_t>(stub)) - static_cast<int64_t>(entryJumpEnd);
    if (distance < INT32_MIN || distance > INT32_MAX)
    {
        LogError(Dbg::StubOutOfReach(reinterpret_cast<uintptr_t>(stub), distance));
        VirtualFree(stub, 0, MEM_RELEASE);
        return nullptr;
    }
    // The trampoline's jump is an absolute jump with displacement 0, so its target follows it directly
    const uint8_t trampolineJump[] = { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    uintptr_t resumeAddress = target.entry + target.movedSize;
    uint8_t* trampoline = stub + TRAMPOLINE_OFFSET;
    WriteStubJump(stub, reinterpret_cast<uintptr_t>(&CalculateGearRatiosHook));
    memcpy(trampoline, reinterpret_cast<const void*>(target.entry), target.movedSize);
    memcpy(trampoline + target.movedSize, trampolineJump, sizeof(trampolineJump));
    memcpy(trampoline + target.movedSize + sizeof(trampolineJump), &resumeAddress, sizeof(resumeAddress));
    if (!MakeStubExecutable(stub))
    {
        LogError(Dbg::StubNotExecutable(reinterpret_cast<uintptr_t>(stub)));
        VirtualFree(stub, 0, MEM_RELEASE);
        return nullptr;
    }
    return stub;
}

// Sets the trampoline and the array offset the hook reads before the entry can jump to it
// A failed write clears both, so no pointer into the stub the caller then frees is left
static bool PatchEntry(const HookTarget& target, uint8_t* stub)
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    int32_t relative = static_cast<int32_t>(reinterpret_cast<uintptr_t>(stub) - (target.entry + REL32_INSTRUCTION_SIZE));
    ratioArrayOffset = target.arrayOffset;
    originalCalculateGearRatios = reinterpret_cast<CalculateGearRatiosFunction>(stub + TRAMPOLINE_OFFSET);
    // The entry becomes a rel32 jump to the stub, and the rest of the moved prologue is filled with no-ops
    uint8_t hookEntryBytes[MAX_PROLOGUE_SIZE] = {};
    memset(hookEntryBytes, NOP_OPCODE, target.movedSize);
    hookEntryBytes[0] = JUMP_OPCODE;
    memcpy(hookEntryBytes + REL32_DISPLACEMENT_OFFSET, &relative, sizeof(relative));
    if (!WriteCode(reinterpret_cast<uint8_t*>(target.entry), hookEntryBytes, target.movedSize))
    {
        LogError(Dbg::EntryUnprotectFailed(target.entry - moduleBase));
        ratioArrayOffset = 0;
        originalCalculateGearRatios = nullptr;
        return false;
    }
    return true;
}

// Runs on the attach path, before the game spawns any vehicle, so no thread is inside the entry while its prologue bytes are rewritten
bool InstallGearRatioHook()
{
    HookTarget target = {};
    if (!ResolveHookTarget(target)) return false;
    uint8_t* stub = BuildStub(target);
    if (stub == nullptr) return false;
    if (!PatchEntry(target, stub))
    {
        VirtualFree(stub, 0, MEM_RELEASE);
        return false;
    }
    InterlockedExchange(&hookActive, 1);
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    LogInfo(Dbg::InstallRoot());
    LogInfo(Dbg::InstallFunction(target.entry, target.entry - moduleBase));
    LogInfo(Dbg::InstallRatioArrayOffset(ratioArrayOffset));
    LogInfo(Dbg::InstallJumpStub(reinterpret_cast<uintptr_t>(stub)));
    return true;
}

int IsGearRatioPatchActive() { return InterlockedCompareExchange(&hookActive, 0, 0) != 0 ? 1 : 0; }
