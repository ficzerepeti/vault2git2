﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Serilog;
using VaultClientIntegrationLib;
using VaultLib;

namespace Vault2Git.Lib
{
    public static class Statics
    {
        // Delete all working files and folders in the repo except those added just for git
        public static bool DeleteWorkingDirectory(string targetDirectory)
        {
            var fDeleteDirectory = true;

            // Process the list of files found in the directory.
            foreach (var fileName in Directory.GetFiles(targetDirectory))
            {
                if (!fileName.Contains(".git") &&
                    fileName != targetDirectory + "\\v2g.bat" &&
                    fileName != targetDirectory + "\\Vault2Git.exe.config")
                {
                    File.Delete(fileName);
                }
                else
                {
                    fDeleteDirectory = false;
                }
            }

            // Delete all subdirectories of this directory, except .git.
            var subdirectoryEntries = Directory.GetDirectories(targetDirectory);
            foreach (var subdirectory in subdirectoryEntries)
            {
                if (!subdirectory.Contains(".git"))
                {
                    if (!DeleteWorkingDirectory(subdirectory))
                    {
                        fDeleteDirectory = false;
                    }
                }
                else
                {
                    fDeleteDirectory = false;
                }
            }

            // If we've not skipped a file or a subdirectory, delete the target directory
            if (fDeleteDirectory)
            {
                try
                {
                    Directory.Delete(targetDirectory, false);
                }
                catch (IOException)
                {
                    // Directory not empty? Presume its a handle still opened by Explorer or a permissions issue. Just continue. Vault get will fail if there is a real issue.
                }
            }

            return fDeleteDirectory;
        }
    }

    public class VaultTxHistoryItemComparer : IComparer<VaultTxHistoryItem>
    {
        public int Compare(VaultTxHistoryItem x, VaultTxHistoryItem y)
        {
            if (x.TxID == y.TxID)
            {
                return 0;
            }

            if (x.TxDate != y.TxDate)
            {
                return x.TxDate < y.TxDate ? -1 : 1;
            }

            return x.TxID < y.TxID ? -1 : 1;

        }
    }

    public class Processor
    {
        private const string Mergetool = "MergeTool";

        /// <summary>
        /// path where conversion will take place. If it not already set as value working folder, it will be set automatically
        /// </summary>
        public string WorkingFolder;

        private string _originalGitBranch;
        private readonly List<string> _vaultSubdirectories;
        private readonly long? _maxTxCount;
        private readonly bool _doGitPush;
        private readonly DateTime? _beginDate;
        private readonly TimeSpan? _sampleTimeWhenNoFullPathAvailable;

        //flags
        public bool ForceFullFolderGet = false;

        //private vars
        /// <summary>
        /// Maps Vault TransactionID to Git Commit SHA-1 Hash
        /// </summary>
        private readonly IDictionary<long, HashSet<string>> _txidMappings = new Dictionary<long, HashSet<string>>();

        //constants
        public const string VaultTag = "[git-vault-id]";

        private readonly IGitProvider _git;
        private readonly IVaultProvider _vault;

        public Processor(IGitProvider git, IVaultProvider vault, List<string> vaultSubdirectories, long? commitCountLimit, bool doGitPush, TimeSpan? sampleTimeWhenNoFullPathAvailable, DateTime? beginDate)
        {
            _git = git;
            _vault = vault;
            _vaultSubdirectories = vaultSubdirectories;
            _maxTxCount = commitCountLimit;
            _doGitPush = doGitPush;
            _sampleTimeWhenNoFullPathAvailable = sampleTimeWhenNoFullPathAvailable;
            _beginDate = beginDate;
            if (_vaultSubdirectories.Count == 0) _vaultSubdirectories.Add("");
        }

