using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GTA;
namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private readonly IOwnedFileWatcher fileWatcher;

        // The files the script reacts to when something outside it changes them
        private enum OwnedFile { Ini, SavedHandlings, ModelMetaData }

        private interface IOwnedFileWatcher
        {
            bool IsWatching(OwnedFile file);
            bool TakeDueChange(OwnedFile file, int gameTime);
            void Dispose();
        }

        // The operating system reports each change, so nothing re-reads a file on a timer
        // Its events arrive on a worker thread, where the game cannot be called, so a handler only raises a flag the tick reads
        private sealed class OwnedFileWatcher : IOwnedFileWatcher
        {
            private readonly ILogger logger;
            private readonly string iniFullPath;
            private readonly string exportsFullPath;
            private readonly string modelMetaDataFullPath;
            private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
            private readonly bool[] watching = new bool[Enum.GetValues(typeof(OwnedFile)).Length];
            // 1 while a change waits for the tick, written by the worker thread through Interlocked only
            private readonly int[] changePending = new int[Enum.GetValues(typeof(OwnedFile)).Length];
            // An editor saves in several writes, so every new event restarts the wait and one refresh runs after the last
            private readonly int[] changeDueTime = new int[Enum.GetValues(typeof(OwnedFile)).Length];
            private readonly bool[] changeWaiting = new bool[Enum.GetValues(typeof(OwnedFile)).Length];

            public OwnedFileWatcher(ILogger logger, string iniPath)
            {
                this.logger = logger;
                iniFullPath = Path.GetFullPath(iniPath);
                // Held with no trailing separator, so an event on the Exports folder itself matches as well as one inside it
                exportsFullPath = Path.GetFullPath(EXPORTS_DIR).TrimEnd(Path.DirectorySeparatorChar);
                modelMetaDataFullPath = Path.GetFullPath(MODEL_METADATA_FILE_PATH);
                StartWatcher(Path.GetDirectoryName(iniFullPath), Path.GetFileName(iniFullPath), false, OwnedFile.Ini);
                // Watches the script's own folder rather than the Exports folder, so the Exports folder being created or deleted counts too, Autosaves included
                StartWatcher(Path.GetFullPath(SCRIPT_FOLDER_PATH), "*", true, OwnedFile.SavedHandlings);
                StartWatcher(Path.GetDirectoryName(modelMetaDataFullPath), Path.GetFileName(modelMetaDataFullPath), false, OwnedFile.ModelMetaData);
            }

            // A watcher that cannot start leaves its files on the timed poll
            private void StartWatcher(string folder, string filter, bool includeSubfolders, params OwnedFile[] files)
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder, filter);
                    watcher.IncludeSubdirectories = includeSubfolders;
                    // DirectoryName reports a folder created, deleted, or renamed, such as the Autosaves folder, which the file names alone never cover
                    watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size;
                    watcher.Changed += OnFileEvent;
                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += OnFileRenamed;
                    // An overflowed event buffer loses events, so every file this watcher covers is checked once
                    watcher.Error += (sender, e) => MarkChanged(files);
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                    for (int i = 0; i < files.Length; i++) watching[(int)files[i]] = true;
                    logger.Log(Dbg.Level.Info, Dbg.FileWatcherStarted(folder, filter));
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.FileWatcherFailed(folder, filter, ex.Message)); }
            }

            private void OnFileEvent(object sender, FileSystemEventArgs e) { MarkPathChanged(e.FullPath); }

            private void OnFileRenamed(object sender, RenamedEventArgs e)
            {
                MarkPathChanged(e.OldFullPath);
                MarkPathChanged(e.FullPath);
            }

            private void MarkPathChanged(string fullPath)
            {
                if (string.Equals(fullPath, iniFullPath, StringComparison.OrdinalIgnoreCase)) MarkChanged(OwnedFile.Ini);
                else if (string.Equals(fullPath, modelMetaDataFullPath, StringComparison.OrdinalIgnoreCase)) MarkChanged(OwnedFile.ModelMetaData);
                else if (string.Equals(fullPath, exportsFullPath, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(exportsFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) MarkChanged(OwnedFile.SavedHandlings);
            }

            private void MarkChanged(params OwnedFile[] files)
            {
                for (int i = 0; i < files.Length; i++) Interlocked.Exchange(ref changePending[(int)files[i]], 1);
            }

            public bool IsWatching(OwnedFile file) { return watching[(int)file]; }

            // True once per settled change, when no further event arrived for FILE_WATCH_DEBOUNCE_MS of game time
            public bool TakeDueChange(OwnedFile file, int gameTime)
            {
                int index = (int)file;
                if (Interlocked.Exchange(ref changePending[index], 0) == 1)
                {
                    changeDueTime[index] = gameTime + FILE_WATCH_DEBOUNCE_MS;
                    changeWaiting[index] = true;
                }
                if (!changeWaiting[index] || gameTime < changeDueTime[index]) return false;
                changeWaiting[index] = false;
                return true;
            }

            // Runs before the script unloads, so no event reaches an instance that is going away
            public void Dispose()
            {
                for (int i = 0; i < watchers.Count; i++)
                {
                    watchers[i].EnableRaisingEvents = false;
                    watchers[i].Dispose();
                }
                watchers.Clear();
            }
        }
    }
}
