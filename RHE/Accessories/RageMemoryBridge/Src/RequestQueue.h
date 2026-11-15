#pragma once
#include <map>
#include <mutex>
#include <vector>

// The requests a caller marks from any thread and the script loop takes and runs on its own frame, keyed by the request's key type
// An entry lives from its request until its outcome is read or canceled, so the map holds only requests in flight or waiting to be read
// The queue owns its mutex, so every call is safe from any thread and nothing else needs to share the lock
template <typename Key, typename Outcome>
class RequestQueue
{
  public:
    // The outcomes a read returns while an attempt has not finished, and a cancel returns for a request it dropped
    RequestQueue(Outcome pendingOutcome, Outcome canceledOutcome) : pending(pendingOutcome), canceled(canceledOutcome) {}

    // Marks a request for the script loop to take. A request already waiting stays one request
    void Mark(const Key& key)
    {
        std::lock_guard<std::mutex> lock(mutex);
        Request& request = requests.try_emplace(key, Request{ false, false, pending }).first->second;
        request.marked = true;
    }

    // Script loop: Takes every marked request and sets each running, so a cancel that lands during its attempt can tell it already started
    std::vector<Key> TakeMarked()
    {
        std::vector<Key> taken;
        std::lock_guard<std::mutex> lock(mutex);
        for (auto& entry : requests)
        {
            if (!entry.second.marked) continue;
            entry.second.marked = false;
            entry.second.running = true;
            taken.push_back(entry.first);
        }
        return taken;
    }

    // Script loop: Writes a taken request's outcome and clears its running mark. A request canceled meanwhile is gone, and its outcome is dropped
    void Finish(const Key& key, Outcome outcome)
    {
        std::lock_guard<std::mutex> lock(mutex);
        auto entry = requests.find(key);
        if (entry == requests.end()) return;
        entry->second.outcome = outcome;
        entry->second.running = false;
    }

    // A final outcome is handed out once and its entry erased, so a later read of the same key reads Pending until it is requested again
    Outcome Read(const Key& key)
    {
        std::lock_guard<std::mutex> lock(mutex);
        auto entry = requests.find(key);
        if (entry == requests.end() || entry->second.marked || entry->second.running) return pending;
        Outcome outcome = entry->second.outcome;
        requests.erase(entry);
        return outcome;
    }

    // A request nobody knows or one still waiting is dropped and reads Canceled. A running one reads Pending, since its attempt cannot be stopped
    // A finished one reads its real outcome, so the caller never disagrees with what the game holds
    Outcome Cancel(const Key& key)
    {
        std::lock_guard<std::mutex> lock(mutex);
        auto entry = requests.find(key);
        if (entry == requests.end()) return canceled;
        if (entry->second.running) return pending;
        Outcome outcome = entry->second.marked ? canceled : entry->second.outcome;
        requests.erase(entry);
        return outcome;
    }

  private:
    struct Request
    {
        bool marked;
        bool running;
        Outcome outcome;
    };

    Outcome pending;
    Outcome canceled;
    std::mutex mutex;
    std::map<Key, Request> requests;
};