        /// <summary>
        /// Pulls versions
        /// </summary>
        /// <param name="git2VaultRepoPath">Key=git, Value=vault</param>
        /// <returns>True if something has been committed. False otherwise</returns>
        public void Pull(IEnumerable<KeyValuePair<string, string>> git2VaultRepoPath)
        {
            //get git current branch name
            _git.GitCurrentBranch(out _originalGitBranch);
            Log.Information($"Starting git branch is {_originalGitBranch}");

            //reorder target branches to start from current branch, so don't need to do checkout for first branch
            var targetList = git2VaultRepoPath.OrderByDescending(p => p.Key.Equals(_originalGitBranch, StringComparison.CurrentCultureIgnoreCase));

            try
            {
                _vault.VaultLogin();
                foreach (var pair in targetList)
                {
                    var perBranchWatch = Stopwatch.StartNew();

                    var gitBranch = pair.Key;
                    var vaultRepoPath = pair.Value;

                    Log.Information($"\nProcessing git branch {gitBranch}");

                    Init(vaultRepoPath, gitBranch);
                    if (!_vault.IsSetRootVaultWorkingFolder())
                    {
                        Environment.Exit(1);
                    }

                    var txHistoryItems = new SortedSet<VaultTxHistoryItem>(new VaultTxHistoryItemComparer());

                    //get current Git version
                    var startingGitVaultVersion = _git.GitVaultVersion(gitBranch, VaultTagStem, BuildVaultTag(vaultRepoPath));

                    //get vault versions
                    foreach (var subdirTxItems in _vaultSubdirectories.Select(vaultSubDirectory => _vault.VaultGetTxHistoryItems(vaultRepoPath, vaultSubDirectory)))
                    {
                        var maybeDateFilteredTxItems = _beginDate.HasValue ? subdirTxItems.Where(txItem => txItem.TxDate.GetDateTime() >= _beginDate.Value) : subdirTxItems;
                        txHistoryItems.UnionWith(maybeDateFilteredTxItems);
                    }

                    // Filter all committed vault versions
                    VaultTxHistoryItem startingTxHistoryItem = null;
                    if (startingGitVaultVersion.HasValue)
                    {
                        var enumerable = txHistoryItems.SkipWhile(x => x.TxID != startingGitVaultVersion.Value);
                        startingTxHistoryItem = enumerable.FirstOrDefault();
                        var passCurrentGitVersion = enumerable.Skip(1);
                        txHistoryItems = new SortedSet<VaultTxHistoryItem>(passCurrentGitVersion, new VaultTxHistoryItemComparer());
                    }

                    var needsInitialCommit = HandleInitialCheckouts(startingTxHistoryItem, vaultRepoPath);
                    if (needsInitialCommit)
                    {
                        GitCommit(vaultRepoPath, startingGitVaultVersion ?? 0, gitBranch, Mergetool, "Initialising some folders", DateTime.UtcNow);
                    }

                    Log.Information($"init took {perBranchWatch.Elapsed}");

                    var counter = 0;
                    var sampledItems = new List<Tuple<VaultTxHistoryItem, TxInfo>>();
                    var perTransactionWatch = Stopwatch.StartNew();
                    foreach (var txHistoryItem in txHistoryItems)
                    {
                        TxInfo txInfo;
                        try
                        {
                            txInfo = _vault.GetTxInfo(txHistoryItem.TxID);
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"Failed to get transaction info for {txHistoryItem.TxDate.GetDateTime():u}, txID={txHistoryItem.TxID}, author={txHistoryItem.UserLogin}, comment={txHistoryItem.Comment}\n\n{e}");
                            continue;
                        }

                        if (_sampleTimeWhenNoFullPathAvailable.HasValue && txInfo.items.All(x => string.IsNullOrEmpty(x.ItemPath1)))
                        {
                            if (sampledItems.Count > 0 && (txHistoryItem.TxDate - sampledItems[0].Item1.TxDate >= _sampleTimeWhenNoFullPathAvailable.Value))
                            {
                                ++counter;
                                GetAndCommitSampledTransactions(vaultRepoPath, sampledItems, gitBranch, perTransactionWatch);
                            }

                            sampledItems.Add(Tuple.Create(txHistoryItem, txInfo));
                        }
                        else
                        {
                            ++counter;
                            GetAndCommitTransaction(vaultRepoPath, txHistoryItem, txInfo, gitBranch, perTransactionWatch);
                        }

                        //check if limit is reached
                        if (_maxTxCount.HasValue && counter >= _maxTxCount)
                            break;
                    }

                    GetAndCommitSampledTransactions(vaultRepoPath, sampledItems, gitBranch, perTransactionWatch);

                    _vault.UnSetVaultWorkingFolder(vaultRepoPath);
                    _git.GitCheckout(_originalGitBranch); // Return to original Git branch
                }
            }
            finally
            {
                var finalizeWatch = Stopwatch.StartNew();
                Log.Information("\n");

                //complete
                _vault.VaultLogout();

                //finalize git (update server info for dumb clients)
                _git.GitFinalize();
                Log.Information($"finalization took {finalizeWatch.Elapsed}");
            }
        }

