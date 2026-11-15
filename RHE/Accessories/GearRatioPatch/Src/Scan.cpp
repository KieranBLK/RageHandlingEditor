#include "Scan.h"
#include <windows.h>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <string>

// Unwind info chains a split function's fragments to its primary entry, and a broken table could loop, so the walk stops after this many links
static const int MAX_CHAIN_DEPTH = 32;

// Every protection value that allows a read. Execute-only pages are left out, since comparing bytes on one would fault
static const DWORD READABLE_PROTECTIONS = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

struct PatternByte
{
    uint8_t value;
    bool wildcard;
};

struct SectionRange
{
    uintptr_t start;
    uintptr_t end;
};

static std::vector<PatternByte> ParsePattern(const char* pattern)
{
    std::vector<PatternByte> bytes;
    const char* cursor = pattern;
    while (*cursor != '\0')
    {
        if (*cursor == ' ')
        {
            cursor++;
            continue;
        }
        if (*cursor == '?')
        {
            bytes.push_back({ 0, true });
            while (*cursor == '?') cursor++;
            continue;
        }
        bytes.push_back({ static_cast<uint8_t>(strtoul(std::string(cursor, 2).c_str(), nullptr, 16)), false });
        cursor += 2;
    }
    return bytes;
}

static std::vector<SectionRange> GetExecutableSections()
{
    std::vector<SectionRange> ranges;
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    auto dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(moduleBase);
    auto ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS64*>(moduleBase + dosHeader->e_lfanew);
    IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(ntHeaders);
    for (WORD i = 0; i < ntHeaders->FileHeader.NumberOfSections; i++, section++)
    {
        if ((section->Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) continue;
        uintptr_t sectionStart = moduleBase + section->VirtualAddress;
        ranges.push_back({ sectionStart, sectionStart + section->Misc.VirtualSize });
    }
    return ranges;
}

static bool IsReadableRegion(const MEMORY_BASIC_INFORMATION& region)
{
    return region.State == MEM_COMMIT && (region.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0 && (region.Protect & READABLE_PROTECTIONS) != 0;
}

bool IsReadableRange(uintptr_t start, size_t size)
{
    uintptr_t end = start + size;
    for (uintptr_t cursor = start; cursor < end;)
    {
        MEMORY_BASIC_INFORMATION region;
        if (VirtualQuery(reinterpret_cast<void*>(cursor), &region, sizeof(region)) == 0 || !IsReadableRegion(region)) return false;
        cursor = reinterpret_cast<uintptr_t>(region.BaseAddress) + region.RegionSize;
    }
    return true;
}

// Visits only committed readable memory, each region clipped to the range, so a guard or reserved page is never touched
static void ForEachReadableRegion(const SectionRange& range, const std::function<void(uintptr_t start, uintptr_t end)>& visit)
{
    uintptr_t regionStart = range.start;
    while (regionStart < range.end)
    {
        MEMORY_BASIC_INFORMATION region;
        if (VirtualQuery(reinterpret_cast<void*>(regionStart), &region, sizeof(region)) == 0) break;
        uintptr_t regionEnd = reinterpret_cast<uintptr_t>(region.BaseAddress) + region.RegionSize;
        if (regionEnd > range.end) regionEnd = range.end;
        if (IsReadableRegion(region)) visit(regionStart, regionEnd);
        regionStart = regionEnd;
    }
}

std::vector<uintptr_t> ScanModule(const char* pattern)
{
    std::vector<uintptr_t> matches;
    std::vector<PatternByte> bytes = ParsePattern(pattern);
    for (const SectionRange& section : GetExecutableSections())
    {
        ForEachReadableRegion(section, [&](uintptr_t regionStart, uintptr_t regionEnd) {
            if (regionEnd - regionStart < bytes.size()) return;
            const uint8_t* memory = reinterpret_cast<const uint8_t*>(regionStart);
            size_t lastStart = regionEnd - regionStart - bytes.size();
            for (size_t offset = 0; offset <= lastStart; offset++)
            {
                size_t j = 0;
                while (j < bytes.size() && (bytes[j].wildcard || memory[offset + j] == bytes[j].value)) j++;
                if (j == bytes.size()) matches.push_back(regionStart + offset);
            }
        });
    }
    return matches;
}

bool MatchesPattern(const uint8_t* memory, const char* pattern)
{
    std::vector<PatternByte> bytes = ParsePattern(pattern);
    for (size_t i = 0; i < bytes.size(); i++)
    {
        if (!bytes[i].wildcard && memory[i] != bytes[i].value) return false;
    }
    return true;
}

uint8_t* AllocateStubNear(uintptr_t searchStart, uintptr_t reachFrom, size_t size)
{
    uintptr_t lowest = reachFrom > STUB_SEARCH_RANGE ? reachFrom - STUB_SEARCH_RANGE : 0;
    for (uintptr_t address = (searchStart & ~(STUB_SEARCH_STEP - 1)) - STUB_SEARCH_STEP; address > lowest; address -= STUB_SEARCH_STEP)
    {
        void* stub = VirtualAlloc(reinterpret_cast<void*>(address), size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (stub != nullptr) return static_cast<uint8_t*>(stub);
    }
    return nullptr;
}

PRUNTIME_FUNCTION ResolvePrimaryEntry(PRUNTIME_FUNCTION entry, uintptr_t imageBase)
{
    for (int depth = 0; depth < MAX_CHAIN_DEPTH; depth++)
    {
        const uint8_t* unwindInfo = reinterpret_cast<const uint8_t*>(imageBase + entry->UnwindData);
        if (((unwindInfo[0] >> 3) & UNW_FLAG_CHAININFO) == 0) return entry;
        // The chained entry follows the unwind codes, whose count is padded to an even number of 2 byte slots
        uint8_t codeCount = unwindInfo[2];
        entry = reinterpret_cast<PRUNTIME_FUNCTION>(const_cast<uint8_t*>(unwindInfo + 4 + static_cast<size_t>((codeCount + 1) & ~1) * 2));
    }
    return entry;
}
