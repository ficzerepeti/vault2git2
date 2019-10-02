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
        void VaultPopulateInfo(string vaultRepoPath, string vaultSubdirectory, ISet<long> txIds, long currentGitVaultVersion);
        void VaultGetVersion(string vaultPath, long vaultVersion, bool recursive);
        long? VaultGetFolderVersion(string folderPath, long txId);
        void VaultLogout();
        void SetVaultWorkingFolder(string repoPath, string diskPath);
        void UnSetVaultWorkingFolder(string repoPath);
        bool IsSetRootVaultWorkingFolder();
        string VaultRepository { get; }
    }
    
    public class VaultProvider : IVaultProvider
    {
        private string _originalWorkingFolder;
        
        public string VaultServer;
        public string VaultUser;
        public string VaultPassword;
        public string VaultRepository { get; set; }

        /// <summary>
        /// Gets a folder version for a transaction ID
        /// </summary>
        /// <param name="folderPath">Vault folder path</param>
        /// <param name="txId">transaction ID</param>
        /// <returns>Version if there's a matching transaction ID. Null in case folder was created after searched transaction. Exception otherwise</returns>
        public long? VaultGetFolderVersion(string folderPath, long txId)
        {
            var versions = ServerOperations.ProcessCommandVersionHistory(folderPath, -1, new VaultDateTime(1990, 1, 1), new VaultDateTime(2090, 1, 1), 0);
            var vaultHistoryItem = versions.FirstOrDefault(x => x.TxID == txId);
            if (vaultHistoryItem != null)
            {
                return vaultHistoryItem.Version;
            }

            var minTxId = versions.Min(x => x.TxID);
            if (minTxId > txId)
            {
                return null;
            }

            throw new Exception($"No matching history item found for {folderPath} at transaction ID {txId}");
        }

        public void VaultPopulateInfo(string repoPath, string subdirectory, ISet<long> txIds, long currentGitVaultVersion)
        {
            var beginDate = new VaultDateTime(1990, 1, 1);
            var endDate = new VaultDateTime(2090, 1, 1);
            foreach (var i in ServerOperations.ProcessCommandVersionHistory($"{repoPath}/{subdirectory}", 1, beginDate, endDate, 0))
            {
                if (i.TxID > currentGitVaultVersion)
                {
                    txIds.Add(i.TxID);
                }
            }
        }

        public void VaultGetVersion(string vaultPath, long vaultVersion, bool recursive)
        {
            // Allow exception to percolate up. Presume its due to a file missing from the latest Version 
            // that's in this Version. That is, this file is later deleted, moved or renamed.
            // apply version to the repo folder
            Log.Debug($"get {vaultPath} version {vaultVersion}");
            GetOperations.ProcessCommandGetVersion(vaultPath, Convert.ToInt32(vaultVersion),
                new GetOptions
                {
                    MakeWritable = MakeWritableType.MakeAllFilesWritable,
                    Merge = MergeType.OverwriteWorkingCopy,
                    OverrideEOL = VaultEOL.None,
                    //remove working copy does not work -- bug http://support.sourcegear.com/viewtopic.php?f=5&t=11145
                    PerformDeletions = PerformDeletionsType.RemoveWorkingCopy,
                    SetFileTime = SetFileTimeType.Modification,
                    Recursive = recursive
                });
            Log.Debug($"get {vaultPath} version {vaultVersion} SUCCESS!");
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
            ServerOperations.client.LoginOptions.URL = $"http://{VaultServer}/VaultService";
            ServerOperations.client.LoginOptions.User = VaultUser;
            ServerOperations.client.LoginOptions.Password = VaultPassword;
            ServerOperations.client.LoginOptions.Repository = VaultRepository;
            ServerOperations.Login();
            ServerOperations.client.MakeBackups = false;
            ServerOperations.client.AutoCommit = false;
            ServerOperations.client.Verbose = true;
        }

        public void VaultLogout() => ServerOperations.Logout();
    }
}