        private bool HandleInitialCheckouts(VaultTxHistoryItem startingTxHistoryItem, string vaultRepoPath)
        {
            if (startingTxHistoryItem == null && !_beginDate.HasValue)
            {
                return false;
            }

            var needsInitialCommit = false;
            foreach (var vaultSubDirectory in _vaultSubdirectories)
            {
                var fsPath = Path.Combine(WorkingFolder, vaultSubDirectory);
                if (string.IsNullOrEmpty(vaultSubDirectory))
                {
                    if (Directory.GetDirectories(fsPath).Any(x => !x.Contains(".git"))) continue;
                }
                else if (Directory.Exists(fsPath)) continue;

                var subdirTxItems = _vault.VaultGetTxHistoryItems(vaultRepoPath, vaultSubDirectory);

                // It's possible there was no commit to vault since _beginDate. To make behaviour consistent with all history merge style let's get the latest versions in case folder is empty
                VaultTxHistoryItem folderTxItem = null;
                if (startingTxHistoryItem != null)
                {
                    folderTxItem = subdirTxItems.FirstOrDefault(x => x.TxID == startingTxHistoryItem.TxID);
                }

                if (folderTxItem == null && _beginDate.HasValue)
                {
                    folderTxItem = subdirTxItems.LastOrDefault(x => x.TxDate.GetDateTime() < _beginDate.Value);
                }

                if (folderTxItem != null)
                {
                    _vault.VaultGetVersion(vaultRepoPath, vaultSubDirectory, folderTxItem.Version, true);
                    needsInitialCommit = true;
                }
            }

            return needsInitialCommit;
        }

        private bool GitCommit(string vaultRepoPath, long txId, string gitBranch, string author, string comment, DateTime commitTime)
        {
            var commitMsg = BuildCommitMessage(vaultRepoPath, txId, comment);
            var gitCommitId = _git.GitCommit(author, commitMsg, commitTime);

            // Mapping Vault Transaction ID to Git Commit SHA-1 Hash
            var committedAnything = !string.IsNullOrEmpty(gitCommitId);
            if (!committedAnything)
            {
                return false;
            }

            if (!_txidMappings.TryGetValue(txId, out var gitCommitIds))
            {
                gitCommitIds = new HashSet<string>();
                _txidMappings[txId] = gitCommitIds;
            }
            gitCommitIds.Add(gitCommitId);

            if (_doGitPush)
            {
                _git.GitPushOrigin(gitBranch);
            }

            return true;
        }

