#pragma once
#include <cstdint>

// The sub-handling categories a handling entry can hold. A caller of the exports reads these by value, so the numbers never change
enum class SubHandlingCategory : int
{
    Car = 0,
    Bike = 1,
    Flying = 2,
    VerticalFlying = 3,
    SpecialFlight = 4,
    Boat = 5,
    SeaPlane = 6,
    Submarine = 7,
    Trailer = 8,
    Weapon = 9
};

// The outcome of one creation or deletion attempt. A caller of the exports reads these by value, so the numbers never change
// A creation reads Created, AlreadyPresent, or PoolFull, and a deletion reads Deleted or NotPresent. The rest serve both
// Canceled is only ever returned by a cancel export, never by a read
enum class SubHandlingOutcome : int
{
    Pending = 0,
    Created = 1,
    AlreadyPresent = 2,
    PoolFull = 3,
    Faulted = 4,
    Unavailable = 5,
    Deleted = 6,
    NotPresent = 7,
    Canceled = 8
};

// The setup's state as an export reads it. A caller of the exports reads these by value, so the numbers never change
// Pending until the script loop's first pass finishes the setup, then Ready when every request can be attempted or Unavailable when the setup failed
enum class SubHandlingSetupState : int
{
    Pending = 0,
    Ready = 1,
    Unavailable = 2
};

// Attach path: Finds the parser global, the handling pool, and the game's allocator and free functions by pattern, logging one ERROR naming a failed part
// A build that is not verified skips the search, since the layouts used here are only confirmed on verified builds
void ScanSubHandlingPatterns(bool buildVerified);

// Script loop: Reads the parser and resolves each category and AdvancedData from its registry, logging the result
// Every request reads Unavailable when this or the scan failed
void ResolveSubHandling();

// Attach path: Marks the setup finished and unresolved when the plugin cannot start, since no script loop will ever finish it
void AbandonSubHandling();

// Script loop: Takes every creation, AdvancedData growth, and deletion request marked since the last frame and tries it, in that order
void ProcessSubHandlingRequests();

// Export, safe from any thread: Returns a SubHandlingSetupState, so a caller can tell a plugin whose setup failed from one still starting
extern "C" __declspec(dllexport) int ReadSubHandlingSetupState();

// Exports, safe from any thread: A request is only marked here, and the script loop makes the attempt on its next frame
// Creation and deletion keep separate requests and outcomes, each keyed by handling address and category
// A read hands out a final outcome once and forgets the request, so a second read of the same key reads Pending until it is requested again
extern "C" __declspec(dllexport) int RequestSubHandlingCreation(uintptr_t handlingAddress, int category);
extern "C" __declspec(dllexport) int ReadSubHandlingCreationOutcome(uintptr_t handlingAddress, int category);
extern "C" __declspec(dllexport) int RequestSubHandlingDeletion(uintptr_t handlingAddress, int category);
extern "C" __declspec(dllexport) int ReadSubHandlingDeletionOutcome(uintptr_t handlingAddress, int category);

// Grows a Car object's AdvancedData array to the requested capacity, keyed by the Car object's address and the capacity
// It reads Created once grown, AlreadyPresent when the array already holds that many, and Faulted on a failed check
// It reads Unavailable when the setup failed or the capacity is out of range
extern "C" __declspec(dllexport) int RequestAdvancedDataCapacity(uintptr_t carDataAddress, int capacity);
extern "C" __declspec(dllexport) int ReadAdvancedDataCapacityOutcome(uintptr_t carDataAddress, int capacity);

// Cancels a request the caller gave up on, with the same keys as its request export
// An unknown or still waiting request is dropped and reads Canceled. One the script loop is running reads Pending, so the caller asks again
// A finished request reads its real outcome and is forgotten, which the caller handles as if it had read it in time
extern "C" __declspec(dllexport) int CancelSubHandlingCreation(uintptr_t handlingAddress, int category);
extern "C" __declspec(dllexport) int CancelSubHandlingDeletion(uintptr_t handlingAddress, int category);
extern "C" __declspec(dllexport) int CancelAdvancedDataCapacity(uintptr_t carDataAddress, int capacity);
