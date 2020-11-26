using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using VaultClientIntegrationLib;
using VaultClientOperationsLib;
using VaultLib;

namespace Vault2Git.Lib
{
    public class VaultTransactionDetail
    {
        public string Subdirectory;
        public VaultDateTime CommitTime;
        public string Comment;
        public string Author;
        public long Version;
        public long TxId;
    }
    
    public interface IVaultProvider
    {
        void VaultLogin();
        void VaultPopulateInfo(string vaultRepoPath, string vaultSubdirectory, ISet<long> txIds, long currentGitVaultVersion);
        TxInfo GetTxInfo(long txId);
        void VaultGetVersion(string repoPath, string itemPath, long vaultVersion, bool recursive);
        VaultTransactionDetail VaultGetTransactionDetail(string repoPath, string folderPath, long txId);
        VaultTransactionDetail VaultGetFolderVersionExactTxId(string repoPath, string folderPath, long txId);
        VaultTransactionDetail VaultGetFolderVersionNearestBeforeDate(string repoPath, string folderPath);
        void VaultLogout();
        void SetVaultWorkingFolder(string repoPath, string diskPath);
        void UnSetVaultWorkingFolder(string repoPath);
        bool IsSetRootVaultWorkingFolder();
        void BeginLabelQuery(string itemPath, long objId, out int rowsRetrievedInherited, out int rowsRetrievedRecursive, out string qryToken);
        void GetLabelQueryItems_Recursive(string qryToken, int begin, int end, out VaultLabelItemX[] vaultLabelItems);
        VaultClientTreeObject FindVaultTreeObjectAtReposOrLocalPath(string testPath);
        void EndLabelQuery(string qryToken);
        string VaultRepository { get; }
    }
    
    public class VaultProvider : IVaultProvider
    {
        private string _originalWorkingFolder;
        
        private readonly string _vaultServer;
        private readonly string _vaultUser;
        private readonly string _vaultPassword;
        private readonly VaultDateTime _beginDate;
        private readonly Dictionary<string, SortedDictionary<long, VaultTxHistoryItem>> _pathToTxIdsToTxDetailHistItem = new Dictionary<string, SortedDictionary<long, VaultTxHistoryItem>>();

        public VaultProvider(string vaultServer, string vaultRepository, string vaultUser, string vaultPassword, DateTime beginDate)
        {
            _vaultServer = vaultServer;
            VaultRepository = vaultRepository;
            _vaultUser = vaultUser;
            _vaultPassword = vaultPassword;
            _beginDate = new VaultDateTime(beginDate.Ticks);
        }

        public void BeginLabelQuery(string itemPath, long objId, out int rowsRetrievedInherited, out int rowsRetrievedRecursive, out string qryToken) =>
            ServerOperations.client.ClientInstance.BeginLabelQuery(itemPath,
                objId,
                true, // get recursive
                true, // get inherited
                true, // get file items
                true, // get folder items
                0, // no limit on results
                out rowsRetrievedInherited,
                out rowsRetrievedRecursive,
                out qryToken);

        public void GetLabelQueryItems_Recursive(string qryToken, int begin, int end, out VaultLabelItemX[] vaultLabelItems)
            => ServerOperations.client.ClientInstance.GetLabelQueryItems_Recursive(qryToken, begin, end, out vaultLabelItems);

        public VaultClientTreeObject FindVaultTreeObjectAtReposOrLocalPath(string testPath) => RepositoryUtil.FindVaultTreeObjectAtReposOrLocalPath(testPath);

        public void EndLabelQuery(string qryToken) => ServerOperations.client.ClientInstance.EndLabelQuery(qryToken);

        public string VaultRepository { get; }

        /// <summary>
        /// Gets a folder version for a transaction ID
        /// </summary>
        /// <param name="repoPath"></param>
        /// <param name="folderPath">Vault folder path</param>
        /// <param name="txId">transaction ID</param>
        /// <returns>Version if there's a matching transaction ID. Null in case folder was created after searched transaction. Exception otherwise</returns>
        public VaultTransactionDetail VaultGetTransactionDetail(string repoPath, string folderPath, long txId)
        {
            var txIdToHistItem = GetTxDetailHistoryItems(repoPath, folderPath);
            if (txIdToHistItem.TryGetValue(txId, out var vaultHistoryItem))
            {
                return new VaultTransactionDetail{Author = vaultHistoryItem.UserLogin, Comment = vaultHistoryItem.Comment, Subdirectory = folderPath, Version = vaultHistoryItem.Version, CommitTime = vaultHistoryItem.TxDate, TxId = txId};
            }

            if (txIdToHistItem.Count > 0 && txIdToHistItem.First().Key > txId)
            {
                return null;
            }

            throw new Exception($"No matching history item found for {folderPath} at transaction ID {txId}");
        }

        public VaultTransactionDetail VaultGetFolderVersionExactTxId(string repoPath, string folderPath, long txId)
        {
            var txIdToHistItem = GetTxDetailHistoryItems(repoPath, folderPath);
            return txIdToHistItem.TryGetValue(txId, out var vaultHistoryItem) ? new VaultTransactionDetail{Author = vaultHistoryItem.UserLogin, Comment = vaultHistoryItem.Comment, Subdirectory = folderPath, Version = vaultHistoryItem.Version, CommitTime = vaultHistoryItem.TxDate, TxId = txId} : null;
        }