        private void GetAndCommitTransaction(string vaultRepoPath, VaultTxHistoryItem txHistoryItem, TxInfo txInfo, string gitBranch, Stopwatch perTransactionWatch)
        {
            var files = string.Join(",", txInfo.items.Select(x => string.IsNullOrEmpty(x.ItemPath1) ? x.Name : x.ItemPath1));
            Log.Debug($"Processing transaction ID {txHistoryItem.TxID}: commit time: {txHistoryItem.TxDate.GetDateTime():u}, comment: {txHistoryItem.Comment}, author: {txHistoryItem.UserLogin}, files/dirs: {files}");

            var itemsFailedToGet = new HashSet<string>();

            // It has been noticed that renames tend to be listed at the end of txnInfo.items. Make sure this event precedes content updates
            foreach (var txDetailItem in txInfo.items.OrderBy(x => x.RequestType != VaultRequestType.Rename && x.RequestType != VaultRequestType.Move))
            {
                if (!TryFindMatchingSubdir(vaultRepoPath, txDetailItem.ItemPath1, out var vaultSubdirectory)
                    && !TryFindMatchingSubdir(vaultRepoPath, txDetailItem.ItemPath2, out vaultSubdirectory))
                {
                    continue;
                }

                if (ForceFullFolderGet) continue;

                var itemPath = RemoveRepoFromItemPath(vaultRepoPath, string.IsNullOrEmpty(txDetailItem.ItemPath1) ? txDetailItem.Name : txDetailItem.ItemPath1);
                var versionToGet = txDetailItem.Version;

                switch (txDetailItem.RequestType)
                {
                    // Do deletions, renames and moves ourselves
                    case VaultRequestType.Delete:
                    {
                        var filesystemPath = txDetailItem.ItemPath1.Replace(vaultRepoPath, WorkingFolder);

                        if (File.Exists(filesystemPath))
                        {
                            File.Delete(filesystemPath);
                        }
                        else if (Directory.Exists(filesystemPath))
                        {
                            Directory.Delete(filesystemPath, true);
                        }

                        continue;
                    }

                    case VaultRequestType.Share:
                    case VaultRequestType.CheckOut:
                    case VaultRequestType.LabelItem:
                    case VaultRequestType.AddFolder: // Nothing in a CopyBranch to do. Its just a place marker
                    case VaultRequestType.CopyBranch: // Git doesn't add empty folders
                        continue;

                    case VaultRequestType.Move:
                    case VaultRequestType.Rename:
                    {
                        Log.Debug($"Handling rename/move from {txDetailItem.ItemPath1} to {txDetailItem.ItemPath2}");

                        var item1NoRepoPath = RemoveRepoFromItemPath(vaultRepoPath, txDetailItem.ItemPath1);
                        var item1FsPath = Path.Combine(WorkingFolder, item1NoRepoPath);

                        var item2NoRepoPath = txDetailItem.RequestType == VaultRequestType.Move ?
                            RemoveRepoFromItemPath(vaultRepoPath, txDetailItem.ItemPath2) : // Move
                            item1NoRepoPath.Remove(item1NoRepoPath.Length - Path.GetFileName(txDetailItem.ItemPath1).Length) + txDetailItem.ItemPath2; // Rename
                        var item2FsPath = Path.Combine(WorkingFolder, item2NoRepoPath);

                        DeleteFileOrFolder(item2FsPath);

                        if (!TryFindMatchingSubdir(vaultRepoPath, item2NoRepoPath, out _))
                        {
                            DeleteFileOrFolder(item1FsPath);
                            continue;
                        }

                        if (Directory.Exists(item1FsPath))
                        {
                            var item2FsPathNoSlashEnding = item2FsPath.EndsWith("/") || item2FsPath.EndsWith("\\") ? item2FsPath.Substring(item2FsPath.Length - 1) : item2FsPath;
                            Directory.CreateDirectory(Path.GetDirectoryName(item2FsPathNoSlashEnding));
                            Directory.Move(item1FsPath, item2FsPath);
                        }
                        else if (File.Exists(item1FsPath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(item2FsPath));
                            File.Move(item1FsPath, item2FsPath);
                        }
                        else
                        {
                            versionToGet = txDetailItem.OtherVersion;
                            itemPath = item2NoRepoPath;
                            break;
                        }
                        continue;
                    }
                }

                // Apply the changes from vault of the correct version for this file
                try
                {
                    _vault.VaultGetVersion(vaultRepoPath, itemPath, versionToGet, false);
                }
                catch
                {
                    itemsFailedToGet.Add(itemPath);
                    continue;
                }

                var fsPath = Path.Combine(WorkingFolder, itemPath);
                if (!File.Exists(fsPath)) continue;

                // Remove Source Code Control
                switch (Path.GetExtension(fsPath).ToLower())
                {
                    case ".sln":
                        RemoveSccFromSln(fsPath);
                        break;
                    case ".csproj":
                        RemoveSccFromCsProj(fsPath);
                        break;
                    case ".vdproj":
                        RemoveSccFromVdProj(fsPath);
                        break;
                }
            }

            GetParentsOfFailedItems(vaultRepoPath, itemsFailedToGet, txHistoryItem.TxID);

            if (GitCommit(vaultRepoPath, txHistoryItem.TxID, gitBranch, txHistoryItem.UserLogin, txHistoryItem.Comment, txHistoryItem.TxDate.GetDateTime()))
            {
                Log.Information($"Committing transaction {txHistoryItem.TxID} took {perTransactionWatch.Elapsed}. Author: {txHistoryItem.UserLogin}, Comment: {txHistoryItem.Comment}, commit time: {txHistoryItem.TxDate.GetDateTime():u}");
            }
            perTransactionWatch.Restart();
        }

