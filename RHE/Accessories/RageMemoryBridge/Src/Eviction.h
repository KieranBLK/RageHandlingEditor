#pragma once
#include <cstdint>

// The outcome of one fragment eviction attempt. A caller of the exports reads these by value, so the numbers never change
// Canceled is only ever returned by the cancel export, never by a read
enum class EvictionOutcome : int
{
    Pending = 0,
    Removed = 1,
    NotLoaded = 2,
    NotReady = 3,
    Refused = 4,
    UnknownModel = 5,
    NoSlot = 6,
    Faulted = 7,
    Unavailable = 8,
    Canceled = 9
};

// Attach path: Finds the streaming functions and the module lookup by pattern, logging one ERROR naming a failed part
// A build that is not verified skips the search, since the layouts the eviction uses are only confirmed on verified builds
void ScanEvictionPatterns(bool buildVerified);

// Script loop: Looks up the fragment module and logs the ready block or one ERROR. Every request reads Unavailable when this or the scan failed
void ResolveEviction();

// Script loop: Takes every request marked since the last frame and tries its eviction
void ProcessEvictionRequests();

// Exports, safe from any thread: A request is only marked here, and the script loop makes the attempt on its next frame
// A request made before the setup finishes waits for it
// A read hands out a final outcome once and forgets the request, so a second read reads Pending until it is requested again
// A cancel drops a waiting request and reads Canceled, reads Pending for one the script loop is running, and reads the real outcome of a finished one
extern "C" __declspec(dllexport) int RequestFragmentEviction(uint32_t modelHash);
extern "C" __declspec(dllexport) int ReadFragmentEvictionOutcome(uint32_t modelHash);
extern "C" __declspec(dllexport) int CancelFragmentEviction(uint32_t modelHash);
