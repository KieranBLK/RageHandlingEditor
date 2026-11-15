#pragma once
#include <windows.h>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

// A rel32 call or jump is one opcode byte followed by a 4 byte displacement counted from the end of the instruction
static const size_t REL32_INSTRUCTION_SIZE = 5;
static const size_t REL32_DISPLACEMENT_OFFSET = 1;

// The opcode bytes of a rel32 call and a rel32 jump, and the one byte no-op that pads moved code
static const uint8_t CALL_OPCODE = 0xE8;
static const uint8_t JUMP_OPCODE = 0xE9;
static const uint8_t NOP_OPCODE = 0x90;

// A stub is one page holding an absolute jump through the 8 byte target at STUB_TARGET_OFFSET
// Stubs are searched downward from the module base in allocation sized steps, never further than a rel32 can reach
static const size_t STUB_SIZE = 0x1000;
static const size_t STUB_TARGET_OFFSET = 8;
static const uintptr_t STUB_SEARCH_RANGE = 0x7FF00000;
static const uintptr_t STUB_SEARCH_STEP = 0x10000;

// True only while every byte of the range sits in committed memory the process may read
bool IsReadableRange(uintptr_t start, size_t size);

// Every address in the game module's executable sections where the pattern matches, with ?? as a wildcard byte
std::vector<uintptr_t> ScanModule(const char* pattern);

// True when the bytes at the address match the pattern. The caller confirms the range is readable first
bool MatchesPattern(const uint8_t* memory, const char* pattern);

// Searches downward from searchStart for free memory that a rel32 jump ending at reachFrom can still reach
// Returns nullptr when none is found, and the caller still checks the exact distance before writing any jump
uint8_t* AllocateStubNear(uintptr_t searchStart, uintptr_t reachFrom, size_t size);

// The address a 4 byte rip-relative displacement points at, counted from the end of those 4 bytes
// Returns 0 when the displacement cannot be read, which no caller accepts as a target
uintptr_t ReadRelativeTarget(uintptr_t displacementAddress);

// A pattern inside many callers can match several times, so every match's rip-relative target is read and accepted only when all of them agree
// Returns 0 and the reason when nothing matched, a target could not be read, or the targets differ
uintptr_t ScanAgreeingTarget(const char* pattern, size_t displacementOffset, std::string& failure);

// The address of the pattern's only match. Returns 0 and the reason when it matched any other number of times
uintptr_t ScanSingleMatch(const char* pattern, std::string& failure);

// True only for the start of a function registered in the game module's unwind table
bool IsRegisteredFunctionStart(uintptr_t target);

// True when the bytes at the address equal the expected bytes. Reads through a fault guard, so an unreadable address reads False
bool GuardedBytesEqual(uintptr_t address, const uint8_t* expected, size_t count);