        private void GetParentsOfFailedItems(string vaultRepoPath, HashSet<string> itemsFailedToGet, long txId)
        {
            if (itemsFailedToGet.Count == 0)
            {
                return;
            }

            Log.Debug($"Failed to get {string.Join(", ", itemsFailedToGet)} in {vaultRepoPath}. Getting their parents now");

            var subDirToFiles = new Dictionary<string, HashSet<string>>();
            foreach (var itemPath in itemsFailedToGet)
            {
                if (!TryFindMatchingSubdir(vaultRepoPath, itemPath, out var matchingVaultSubdirectory))
                {
                    continue;
                }

                if (!subDirToFiles.TryGetValue(matchingVaultSubdirectory, out var files))
                {
                    files = new HashSet<string>();
                    subDirToFiles.Add(matchingVaultSubdirectory, files);
                }

                files.Add(itemPath);
            }

            var downloadedFolders = new HashSet<string>();
            foreach (var pair in subDirToFiles)
            {
                var vaultSubDir = pair.Key;
                var files = pair.Value;

                for (var parents = new HashSet<string>(files.Select(x => Path.GetDirectoryName(x))); parents.Count > 0; parents.RemoveWhere(parent => downloadedFolders.Any(parent.StartsWith)))
                {
                    var parent = parents.OrderBy(x => x.Count(y => y == '/' || y == '\\')).First();

                    try
                    {
                        GetVaultSubdirectoryExactTxId(vaultRepoPath, parent, txId);
                        downloadedFolders.Add(parent);
                        if (parent == vaultSubDir)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        var grandParent = Path.GetDirectoryName(parent);
                        if (!string.IsNullOrEmpty(grandParent))
                        {
                            parents.Add(grandParent);
                        }
                    }

                    parents.Remove(parent);
                }
            }
        }

        private void GetAndCommitSampledTransactions(string vaultRepoPath, List<Tuple<VaultTxHistoryItem, TxInfo>> group, string gitBranch, Stopwatch perTransactionWatch)
        {
            if (group.Count == 0) return;

            var lastHistoryItem = group.Last().Item1;
            foreach (var vaultSubdirectory in _vaultSubdirectories)
            {
                try
                {
                    GetVaultSubdirectoryExactTxId(vaultRepoPath, vaultSubdirectory, lastHistoryItem.TxID);
                }
                catch (Exception e)
                {
                    Log.Warning($"Failed to get txId {lastHistoryItem.TxID} for {vaultRepoPath}/{vaultSubdirectory}: {e}");
                }
            }

            var commitSummary = string.Join("\n", group.Select(x => $"TxID: {x.Item1.TxID}. Author: {x.Item1.UserLogin}, Comment: {x.Item1.Comment}, commit time: {x.Item1.TxDate.GetDateTime():u}"));
            if (GitCommit(vaultRepoPath, lastHistoryItem.TxID, gitBranch, Mergetool, commitSummary, lastHistoryItem.TxDate.GetDateTime()))
            {
                Log.Information($"Committing transaction took {perTransactionWatch.Elapsed}. {commitSummary}");
            }

            group.Clear();
            perTransactionWatch.Restart();
        }

