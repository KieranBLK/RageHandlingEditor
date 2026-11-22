using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private readonly IUpdateChecker updateChecker;

        private interface IUpdateChecker
        {
            void Start();
            bool IsFinished();
            void Finish();
            void WriteReloadStateLines(StringBuilder sb);
            bool ConsumeReloadStateLine(string raw);
        }

        // Asks the releases endpoint for the latest published version once per game launch, on a worker thread, so the frame never waits on the network
        // A finished check rides the reload state, which a reload carries and a relaunch discards, so the reloads of one launch never ask again
        private sealed class UpdateChecker : IUpdateChecker
        {
            private readonly ILogger logger;
            private readonly INotificationFeed notifications;
            private readonly Func<bool> isEnabled;
            private Task<string> pendingRequest;
            // The latest release's version this launch's finished check read, null until one finishes
            // A failed check leaves it null, so a later turn-on or the next load in this launch asks again
            private string latestReleaseVersion;

            public UpdateChecker(ILogger checkLogger, INotificationFeed feed, Func<bool> checkEnabled)
            {
                logger = checkLogger;
                notifications = feed;
                isEnabled = checkEnabled;
            }

            public void Start()
            {
                if (pendingRequest != null)
                {
                    logger.Log(Dbg.Level.Info, Dbg.UpdateCheckSkipped("A check is already running"));
                    return;
                }
                if (latestReleaseVersion != null)
                {
                    logger.Log(Dbg.Level.Info, Dbg.UpdateCheckSkipped("Already checked this game launch"));
                    return;
                }
                // The endpoint accepts TLS 1.2 or newer, and an explicit protocol set from an old framework default can lack it
                // SystemDefault lets the operating system choose, which already offers TLS 1.2, and the setting is shared by every script in the game
                // So TLS 1.2 is only added to an explicit set, never replacing SystemDefault with a narrower one
                if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault) ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                logger.Log(Dbg.Level.Info, Dbg.UpdateCheckStarted(ServicePointManager.SecurityProtocol.ToString()));
                pendingRequest = Task.Run(() => RequestLatestTag());
            }

            // Runs on a worker thread, so it touches no game, menu, or script state
            private static string RequestLatestTag()
            {
                var request = (HttpWebRequest)WebRequest.Create(UPDATE_CHECK_URL);
                request.UserAgent = UPDATE_CHECK_USER_AGENT_PRODUCT + "/" + ScriptVersion;
                request.Accept = UPDATE_CHECK_ACCEPT;
                // Timeout bounds the wait for the response and ReadWriteTimeout the read of its body, so both are set
                request.Timeout = UPDATE_CHECK_TIMEOUT_MS;
                request.ReadWriteTimeout = UPDATE_CHECK_TIMEOUT_MS;
                string body;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) body = reader.ReadToEnd();
                var reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                object tag;
                return reply != null && reply.TryGetValue(UPDATE_CHECK_TAG_KEY, out tag) ? tag as string : null;
            }

            public bool IsFinished() { return pendingRequest != null && pendingRequest.IsCompleted; }

            // A throw here would reach the tick boundary and log at ERROR, which posts a red toast, so every failure stays a WARN
            public void Finish()
            {
                Task<string> finished = pendingRequest;
                pendingRequest = null;
                try
                {
                    if (!isEnabled())
                    {
                        logger.Log(Dbg.Level.Info, Dbg.UpdateResultDropped());
                        return;
                    }
                    if (finished.IsFaulted)
                    {
                        Exception failure = finished.Exception.InnerException ?? finished.Exception;
                        logger.Log(Dbg.Level.Warn, Dbg.UpdateCheckFailed(failure.Message));
                        return;
                    }
                    string tag = finished.Result;
                    Version latest;
                    if (!ParseReleaseVersion(tag, out latest))
                    {
                        logger.Log(Dbg.Level.Warn, Dbg.UpdateTagUnparsed(tag ?? ""));
                        return;
                    }
                    latestReleaseVersion = latest.ToString();
                    // A running build newer than the release, such as a development build, reads as up to date
                    if (latest <= new Version(ScriptVersion))
                    {
                        logger.Log(Dbg.Level.Info, Dbg.UpdateCurrent(latestReleaseVersion, ScriptVersion));
                        return;
                    }
                    notifications.Notify(NOTIFICATION_KEY_UPDATE_AVAILABLE, string.Format(NOTIF_UPDATE_AVAILABLE, latestReleaseVersion));
                    logger.Log(Dbg.Level.Info, Dbg.UpdateAvailable(latestReleaseVersion, ScriptVersion));
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.UpdateCheckFailed(ex.Message)); }
            }

            // A tag with text after its numbers, such as a beta suffix, does not parse, so it never reads as newer
            private static bool ParseReleaseVersion(string tag, out Version version)
            {
                version = null;
                if (string.IsNullOrEmpty(tag)) return false;
                string text = tag.StartsWith(UPDATE_CHECK_TAG_PREFIX, StringComparison.OrdinalIgnoreCase) ? tag.Substring(UPDATE_CHECK_TAG_PREFIX.Length) : tag;
                return Version.TryParse(text, out version);
            }

            // Writes nothing until this launch's check finishes, so a reload during a request or after a failure asks again
            public void WriteReloadStateLines(StringBuilder sb)
            {
                if (latestReleaseVersion != null) sb.AppendLine(latestReleaseVersion);
            }

            public bool ConsumeReloadStateLine(string raw)
            {
                latestReleaseVersion = raw;
                return true;
            }
        }
    }
}