        public VaultTransactionDetail VaultGetFolderVersionNearestBeforeDate(string repoPath, string folderPath)
        {
            var fullPath = MakeFullPath(repoPath, folderPath);
            var versions = ServerOperations.ProcessCommandVersionHistory(fullPath, -1, new VaultDateTime(1990,1,1), _beginDate, 1);
            var vaultHistoryItem = versions.FirstOrDefault();
            return vaultHistoryItem != null ? new VaultTransactionDetail{Author = vaultHistoryItem.UserLogin, Comment = vaultHistoryItem.Comment, Subdirectory = folderPath, Version = vaultHistoryItem.Version, CommitTime = vaultHistoryItem.TxDate, TxId = vaultHistoryItem.TxID} : null;
        }

        public void VaultPopulateInfo(string repoPath, string subdirectory, ISet<long> txIds, long currentGitVaultVersion)
        {
            var txIdToHistItem = GetTxDetailHistoryItems(repoPath, subdirectory);
            txIds.UnionWith(txIdToHistItem.Keys.Where(txId => txId > currentGitVaultVersion));
        }

        public TxInfo GetTxInfo(long txId) => ServerOperations.ProcessCommandTxDetail(txId);

        public void VaultGetVersion(string repoPath, string itemPath, long vaultVersion, bool recursive)
        {
            var fullPath = MakeFullPath(repoPath, itemPath);

            // Allow exception to percolate up. Presume its due to a file missing from the latest Version 
            // that's in this Version. That is, this file is later deleted, moved or renamed.
            // apply version to the repo folder
            Log.Debug($"get {fullPath} version {vaultVersion}");
            GetOperations.ProcessCommandGetVersion(fullPath, Convert.ToInt32(vaultVersion),
                new GetOptions
                {
                    MakeWritable = MakeWritableType.MakeAllFilesWritable,
                    Merge = MergeType.OverwriteWorkingCopy,
                    OverrideEOL = VaultEOL.None,
                    //remove working copy does not work -- bug http://support.sourcegear.com/viewtopic.php?f=5&t=11145
                    PerformDeletions = PerformDeletionsType.RemoveWorkingCopy,
                    SetFileTime = SetFileTimeType.Modification,
                    Recursive = recursive || string.IsNullOrEmpty(itemPath)
                });
            Log.Debug($"get {fullPath} version {vaultVersion} SUCCESS!");
        }

        public void SetVaultWorkingFolder(string repoPath, string diskPath)
        {
            // Save the current working folder
            var list = ServerOperations.GetWorkingFolderAssignments();
            foreach (DictionaryEntry dict in list)
            {
                if (dict.Key.ToString().Equals(repoPath, StringComparison.OrdinalIgnoreCase))
                {
                    _originalWorkingFolder = dict.Value.ToString();
                    break;
                }
            }

            try
            {
                ServerOperations.SetWorkingFolder(repoPath, diskPath, true);
            }
            catch (WorkingFolderConflictException ex)
            {
                // Remove the working folder assignment and try again
                ServerOperations.RemoveWorkingFolder((string) ex.ConflictList[0]);
                ServerOperations.SetWorkingFolder(repoPath, diskPath, true);
            }
        }

        public void UnSetVaultWorkingFolder(string repoPath)
        {
            //remove any assignment first
            //it is case sensitive, so we have to find how it is recorded first
            var exPath = ServerOperations.GetWorkingFolderAssignments().Cast<DictionaryEntry>()
                .Select(e => e.Key.ToString()).FirstOrDefault(e => repoPath.Equals(e, StringComparison.OrdinalIgnoreCase));
            if (null != exPath)
                ServerOperations.RemoveWorkingFolder(exPath);

            if (_originalWorkingFolder != null)
            {
                ServerOperations.SetWorkingFolder(repoPath, _originalWorkingFolder, true);
                _originalWorkingFolder = null;
            }
        }

        public bool IsSetRootVaultWorkingFolder()
        {
            var exPath = ServerOperations.GetWorkingFolderAssignments().Cast<DictionaryEntry>().Select(e => e.Key.ToString()).FirstOrDefault(e => "$".Equals(e, StringComparison.OrdinalIgnoreCase));
            if (null == exPath)
            {
                Log.Information("Root working folder is not set. It must be set so that files referred to outside of git repo may be retrieved. Will terminate on enter");
                return false;
            }

            return true;
        }

        public void VaultLogin()
        {
            ServerOperations.client.LoginOptions.URL = $"http://{_vaultServer}/VaultService";
            ServerOperations.client.LoginOptions.User = _vaultUser;
            ServerOperations.client.LoginOptions.Password = _vaultPassword;
            ServerOperations.client.LoginOptions.Repository = VaultRepository;
            ServerOperations.Login();
            ServerOperations.client.MakeBackups = false;
            ServerOperations.client.AutoCommit = false;
            ServerOperations.client.Verbose = true;
        }

        public void VaultLogout() => ServerOperations.Logout();

        private SortedDictionary<long, VaultTxHistoryItem> GetTxDetailHistoryItems(string repoPath, string subdirectory)
        {
            var fullPath = MakeFullPath(repoPath, subdirectory);
            if (_pathToTxIdsToTxDetailHistItem.TryGetValue(fullPath, out var beginDateAndTxDetailItems))
            {
                return beginDateAndTxDetailItems;
            }
            var versions = ServerOperations.ProcessCommandVersionHistory(fullPath, -1, _beginDate, new VaultDateTime(2090,1,1), 0);
            var txIdToHistItem = new SortedDictionary<long, VaultTxHistoryItem>(versions.ToDictionary(x => x.TxID, x => x));
            _pathToTxIdsToTxDetailHistItem[fullPath] = txIdToHistItem;
            return txIdToHistItem;
        }

        private static string MakeFullPath(string repoPath, string itemPath) => string.IsNullOrEmpty(itemPath) ? repoPath : $"{repoPath}/{itemPath}";
    }
}