        private static void DeleteFileOrFolder(string fsPath)
        {
            if (Directory.Exists(fsPath))
            {
                Directory.Delete(fsPath, true);
            }
            else if (File.Exists(fsPath))
            {
                File.Delete(fsPath);
            }
        }

        private static string RemoveRepoFromItemPath(string vaultRepoPath, string itemPath)
        {
            if (itemPath.StartsWith(vaultRepoPath))
            {
                itemPath = itemPath.Remove(0, vaultRepoPath.Length);
                if (itemPath.StartsWith("/"))
                {
                    itemPath = itemPath.Remove(0, 1);
                }
            }

            return itemPath;
        }

        private void GetVaultSubdirectoryExactTxId(string vaultRepoPath, string vaultSubdirectory, long txId)
        {
            var folderTransactionDetail = _vault.VaultGetFolderVersionExactTxId(vaultRepoPath, vaultSubdirectory, txId);
            if (folderTransactionDetail == null)
            {
                throw new Exception($"Failed to get transaction detail for {vaultSubdirectory} and txId {txId}");
            }

            Log.Debug($"Getting {vaultRepoPath}/{vaultSubdirectory} recursively. TxID: {txId}, Time: {folderTransactionDetail.TxDate.GetDateTime():u}, Version: {folderTransactionDetail.Version}, Comment: {folderTransactionDetail.Comment}, Author: {folderTransactionDetail.UserLogin}");
            var targetDirectory = Path.Combine(WorkingFolder, vaultSubdirectory);
            if (Directory.Exists(targetDirectory))
            {
                Statics.DeleteWorkingDirectory(targetDirectory);
            }

            _vault.VaultGetVersion(vaultRepoPath, vaultSubdirectory, folderTransactionDetail.Version, true);

            var fsPath = Path.Combine(WorkingFolder, vaultSubdirectory);
            Directory.GetFiles(fsPath, "*.sln", SearchOption.AllDirectories).Where(f => !f.Contains("~")).ToList().ForEach(RemoveSccFromSln);
            Directory.GetFiles(fsPath, "*.csproj", SearchOption.AllDirectories).Where(f => !f.Contains("~")).ToList().ForEach(RemoveSccFromCsProj);
            Directory.GetFiles(fsPath, "*.vdproj", SearchOption.AllDirectories).Where(f => !f.Contains("~")).ToList().ForEach(RemoveSccFromVdProj);
        }

        /// <summary>
        /// removes Source control refs from sln files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        private static void RemoveSccFromSln(string filePath)
        {
            var lines = File.ReadAllLines(filePath).ToList();
            //scan lines 
            var searchingForStart = true;
            var beginingLine = 0;
            var endingLine = 0;
            var currentLine = 0;
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (searchingForStart)
                {
                    if (trimmedLine.StartsWith("GlobalSection(SourceCodeControl)"))
                    {
                        beginingLine = currentLine;
                        searchingForStart = false;
                    }
                }
                else
                {
                    if (trimmedLine.StartsWith("EndGlobalSection"))
                    {
                        endingLine = currentLine;
                        break;
                    }
                }

                currentLine++;
            }

