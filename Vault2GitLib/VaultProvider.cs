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
    public interface IVaultProvider
    {
        void VaultLogin();
        List<VaultTxHistoryItem> VaultGetTxHistoryItems(string vaultRepoPath, string vaultSubdirectory);
        TxInfo GetTxInfo(long txId);
        void VaultGetVersion(string repoPath, string itemPath, long vaultVersion, bool recursive);
        VaultTxHistoryItem VaultGetFolderVersionExactTxId(string repoPath, string folderPath, long txId);
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
        private readonly Dictionary<string, List<VaultTxHistoryItem>> _pathToTxIdsToTxDetailHistItem = new Dictionary<string, List<VaultTxHistoryItem>>();

        public VaultProvider(string vaultServer, string vaultRepository, string vaultUser, string vaultPassword)
        {
            _vaultServer = vaultServer;
            VaultRepository = vaultRepository;
            _vaultUser = vaultUser;
            _vaultPassword = vaultPassword;
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

        public VaultTxHistoryItem VaultGetFolderVersionExactTxId(string repoPath, string folderPath, long txId)
        {
            var txIdToHistItem = GetTxDetailHistoryItems(repoPath, folderPath);
            return txIdToHistItem.FirstOrDefault(x => x.TxID == txId);
        }

        public List<VaultTxHistoryItem> VaultGetTxHistoryItems(string repoPath, string subdirectory)
        {
            return GetTxDetailHistoryItems(repoPath, subdirectory);
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

        private List<VaultTxHistoryItem> GetTxDetailHistoryItems(string repoPath, string subdirectory)
        {
            var fullPath = MakeFullPath(repoPath, subdirectory);
            if (_pathToTxIdsToTxDetailHistItem.TryGetValue(fullPath, out var txIdToHistItem))
            {
                return txIdToHistItem;
            }

            var versions = ServerOperations.ProcessCommandVersionHistory(fullPath, -1, new VaultDateTime(1990,1,1), new VaultDateTime(2090,1,1), 0);
            txIdToHistItem = new List<VaultTxHistoryItem>(versions.Reverse());
            _pathToTxIdsToTxDetailHistItem[fullPath] = txIdToHistItem;
            return txIdToHistItem;
        }

        private static string MakeFullPath(string repoPath, string itemPath) => string.IsNullOrEmpty(itemPath) ? repoPath : $"{repoPath}/{itemPath}";
    }
}