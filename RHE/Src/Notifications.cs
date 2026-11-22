using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private readonly INotificationFeed notifications;

        private interface INotificationFeed
        {
            int PostNotification(string message, int displayMs = -1);
            void Notify(string key, string message, int displayMs = -1);
            void PostNotificationOnce(string key, string message, int cooldownMs = NOTIF_COOLDOWN_MS, int displayMs = -1);
            void RemoveNotification(string key);
            void NotifyFrozen(string key, string message);
            void RemoveFrozenNotification(string key);
            void FlushStartupNotifications();
            bool HasPendingStartupNotifications();
            void SweepExpiredNotifications();
            void RemoveAllActiveNotifications();
        }

        // Holds every toast's cooldown, screen ID, and expiry, so no other file can drop a tracked ID and leave its toast on screen
        private sealed class NotificationFeed : INotificationFeed
        {
            private readonly ILogger logger;
            // True until the first tick, while a post has no effect on screen
            private readonly Func<bool> isBeforeFirstTick;
            // Key = notification key, Value = the Game.GameTime its cooldown ends
            private Dictionary<string, int> notifCooldowns = new Dictionary<string, int>();
            private Dictionary<string, int> notifActive = new Dictionary<string, int>();
            // Key = THEFEED post ID, Value = the Game.GameTime it expires at. One entry per queued auto-removal, rather than 2 parallel lists
            private List<KeyValuePair<int, int>> notifExpirations = new List<KeyValuePair<int, int>>();

            // A post made from the constructor, before the first tick, has no effect on screen, so it waits here and posts on the first tick
            private class PendingNotification
            {
                public string Key;
                public string Message;
                public int DisplayMs;
            }

            private List<PendingNotification> pendingStartupNotifications = new List<PendingNotification>();

            public NotificationFeed(ILogger feedLogger, Func<bool> beforeFirstTick)
            {
                logger = feedLogger;
                isBeforeFirstTick = beforeFirstTick;
            }

            // displayMs = -1 (default): Duration derived from the color prefix, ~r~ = error, ~y~ = warn, anything else = default
            // displayMs > 0: Explicit, queued for auto-removal after that many game milliseconds
            // displayMs = 0: Not queued, the game controls display duration
            public int PostNotification(string message, int displayMs = -1)
            {
                if (displayMs < 0)
                {
                    if (message.StartsWith("~r~", StringComparison.Ordinal)) displayMs = NOTIF_DISPLAY_ERROR_MS;
                    else if (message.StartsWith("~y~", StringComparison.Ordinal)) displayMs = NOTIF_DISPLAY_WARN_MS;
                    else displayMs = NOTIF_DISPLAY_MS;
                }
                Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, NOTIF_FEED_LABEL);
                List<string> pieces = SplitIntoFeedPieces(message);
                for (int i = 0; i < pieces.Count; i++) Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, pieces[i]);
                int id = Function.Call<int>(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, false);
                if (displayMs > 0) notifExpirations.Add(new KeyValuePair<int, int>(id, Game.GameTime + displayMs));
                return id;
            }

            // A piece never ends inside a ~x~ color token or a surrogate pair
            // Text past the last piece is dropped, which only a message far longer than any toast reaches
            private static List<string> SplitIntoFeedPieces(string message)
            {
                var pieces = new List<string>();
                int start = 0;
                while (start < message.Length && pieces.Count < NOTIF_MAX_PIECES)
                {
                    int end = start;
                    int bytes = 0;
                    while (end < message.Length)
                    {
                        int width = char.IsHighSurrogate(message[end]) && end + 1 < message.Length ? 2 : 1;
                        int size = width == 2 ? 4 : message[end] < 0x80 ? 1 : message[end] < 0x800 ? 2 : 3;
                        if (bytes + size > NOTIF_PIECE_MAX_BYTES) break;
                        bytes += size;
                        end += width;
                    }
                    if (end < message.Length)
                    {
                        int tildes = 0;
                        for (int i = start; i < end; i++) if (message[i] == '~') tildes++;
                        int lastTilde = message.LastIndexOf('~', end - 1, end - start);
                        if (tildes % 2 != 0 && lastTilde > start) end = lastTilde;
                    }
                    pieces.Add(message.Substring(start, end - start));
                    start = end;
                }
                return pieces;
            }

            // Replaces in place: Removes whatever notification is tracked under key before posting the new one
            public void Notify(string key, string message, int displayMs = -1)
            {
                if (isBeforeFirstTick())
                {
                    pendingStartupNotifications.Add(new PendingNotification { Key = key, Message = message, DisplayMs = displayMs });
                    return;
                }
                RemovePreviousPost(key);
                notifActive[key] = PostNotification(message, displayMs);
            }

            // The one removal of a feed item, which logs a WARN instead of throwing since a failed removal only leaves a toast on screen
            private void RemoveFeedItem(int id)
            {
                try { Function.Call(Hash.THEFEED_REMOVE_ITEM, id); }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.NotificationRemoveFailed(ex.Message)); }
            }

            // Removes the post still tracked under key, so a replacement never stacks beside it
            private void RemovePreviousPost(string key)
            {
                int prev;
                if (notifActive.TryGetValue(key, out prev) && prev >= 0) RemoveFeedItem(prev);
            }

            public void FlushStartupNotifications()
            {
                List<PendingNotification> pending = pendingStartupNotifications;
                pendingStartupNotifications = new List<PendingNotification>();
                for (int i = 0; i < pending.Count; i++) Notify(pending[i].Key, pending[i].Message, pending[i].DisplayMs);
            }

            public bool HasPendingStartupNotifications() { return pendingStartupNotifications.Count > 0; }

            public void PostNotificationOnce(string key, string message, int cooldownMs = NOTIF_COOLDOWN_MS, int displayMs = -1)
            {
                int now = Game.GameTime;
                int cooldownEnd;
                if (notifCooldowns.TryGetValue(key, out cooldownEnd) && now < cooldownEnd) return;
                if (notifCooldowns.Count > NOTIF_COOLDOWN_PRUNE_THRESHOLD) PruneEndedNotificationCooldowns(now);
                notifCooldowns[key] = now + cooldownMs;
                Notify(key, message, displayMs);
            }

            // Error notifications key on their own message text, so without this each distinct message would keep its entry for the whole session
            private void PruneEndedNotificationCooldowns(int now)
            {
                var endedKeys = new List<string>();
                foreach (KeyValuePair<string, int> entry in notifCooldowns) if (now >= entry.Value) endedKeys.Add(entry.Key);
                for (int i = 0; i < endedKeys.Count; i++) notifCooldowns.Remove(endedKeys[i]);
            }

            // Sweeps the active keyed posts, every post queued for auto-removal, and any frozen post, so nothing this instance posted lingers past its reload
            // An ID present in both sets is removed twice, which is harmless: THEFEED_REMOVE_ITEM on an already-removed ID does nothing
            public void RemoveAllActiveNotifications()
            {
                foreach (int id in notifActive.Values) RemoveFeedItem(id);
                for (int i = 0; i < notifExpirations.Count; i++) RemoveFeedItem(notifExpirations[i].Key);
                ClearFrozenPost();
            }

            // The game holds a frozen post on screen until THEFEED_CLEAR_FROZEN_POST, for a toast that describes a state lasting until the player ends it
            // It posts directly and never through the startup queue, since the freeze applies to whichever post comes next and a queued post could miss it
            // displayMs = 0 queues no auto-removal, so only RemoveFrozenNotification or a reload takes it down
            public void NotifyFrozen(string key, string message)
            {
                RemovePreviousPost(key);
                Function.Call(Hash.THEFEED_FREEZE_NEXT_POST);
                notifActive[key] = PostNotification(message, 0);
            }

            // Clearing with nothing frozen does nothing, so every exit path can call this unconditionally
            public void RemoveFrozenNotification(string key)
            {
                RemoveNotification(key);
                ClearFrozenPost();
            }

            private void ClearFrozenPost()
            {
                try { Function.Call(Hash.THEFEED_CLEAR_FROZEN_POST); }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.FrozenPostClearFailed(ex.Message)); }
            }

            public void SweepExpiredNotifications()
            {
                int now = Game.GameTime;
                for (int i = notifExpirations.Count - 1; i >= 0; i--)
                {
                    if (now < notifExpirations[i].Value) continue;
                    int expiredId = notifExpirations[i].Key;
                    RemoveFeedItem(expiredId);
                    notifExpirations.RemoveAt(i);
                    RemoveActiveNotificationEntry(expiredId);
                }
            }

            // Removes the notification tracked under key from the screen now, for a toast that describes a state which just ended
            // Its queued auto-removal stays and later removes an already-removed ID, a harmless no-op
            public void RemoveNotification(string key)
            {
                int id;
                if (!notifActive.TryGetValue(key, out id)) return;
                notifActive.Remove(key);
                RemoveFeedItem(id);
            }

            // Drops the notifActive key still pointing at a notification that is already gone, so the dictionary only holds live posts
            private void RemoveActiveNotificationEntry(int id)
            {
                string expiredKey = null;
                foreach (KeyValuePair<string, int> entry in notifActive)
                {
                    if (entry.Value == id)
                    {
                        expiredKey = entry.Key;
                        break;
                    }
                }
                if (expiredKey != null) notifActive.Remove(expiredKey);
            }
        }
    }
}
