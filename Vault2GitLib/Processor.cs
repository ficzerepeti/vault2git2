using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        public static string Replace(this string str, string oldValue, string newValue, StringComparison comparision)
        {
            var sb = new StringBuilder();

            var previousIndex = 0;
            var index = str.IndexOf(oldValue, comparision);
            while (index != -1)
            {
                sb.Append(str.Substring(previousIndex, index - previousIndex));
                sb.Append(newValue);
                index += oldValue.Length;

                previousIndex = index;
                index = str.IndexOf(oldValue, index, comparision);
            }

            sb.Append(str.Substring(previousIndex));

            return sb.ToString();
        }

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

        public static string ToForwardSlash(this string str) => str.Replace('\\', '/');
    }

    public class Processor
    {
        /// <summary>
        /// path where conversion will take place. If it not already set as value working folder, it will be set automatically
        /// </summary>
        public string WorkingFolder;

        private string _originalGitBranch;
        private readonly bool _mergeAllHistory;
        private DateTime _beginDate;
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

        private class TransactionDetail
        {
            public string Author;
            public string Comment;
            public DateTime MinCommitTime;
            public DateTime MaxCommitTime;
            public readonly List<VaultTransactionDetail> VaultTransactionDetails = new List<VaultTransactionDetail>();
        }

        public Processor(IGitProvider git, IVaultProvider vault, DateTime? beginDate, List<string> vaultSubdirectories)
        {
            _git = git;
            _vault = vault;
            _beginDate = beginDate ?? new DateTime(1990,1,1);
            _vaultSubdirectories = vaultSubdirectories;
            _mergeAllHistory = !beginDate.HasValue;
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
                    
                    // It's possible there was no commit to vault since _beginDate. To make behaviour consistent with all history merge style let's get the latest versions in case folder is empty
                    if (!_mergeAllHistory)
                    {
                        foreach (var vaultSubdirectory in _vaultSubdirectories.Where(subDir => !Directory.Exists(Path.Combine(WorkingFolder, subDir))))
                        {
                            var transactionDetail = _vault.VaultGetFolderVersionNearestBeforeDate(vaultRepoPath, vaultSubdirectory, _beginDate);
                            if (transactionDetail == null) continue;
                            _vault.VaultGetVersion($"{vaultRepoPath}/{vaultSubdirectory}", transactionDetail.Version, true);
                            GitCommit(new[] {transactionDetail}, doGitPush, vaultRepoPath, transactionDetail.TxId, gitBranch);
                        }
                    }

                    var txIds = new SortedSet<long>();
                    var directories = _vaultSubdirectories.Any() ? _vaultSubdirectories : new List<string>{ "" };
                    foreach (var vaultSubdirectory in directories)
                    {
                        //get current Git version
                        var currentGitVaultVersion = _git.GitVaultVersion(gitBranch, BuildVaultTag($"{vaultRepoPath}/{vaultSubdirectory}"));
                        //get vaultVersions
                        _vault.VaultPopulateInfo(vaultRepoPath, vaultSubdirectory, _beginDate, txIds, currentGitVaultVersion);
                    }

                    Log.Information($"init took {perBranchWatch.Elapsed}");

                    var counter = 0;
                    foreach (var txId in txIds)
                    {
                        var perTransactionWatch = Stopwatch.StartNew();
                        
                        ++counter;

                        var result = ProcessTransaction(vaultRepoPath, txId);
                        if (GitCommit(result.VaultTransactionDetails, doGitPush, vaultRepoPath, txId, gitBranch))
                        {
                            var commitTime = result.MinCommitTime != result.MaxCommitTime ? $"min={result.MinCommitTime},max={result.MaxCommitTime}" : result.MinCommitTime.ToString(CultureInfo.InvariantCulture);
                            Log.Information($"processing transaction {txId} took {perTransactionWatch.Elapsed}. Author: {result.Author}, Comment: {result.Comment}, commit time: {commitTime}");
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

        private bool GitCommit(IEnumerable<VaultTransactionDetail> transactionDetails, bool doGitPush, string vaultRepoPath, long txId, string gitBranch)
        {
            var committedAnything = false;
            foreach (var transactionDetail in transactionDetails)
            {
                var commitMsg = BuildCommitMessage($"{vaultRepoPath}/{transactionDetail.Subdirectory}", txId, transactionDetail.Comment);
                var subDir = string.IsNullOrEmpty(transactionDetail.Subdirectory) ? "." : transactionDetail.Subdirectory;
                var gitCommitId = _git.GitCommit(transactionDetail.Author, commitMsg, new DateTime(transactionDetail.CommitTime.Ticks), subDir);

                if (string.IsNullOrEmpty(gitCommitId)) continue;
                committedAnything = true;
                // Avoid moving back in time in case no commit was done after _beginDate and latest folder was checked out commit time may be older than _beginDate.
                if (transactionDetail.CommitTime.Ticks > _beginDate.Ticks)
                {
                    _beginDate = new DateTime(transactionDetail.CommitTime.Ticks);
                }

                // Mapping Vault Transaction ID to Git Commit SHA-1 Hash
                if (!_txidMappings.TryGetValue(txId, out var gitCommitIds))
                {
                    gitCommitIds = new HashSet<string>();
                    _txidMappings[txId] = gitCommitIds;
                }

                gitCommitIds.Add(gitCommitId);
            }

            if (doGitPush && committedAnything)
            {
                _git.GitPushOrigin(gitBranch);
            }

            return committedAnything;
        }

        private TransactionDetail ProcessTransaction(string vaultRepoPath, long txId)
        {
            var returnValue = new TransactionDetail();
            try
            {
                var subDirToTransactionDetail = new Dictionary<string, VaultTransactionDetail>();
                var movedRenamedPaths = new Dictionary<string, string>();
                var txnInfo = _vault.GetTxInfo(txId);
                returnValue.Author = txnInfo.userlogin;
                returnValue.Comment = txnInfo.changesetComment;

                // It has been noticed that renames tend to be listed at the end of txnInfo.items. Make sure this event precedes content updates
                var orderedItems = txnInfo.items.OrderBy(x => x.RequestType != VaultRequestType.Rename && x.RequestType != VaultRequestType.Move).ToList();
                if (orderedItems.Any())
                {
                    var minTime = orderedItems.Min(x => x.TxDate);
                    var maxTime = orderedItems.Max(x => x.TxDate);
                    var commitTime = minTime != maxTime ? $"min={minTime},max={maxTime}" : minTime.ToString();

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

                    returnValue.MaxCommitTime = new DateTime(Math.Max(returnValue.MaxCommitTime.Ticks, txDetailItem.TxDate.Ticks));
                    returnValue.MinCommitTime = new DateTime(Math.Min(returnValue.MinCommitTime.Ticks, txDetailItem.TxDate.Ticks));

                    if (!subDirToTransactionDetail.TryGetValue(vaultSubdirectory, out _))
                    {
                        var transactionDetail = ForceFullFolderGet
                            ? GetVaultSubdirectoryExactTxId(vaultRepoPath, vaultSubdirectory, txId)
                            : new VaultTransactionDetail
                            {
                                Author = txnInfo.userlogin,
                                Comment = txnInfo.changesetComment,
                                Subdirectory = vaultSubdirectory,
                                CommitTime = txDetailItem.TxDate,
                                Version = _vault.VaultGetFolderVersion($"{vaultRepoPath}/{vaultSubdirectory}", _beginDate, txId).Version,
                                TxId = txId
                            };
                        subDirToTransactionDetail[vaultSubdirectory] = transactionDetail;
                        returnValue.VaultTransactionDetails.Add(transactionDetail);
                    }
                    if (ForceFullFolderGet) continue;

                    var workingDirWithSubdir = Path.Combine(WorkingFolder, vaultSubdirectory);
                    var vaultPathWithSubdir = $"{vaultRepoPath}/{vaultSubdirectory}";
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
                        case VaultRequestType.Move:
                        case VaultRequestType.Rename:
                            ProcessFileItem(vaultPathWithSubdir, workingDirWithSubdir, txDetailItem, true);
                            var movedTo = Path.Combine(Path.GetDirectoryName(txDetailItem.ItemPath1), txDetailItem.ItemPath2).ToForwardSlash();
                            movedRenamedPaths.Add(txDetailItem.ItemPath1, movedTo);
                            continue;
                        case VaultRequestType.Share:
                            ProcessFileItem(vaultPathWithSubdir, workingDirWithSubdir, txDetailItem, false);
                            continue;

                        case VaultRequestType.CheckIn:
                        case VaultRequestType.CheckOut:
                        case VaultRequestType.LabelItem:
                        case VaultRequestType.AddFolder: // Nothing in a CopyBranch to do. Its just a place marker
                        case VaultRequestType.CopyBranch: // Git doesn't add empty folders
                            continue;
                    }

                    // Apply the changes from vault of the correct version for this file
                    var itemPath = string.IsNullOrEmpty(txDetailItem.ItemPath1) ? txDetailItem.Name : txDetailItem.ItemPath1;

                    try
                    {
                        var isMoved = movedRenamedPaths.TryGetValue(itemPath, out var movedTo);
                        if (isMoved)
                        {
                            Log.Debug($"Handling rename/move from {itemPath} to {movedTo}");
                        }

                        _vault.VaultGetVersion(isMoved ? movedTo : itemPath, txDetailItem.Version, false);
                    }
                    catch (Exception)
                    {
                        var folderPath = Path.GetDirectoryName(itemPath).ToForwardSlash();
                        var folderVersion = _vault.VaultGetFolderVersion(folderPath, _beginDate, txId).Version;
                        _vault.VaultGetVersion(folderPath, folderVersion, false);
                    }

                    if (!File.Exists(itemPath)) continue;

                    // Remove Source Code Control
                    switch (Path.GetExtension(itemPath).ToLower())
                    {
                        case ".sln":
                            RemoveSccFromSln(itemPath);
                            break;
                        case ".csproj":
                            RemoveSccFromCsProj(itemPath);
                            break;
                        case ".vdproj":
                            RemoveSccFromVdProj(itemPath);
                            break;
                    }
                }

                return returnValue;
            }
            catch (Exception)
            {
                // If an exception is thrown, presume its because a file has been requested which no longer exists in the tip of the repository.
                // That is, the file has been moved, renamed or deleted.
                // It may be accurate to search the txn details in above loop for request types of moved, renamed or deleted and
                // if one is found, execute this code rather than waiting for the exception. Just not sure that it will find everything. But
                // I know this code works, though it is much slower for repositories with a large number of files in each Version. Also, all the 
                // files that have been retrieved from the Server will still be in the client-side cache so the GetFile above is not wasted.
                // If we did not need this code then we would not need to use the Working Directory which would be a cleaner solution.
                try
                {
                    returnValue.VaultTransactionDetails.Clear();
                    returnValue.VaultTransactionDetails.AddRange(GetVaultSubdirectoriesExactTxId(vaultRepoPath, txId));
                    return returnValue;
                }
                catch (Exception e)
                {
                    Log.Error($"Cannot get transaction details for {txId}.\n{e}");
                    throw;
                }
            }
        }

        private IEnumerable<VaultTransactionDetail> GetVaultSubdirectoriesExactTxId(string vaultRepoPath, long txId)
        {
            var retVal = new Dictionary<string, VaultTransactionDetail>();
            foreach (var vaultSubdirectory in _vaultSubdirectories)
            {
                var transactionDetail = GetVaultSubdirectoryExactTxId(vaultRepoPath, vaultSubdirectory, txId);
                if (transactionDetail != null)
                {
                    retVal[vaultSubdirectory] = transactionDetail;
                }
            }

            return retVal.Values;
        }

        private VaultTransactionDetail GetVaultSubdirectoryExactTxId(string vaultRepoPath, string vaultSubdirectory, long txId)
        {
            var folderVersion = _vault.VaultGetFolderVersionExactTxId(vaultRepoPath, vaultSubdirectory, _beginDate, txId);

            if (folderVersion != null)
            {
                Log.Debug($"Getting {vaultRepoPath}/{vaultSubdirectory} recursively. TxID: {txId}, Time: {folderVersion.CommitTime}, Version: {folderVersion.Version}, Comment: {folderVersion.Comment}, Author: {folderVersion.Author}");
                folderVersion.Subdirectory = vaultSubdirectory;
                var targetDirectory = Path.Combine(WorkingFolder, vaultSubdirectory);
                if (Directory.Exists(targetDirectory))
                {
                    Statics.DeleteWorkingDirectory(targetDirectory);
                }

                _vault.VaultGetVersion($"{vaultRepoPath}/{vaultSubdirectory}", folderVersion.Version, true);

                var fsPath = Path.Combine(WorkingFolder, vaultSubdirectory);
                //change all sln files
                Directory.GetFiles(fsPath, "*.sln", SearchOption.AllDirectories)
                    //remove temp files created by vault
                    .Where(f => !f.Contains("~")).ToList().ForEach(RemoveSccFromSln);
                //change all csproj files
                Directory.GetFiles(fsPath, "*.csproj", SearchOption.AllDirectories)
                    //remove temp files created by vault
                    .Where(f => !f.Contains("~")).ToList().ForEach(RemoveSccFromCsProj);
                //change all vdproj files
                Directory.GetFiles(fsPath, "*.vdproj", SearchOption.AllDirectories)
                    //remove temp files created by vault
                    .Where(f => !f.Contains("~")).ToList().ForEach(RemoveSccFromVdProj);
            }

            return folderVersion;
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

        private static void ProcessFileItem(string vaultRepoPath, string workingFolder, VaultTxDetailHistoryItem txdetailitem, bool moveFiles)
        {
            // Convert the Vault path to a file system path
            var itemPath1 = string.Copy(txdetailitem.ItemPath1);
            var itemPath2 = string.Copy(txdetailitem.ItemPath2);

            // Ensure the files are withing the folder we are working with. 
            // If the source path is outside the current branch, throw an exception and let vault handle the processing because
            // we do not have the correct state of files outside the current branch.
            // If the target path is outside, ignore a file copy and delete a file move.
            // E.g. A Share can be shared outside of the branch we are working with
            Log.Debug($"Processing {itemPath1} to {itemPath2}. MoveFiles = {moveFiles})");
            var itemPath1WithinCurrentBranch = itemPath1.StartsWith(vaultRepoPath, true, CultureInfo.CurrentCulture);
            var itemPath2WithinCurrentBranch = itemPath2.StartsWith(vaultRepoPath, true, CultureInfo.CurrentCulture);

            if (!itemPath1WithinCurrentBranch)
            {
                Log.Debug("   Source file is outside of working folder. Error");
                throw new FileNotFoundException($"Source file is outside the current branch: {itemPath1}");
            }

            // Don't copy files outside of the branch
            if (!moveFiles && !itemPath2WithinCurrentBranch)
            {
                Log.Debug("   Ignoring target file outside of working folder");
                return;
            }

            itemPath1 = itemPath1.Replace(vaultRepoPath, workingFolder, StringComparison.CurrentCultureIgnoreCase);
            itemPath1 = itemPath1.Replace('/', '\\');

            itemPath2 = itemPath2.Replace(vaultRepoPath, workingFolder, StringComparison.CurrentCultureIgnoreCase);
            itemPath2 = itemPath2.Replace('/', '\\');

            if (File.Exists(itemPath1))
            {
                var directory2 = Path.GetDirectoryName(itemPath2);
                if (!Directory.Exists(directory2))
                {
                    Directory.CreateDirectory(directory2);
                }

                if (itemPath2WithinCurrentBranch && File.Exists(itemPath2))
                {
                    Log.Debug($"   Deleting {itemPath2}");
                    File.Delete(itemPath2);
                }

                if (moveFiles)
                {
                    // If target is outside of current branch, just delete the source file
                    if (!itemPath2WithinCurrentBranch)
                    {
                        Log.Debug($"   Deleting {itemPath1}");
                        File.Delete(itemPath1);
                    }
                    else
                    {
                        Log.Debug($"   Moving {itemPath2}");
                        File.Move(itemPath1, itemPath2);
                    }
                }
                else
                {
                    Log.Debug($"   Copying {itemPath1} to {itemPath2}");
                    File.Copy(itemPath1, itemPath2);
                }
            }
            else if (Directory.Exists(itemPath1))
            {
                if (moveFiles)
                {
                    // If target is outside of current branch, just delete the source directory
                    if (!itemPath2WithinCurrentBranch)
                    {
                        Directory.Delete(itemPath1);
                    }
                    else
                    {
                        Directory.Move(itemPath1, itemPath2);
                    }
                }
                else
                {
                    DirectoryCopy(itemPath1, itemPath2, true);
                }
            }
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            // Get the subdirectories for the specified directory.
            var dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException($"Source directory does not exist or could not be found: {sourceDirName}");
            }

            // If the destination directory doesn't exist, create it. 
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Get the files in the directory and copy them to the new location.
            foreach (var file in dir.GetFiles())
            {
                file.CopyTo(Path.Combine(destDirName, file.Name), false);
            }

            // If copying subdirectories, copy them and their contents to new location. 
            if (copySubDirs)
            {
                foreach (var subdir in dir.GetDirectories())
                {
                    DirectoryCopy(subdir.FullName, Path.Combine(destDirName, subdir.Name), copySubDirs);
                }
            }
        }

        private bool TryFindMatchingSubdir(string vaultRepo, string itemPath, out string matchingVaultSubdirectory)
        {

            if (!_vaultSubdirectories.Any())
            {
                matchingVaultSubdirectory = "";
                return true;
            }

            foreach (var vaultSubdirectory in _vaultSubdirectories)
            {
                if (itemPath.StartsWith($"{vaultRepo}/{vaultSubdirectory}/", true, CultureInfo.CurrentCulture)
                    || itemPath.Equals($"{vaultRepo}/{vaultSubdirectory}", StringComparison.CurrentCultureIgnoreCase))
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

        private string BuildCommitMessage(string repoPath, long trxId, string comment)
        {
            //parse path repo$RepoPath@version/trx
            var r = new StringBuilder(comment);
            r.AppendLine();
            r.AppendLine($"{BuildVaultTag(repoPath)}@{trxId}");
            return r.ToString();
        }

        private string BuildVaultTag(string repoPath) => $"{VaultTag} {_vault.VaultRepository}{repoPath}";

        private void VaultFinalize(string vaultRepoPath)
        {
            _vault.UnSetVaultWorkingFolder(vaultRepoPath);
            _git.GitCheckout(_originalGitBranch); // Return to original Git branch
        }
    }
}