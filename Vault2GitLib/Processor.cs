using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Serilog;
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
            return x.TxDate < y.TxDate ? -1 : 1;
        }
    }

    public class Processor
    {
        /// <summary>
        /// path where conversion will take place. If it not already set as value working folder, it will be set automatically
        /// </summary>
        public string WorkingFolder;

        private string _originalGitBranch;
        private readonly List<string> _vaultSubdirectories;

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

        public Processor(IGitProvider git, IVaultProvider vault, List<string> vaultSubdirectories)
        {
            _git = git;
            _vault = vault;
            _vaultSubdirectories = vaultSubdirectories;
            if (_vaultSubdirectories.Count == 0) _vaultSubdirectories.Add("");
        }

        /// <summary>
        /// Pulls versions
        /// </summary>
        /// <param name="git2VaultRepoPath">Key=git, Value=vault</param>
        /// <param name="maxTxCount"></param>
        /// <returns>True if something has been committed. False otherwise</returns>
        public void Pull(IEnumerable<KeyValuePair<string, string>> git2VaultRepoPath, long? maxTxCount, bool doGitPush)
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
                    var currentGitVaultVersion = _git.GitVaultVersion(gitBranch, VaultTagStem, BuildVaultTag(vaultRepoPath));
                    var needsInitialCommit = false;

                    foreach (var vaultSubDirectory in _vaultSubdirectories)
                    {
                        var fsPath = Path.Combine(WorkingFolder, vaultSubDirectory);

                        // It's possible there was no commit to vault since _beginDate. To make behaviour consistent with all history merge style let's get the latest versions in case folder is empty
                        if (!currentGitVaultVersion.HasValue)
                        {
                            var txHistoryItem = _vault.VaultGetFolderVersionNearestBeforeBeginDate(vaultRepoPath, vaultSubDirectory);
                            if (txHistoryItem != null)
                            {
                                _vault.VaultGetVersion(vaultRepoPath, vaultSubDirectory, txHistoryItem.Version, true);
                                needsInitialCommit = true;
                            }
                        }
                        else if (!Directory.Exists(fsPath))
                        {
                            var subDirTxIds = _vault.VaultPopulateInfo(vaultRepoPath, vaultSubDirectory, 0);
                            var prevTxHistoryItem = subDirTxIds.LastOrDefault(x => x.TxID < currentGitVaultVersion.Value);
                            if (prevTxHistoryItem != null)
                            {
                                var txIdToGet = prevTxHistoryItem.TxID;
                                GetVaultSubdirectoryExactTxId(vaultRepoPath, vaultSubDirectory, txIdToGet);
                                needsInitialCommit = true;
                            }
                        }

                        //get vaultVersions
                        var newTxIds = _vault.VaultPopulateInfo(vaultRepoPath, vaultSubDirectory, currentGitVaultVersion ?? 0);
                        txHistoryItems.UnionWith(newTxIds);
                    }

                    if (needsInitialCommit)
                    {
                        GitCommit(doGitPush, vaultRepoPath, currentGitVaultVersion ?? 0, gitBranch, "MergeTool", "Initialising some folders", DateTime.UtcNow);
                    }

                    Log.Information($"init took {perBranchWatch.Elapsed}");

                    var counter = 0;
                    foreach (var txHistoryItem in txHistoryItems)
                    {
                        var perTransactionWatch = Stopwatch.StartNew();
                        
                        ++counter;

                        ProcessTransaction(vaultRepoPath, txHistoryItem.TxID);
                        if (GitCommit(doGitPush, vaultRepoPath, txHistoryItem.TxID, gitBranch, txHistoryItem.UserLogin, txHistoryItem.Comment, txHistoryItem.TxDate.GetDateTime()))
                        {
                            Log.Information($"processing transaction {txHistoryItem.TxID} took {perTransactionWatch.Elapsed}. Author: {txHistoryItem.UserLogin}, Comment: {txHistoryItem.Comment}, commit time: {txHistoryItem.TxDate.GetDateTime()}");
                        }

                        //check if limit is reached
                        if (maxTxCount.HasValue && counter >= maxTxCount)
                            break;
                    }

                    VaultFinalize(vaultRepoPath);
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

        private bool GitCommit(bool doGitPush, string vaultRepoPath, long txId, string gitBranch, string author, string comment, DateTime commitTime)
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

            if (doGitPush)
            {
                _git.GitPushOrigin(gitBranch);
            }

            return true;
        }

        private void ProcessTransaction(string vaultRepoPath, long txId)
        {
            var foldersRecursivelyDownloaded = new HashSet<string>();

            var txnInfo = _vault.GetTxInfo(txId);

            // It has been noticed that renames tend to be listed at the end of txnInfo.items. Make sure this event precedes content updates
            var orderedItems = txnInfo.items.OrderBy(x => x.RequestType != VaultRequestType.Rename && x.RequestType != VaultRequestType.Move).ToList();
            if (orderedItems.Any())
            {
                var commitTime = orderedItems.First().TxDate;
                var files = string.Join(",", txnInfo.items.Select(x => string.IsNullOrEmpty(x.ItemPath1) ? x.Name : x.ItemPath1));
                Log.Debug($"Processing transaction ID {txId}: commit time: {commitTime}, comment: {txnInfo.changesetComment}, author: {txnInfo.userlogin}, files/dirs: {files}");
            }

            foreach (var txDetailItem in orderedItems)
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
                catch (Exception e)
                {
                    if (foldersRecursivelyDownloaded.Add(vaultSubdirectory))
                    {
                        Log.Debug($"Failed to get {vaultRepoPath}/{itemPath} version {versionToGet}: {e}");
                        GetVaultSubdirectoryExactTxId(vaultRepoPath, vaultSubdirectory, txId);
                    }
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

            Log.Debug($"Getting {vaultRepoPath}/{vaultSubdirectory} recursively. TxID: {txId}, Time: {folderTransactionDetail.TxDate}, Version: {folderTransactionDetail.Version}, Comment: {folderTransactionDetail.Comment}, Author: {folderTransactionDetail.UserLogin}");
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

        private void VaultFinalize(string vaultRepoPath)
        {
            _vault.UnSetVaultWorkingFolder(vaultRepoPath);
            _git.GitCheckout(_originalGitBranch); // Return to original Git branch
        }
    }
}