            //removing lines
            if (beginingLine > 0 & endingLine > 0)
            {
                lines.RemoveRange(beginingLine, endingLine - beginingLine + 1);
                File.WriteAllLines(filePath, lines.ToArray(), Encoding.UTF8);
            }
        }

        /// <summary>
        /// removes Source control refs from csProj files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        public static void RemoveSccFromCsProj(string filePath)
        {
            var doc = new XmlDocument();
            try
            {
                doc.Load(filePath);
                while (true)
                {
                    var nav = doc.CreateNavigator().SelectSingleNode("//*[starts-with(name(), 'Scc')]");
                    if (null == nav)
                        break;
                    nav.DeleteSelf();
                }

                doc.Save(filePath);
            }
            catch
            {
                Log.Error($"Failed for {filePath}");
                throw;
            }
        }

        /// <summary>
        /// removes Source control refs from vdProj files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        private static void RemoveSccFromVdProj(string filePath)
        {
            var lines = File.ReadAllLines(filePath).ToList();
            File.WriteAllLines(filePath, lines.Where(l => !l.Trim().StartsWith(@"""Scc")).ToArray(), Encoding.UTF8);
        }

        /// <summary>
        /// Creates Git tags from Vault labels
        /// </summary>
        /// <param name="repositoryFolderPath"></param>
        /// <returns></returns>
        public void CreateTagsFromLabels(string repositoryFolderPath = "$")
        {
            var labelWatch = Stopwatch.StartNew();
            Log.Information("Creating tags from labels...");
            _vault.VaultLogin();

            // Search for all labels recursively
            var objId = _vault.FindVaultTreeObjectAtReposOrLocalPath(repositoryFolderPath).ID;

            _vault.BeginLabelQuery(repositoryFolderPath, objId, out _, out var rowsRetRecur, out var qryToken);
            _vault.GetLabelQueryItems_Recursive(qryToken, 0, rowsRetRecur, out var labelItems);

            try
            {
                if (labelItems != null)
                {
                    foreach (var currItem in labelItems)
                    {
                        if (!_txidMappings.TryGetValue(currItem.TxID, out var gitCommitIds))
                            continue;

                        var gitCommitId = string.Join(",", gitCommitIds);
                        if (!string.IsNullOrEmpty(gitCommitId))
                        {
                            var gitLabelName = Regex.Replace(currItem.Label, "[\\W]", "_");
                            _git.GitAddTag($"{currItem.TxID}_{gitLabelName}", gitCommitId, currItem.Comment);
                        }
                    }
                }

                //add ticks for git tags
                Log.Information($"tags creation took {labelWatch.Elapsed}");
            }
            finally
            {
                //complete
                _vault.EndLabelQuery(qryToken);
                _vault.VaultLogout();
                _git.GitFinalize();
            }
        }

        private bool TryFindMatchingSubdir(string vaultRepo, string itemPath, out string matchingVaultSubdirectory)
        {
            itemPath = RemoveRepoFromItemPath(vaultRepo, itemPath);

            foreach (var vaultSubdirectory in _vaultSubdirectories)
            {
                if (itemPath.StartsWith(vaultSubdirectory, StringComparison.InvariantCultureIgnoreCase))
                {
                    matchingVaultSubdirectory = vaultSubdirectory;
                    return true;
                }
            }

            matchingVaultSubdirectory = null;
            return false;
        }

        private void Init(string vaultRepoPath, string gitBranch)
        {
            //set working folder
            _vault.SetVaultWorkingFolder(vaultRepoPath, WorkingFolder);
            //checkout branch
            for (var tries = 0; tries <= 5; tries++)
            {
                _git.GitCheckout(gitBranch);
                //confirm current branch (sometimes checkout failed)
                _git.GitCurrentBranch(out var currentBranch);
                if (gitBranch.Equals(currentBranch, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            throw new Exception("cannot switch branches");
        }

        // vaultLogin is the user name as known in Vault e.g. 'robert' which needs to be mapped to rob.goodridge

        private string BuildCommitMessage(string repoPath, long trxId, string comment) => $"{comment}\n{BuildVaultTag(repoPath)}@{trxId}";
        private string VaultTagStem => $"{VaultTag} {_vault.VaultRepository}";
        private string BuildVaultTag(string repoPath) => $"{VaultTagStem} {repoPath}/{string.Join(";", _vaultSubdirectories)}";
    }
}