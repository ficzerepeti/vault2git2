﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VaultClientIntegrationLib;
using VaultClientOperationsLib;
using VaultLib;
using System.Xml;

namespace Vault2Git.Lib
{
    public static class Statics
    {
       public static String Replace(this String str, string oldValue, string newValue, StringComparison comparision)
       {
          StringBuilder sb = new StringBuilder();

          int previousIndex = 0;
          int index = str.IndexOf(oldValue, comparision);
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
          bool fDeleteDirectory = true;

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
           string [] subdirectoryEntries = Directory.GetDirectories(targetDirectory);
           foreach (string subdirectory in subdirectoryEntries)
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
        /// path to git.exe
        /// </summary>
        public string GitCmd;

        /// <summary>
        /// path where conversion will take place. If it not already set as value working folder, it will be set automatically
        /// </summary>
        public string WorkingFolder;

        public string OriginalWorkingFolder;
        public string OriginalGitBranch;

        public string VaultServer;
        public string VaultUser;
        public string VaultPassword;
        public string VaultRepository;
        public List<string> VaultSubdirectories;

        public string GitDomainName;

        //callback
        public Func<long, TimeSpan, bool> Progress;

        //flags
        public bool SkipEmptyCommits = false;
        public bool Verbose = false;
        public bool Pause = false;
        public bool ForceFullFolderGet = false;

        //private vars
        /// <summary>
        /// Maps Vault TransactionID to Git Commit SHA-1 Hash
        /// </summary>
        private readonly IDictionary<long, HashSet<string>> _txidMappings = new Dictionary<long, HashSet<string>>();

        //constants
        private const int GitGcInterval = 200;
        private const string VaultTag = "[git-vault-id]";

        /// <summary>
        /// version number reported to <see cref="Progress"/> when init is complete
        /// </summary>
        public const int ProgressSpecialVersionInit = 0;
        
        /// <summary>
        /// version number reported to <see cref="Progress"/> when git gc is complete
        /// </summary>
        public const int ProgressSpecialVersionGc = -1;

        /// <summary>
        /// version number reported to <see cref="Progress"/> when finalization finished (e.g. logout, unset wf etc)
        /// </summary>
        public const int ProgressSpecialVersionFinalize = -2;

        /// <summary>
        /// version number reported to <see cref="Progress"/> when git tags creation is completed
        /// </summary>
        public const int ProgressSpecialVersionTags = -3;

        /// <summary>
        /// Pulls versions
        /// </summary>
        /// <param name="git2VaultRepoPath">Key=git, Value=vault</param>
        /// <param name="limitCount"></param>
        /// <returns></returns>
        public bool Pull(IEnumerable<KeyValuePair<string, string>> git2VaultRepoPath, long limitCount)
        {
            //get git current branch name
            gitCurrentBranch(out OriginalGitBranch);
            Console.WriteLine($"Starting git branch is {OriginalGitBranch}");
            
            //reorder target branches to start from current branch, so don't need to do checkout for first branch
            var targetList = git2VaultRepoPath.OrderByDescending(p => p.Key.Equals(OriginalGitBranch, StringComparison.CurrentCultureIgnoreCase));

            try
            {
                vaultLogin();
                foreach (var pair in targetList)
                {
                    var perBranchWatch = Stopwatch.StartNew();
                   
                    var gitBranch = pair.Key;
                    var vaultRepoPath = pair.Value;

                    Console.WriteLine($"\nProcessing git branch {gitBranch}");
                    
                    Init(vaultRepoPath, gitBranch);
                    if (!IsSetRootVaultWorkingFolder())
                    {
                       Environment.Exit(1);
                    }

                    var txIds = new SortedSet<long>();
                    foreach (var vaultSubdirectory in VaultSubdirectories)
                    {
                       //get current Git version
                       long currentGitVaultVersion = 0; // TODO: this should become TxID
                       gitVaultVersion(gitBranch, vaultRepoPath, vaultSubdirectory, ref currentGitVaultVersion);
                       //get vaultVersions
                       vaultPopulateInfo(vaultRepoPath, vaultSubdirectory, txIds, currentGitVaultVersion);
                    }

                    //do init only if there is something to work on
                    if (!txIds.Any())
                        return false;

                    //report init
                    if (null != Progress)
                        if (Progress(ProgressSpecialVersionInit, perBranchWatch.Elapsed))
                            return true;

                    var counter = 0;
                    foreach (var txId in txIds)
                    {
                       var perTransactionWatch = Stopwatch.StartNew();
                       
                       var affectedSubdirs = new HashSet<string>();
                       var txnInfo = ServerOperations.ProcessCommandTxDetail(txId);
                       var movedRenamedPaths = new Dictionary<string, string>();
                       // It has been noticed that renames tend to be listed at the end of txnInfo.items. Make sure this event precedes content updates
                       var orderedItems = txnInfo.items.OrderBy(x => x.RequestType != VaultRequestType.Rename && x.RequestType != VaultRequestType.Move).ToList();
                       
                       try
                       {
                          if (ForceFullFolderGet)
                          {
                             throw new Exception("Force full folder get");
                          }
                       
                          foreach (var txDetailItem in orderedItems)
                          {
                             if (!TryFindMatchingSubdir(vaultRepoPath, txDetailItem.ItemPath1, out var vaultSubdirectory))
                             {
                                continue;
                             }
                             affectedSubdirs.Add(vaultSubdirectory);
                             var vaultPathWithSubdir = $"{vaultRepoPath}/{vaultSubdirectory}";
                             var workingDirWithSubdir = Path.Combine(WorkingFolder, vaultSubdirectory);
                        
                             switch (txDetailItem.RequestType)
                             {
                                // Do deletions, renames and moves ourselves
                                case VaultRequestType.Delete:
                                {
                                   var suffix = txDetailItem.ItemPath1.Substring(vaultPathWithSubdir.Length);
                                   var filesystemPath = Path.Combine(WorkingFolder, suffix);
                              
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
                                case VaultRequestType.AddFolder: // Nothing in a CopyBranch to do. Its just a place marker
                                case VaultRequestType.CopyBranch: // Git doesn't add empty folders
                                   continue;
                             }

                             // Apply the changes from vault of the correct version for this file 
                             try
                             {
                                if (movedRenamedPaths.TryGetValue(txDetailItem.ItemPath1, out var itemPath))
                                {
                                   if (Verbose) Console.WriteLine($"Handling rename/move from {txDetailItem.ItemPath1} to {itemPath}");
                                }
                                else
                                {
                                   itemPath = txDetailItem.ItemPath1;
                                }
                                vaultGetVersion(itemPath, txDetailItem.Version, false);
                             }
                             catch (Exception)
                             {
                                var folderPath = Path.GetDirectoryName(txDetailItem.ItemPath1).ToForwardSlash();
                                var folderVersion = vaultGetFolderVersion(folderPath, txId);
                                vaultGetVersion(folderPath, folderVersion.Value, false);
                             }

                             if (!File.Exists(txDetailItem.ItemPath1)) continue;
                        
                             // Remove Source Code Control
                             switch (Path.GetExtension(txDetailItem.ItemPath1).ToLower())
                             {
                                case ".sln":
                                   removeSCCFromSln(txDetailItem.ItemPath1);
                                   break;
                                case ".csproj":
                                   removeSCCFromCSProj(txDetailItem.ItemPath1);
                                   break;
                                case ".vdproj":
                                   removeSCCFromVDProj(txDetailItem.ItemPath1);
                                   break;
                             }
                          }
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
                             var subdirsToGet = new HashSet<string>();
                             foreach (var itemPath in txnInfo.items.Select(x => x.ItemPath1))
                             {
                                if (TryFindMatchingSubdir(vaultRepoPath, itemPath, out var vaultSubdirectory))
                                {
                                   subdirsToGet.Add(vaultSubdirectory);
                                }
                             }
                             foreach (var vaultSubdirectory in subdirsToGet)
                             {
                                var targetDirectory = Path.Combine(WorkingFolder, vaultSubdirectory);
                                if (Directory.Exists(targetDirectory))
                                {
                                   Statics.DeleteWorkingDirectory(targetDirectory);
                                }
                                var folderPath = $"{vaultRepoPath}/{vaultSubdirectory}";
                                var folderVersion = vaultGetFolderVersion(folderPath, txId);
                                if (folderVersion.HasValue)
                                {
                                   vaultGetVersion(folderPath, folderVersion.Value, true);
                                }
                             }

                             //change all sln files
                             Directory.GetFiles(WorkingFolder, "*.sln", SearchOption.AllDirectories)
                                //remove temp files created by vault
                                .Where(f => !f.Contains("~")).ToList().ForEach(f => removeSCCFromSln(f));
                             //change all csproj files
                             Directory.GetFiles(WorkingFolder, "*.csproj", SearchOption.AllDirectories)
                                //remove temp files created by vault
                                .Where(f => !f.Contains("~")).ToList().ForEach(f => removeSCCFromCSProj(f));
                             //change all vdproj files
                             Directory.GetFiles(WorkingFolder, "*.vdproj", SearchOption.AllDirectories)
                                //remove temp files created by vault
                                .Where(f => !f.Contains("~")).ToList().ForEach(f => removeSCCFromVDProj(f));
                          }
                          catch (Exception e)
                          {
                             throw new Exception($"Cannot get transaction details for {txId}: {e}");
                          }
                       }

                       if (Pause)
                       {
                          Console.WriteLine("Pause before commit. Enter to continue.");
                          Console.ReadLine();
                       }

                       //commit
                       foreach (var affectedSubdir in affectedSubdirs)
                       {
                          var commitMsg = buildCommitMessage($"{vaultRepoPath}/{affectedSubdir}", txId, txnInfo.changesetComment);
                           
                          gitCommit(txnInfo.userlogin, txId, GitDomainName, commitMsg, new DateTime(txnInfo.items.First().ModDate.Ticks));
                          
                          //call gc
                          if (0 == ++counter % GitGcInterval)
                          {
                             var gcWatch = Stopwatch.StartNew();
                             gitGC();
                             if (null != Progress && Progress(ProgressSpecialVersionGc, gcWatch.Elapsed)) return true;
                          }
                       }
                       
                       if (null != Progress)
                          if (Progress(txId, perTransactionWatch.Elapsed))
                             return true;
                       
                       //check if limit is reached
                       if (counter >= limitCount)
                          break;
                    }

                    vaultFinalize(vaultRepoPath);
                }
            }
            finally
            {
               var finalizeWatch = Stopwatch.StartNew();
               Console.WriteLine("\n");

               //complete
                vaultLogout();

                //finalize git (update server info for dumb clients)
                gitFinalize();
                Progress?.Invoke(ProgressSpecialVersionFinalize, finalizeWatch.Elapsed);
            }
            return false;
        }

        /// <summary>
        /// Gets a folder version for a transaction ID
        /// </summary>
        /// <param name="folderPath">Vault folder path</param>
        /// <param name="txId">transaction ID</param>
        /// <returns>Version if there's a matching transaction ID. Null in case folder was created after searched transaction. Exception otherwise</returns>
        private long? vaultGetFolderVersion(string folderPath, long txId)
        {
           var versions = ServerOperations.ProcessCommandVersionHistory(folderPath, -1, new VaultDateTime(1990,1,1), new VaultDateTime(2090,1,1), 0);
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


        /// <summary>
        /// removes Source control refs from sln files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        private static void removeSCCFromSln(string filePath)
        {
            var lines = File.ReadAllLines(filePath).ToList();
            //scan lines 
            var searchingForStart = true;
            var beginingLine = 0;
            var endingLine = 0;
            var currentLine = 0;
            foreach(var line in lines)
            {
                var trimmedLine = line.Trim();
                if (searchingForStart)
                {
                    if (trimmedLine.StartsWith("GlobalSection(SourceCodeControl)"))
                    {
                        beginingLine = currentLine;
                        searchingForStart = false;
                    }
                } else
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
            if (beginingLine >0 & endingLine > 0)
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
        public static void removeSCCFromCSProj(string filePath)
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
                Console.WriteLine("Failed for {0}", filePath);
                throw;
            }
        }

        /// <summary>
        /// removes Source control refs from vdProj files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        private static void removeSCCFromVDProj(string filePath)
        {
            var lines = File.ReadAllLines(filePath).ToList();
            File.WriteAllLines(filePath, lines.Where(l => !l.Trim().StartsWith(@"""Scc")).ToArray(), Encoding.UTF8);
        }

        private void vaultPopulateInfo(string repoPath, string subdirectory, ISet<long> txIds, long currentGitVaultVersion)
        {
            var beginDate = new VaultDateTime(1990,1,1);
            var endDate = new VaultDateTime(2090,1,1);
            foreach (var i in ServerOperations.ProcessCommandVersionHistory($"{repoPath}/{subdirectory}", 1, beginDate, endDate,  0))
            {
               if (i.TxID > currentGitVaultVersion)
               {
                  txIds.Add(i.TxID);
               }
            }
        }

        /// <summary>
        /// Creates Git tags from Vault labels
        /// </summary>
        /// <param name="repositoryFolderPath"></param>
        /// <returns></returns>
        public bool CreateTagsFromLabels(string repositoryFolderPath = "$")
        {
            var labelWatch = Stopwatch.StartNew();
            Console.WriteLine( "Creating tags from labels...");
            vaultLogin();

            // Search for all labels recursively
            long objId = RepositoryUtil.FindVaultTreeObjectAtReposOrLocalPath(repositoryFolderPath).ID;

            ServerOperations.client.ClientInstance.BeginLabelQuery(repositoryFolderPath,
                                                                                   objId,
                                                                                   true, // get recursive
                                                                                   true, // get inherited
                                                                                   true, // get file items
                                                                                   true, // get folder items
                                                                                   0, // no limit on results
                                                                                   out _,
                                                                                   out var rowsRetRecur,
                                                                                   out var qryToken);


            ServerOperations.client.ClientInstance.GetLabelQueryItems_Recursive(qryToken,
                                                                                0,
                                                                                rowsRetRecur,
                                                                                out var labelItems);

            try
            {
               if (labelItems != null)
               {
                  foreach (VaultLabelItemX currItem in labelItems)
                  {
                     if (!_txidMappings.TryGetValue(currItem.TxID, out var gitCommitIds))
                        continue;

                     var gitCommitId = string.Join(",", gitCommitIds);
                     if (!string.IsNullOrEmpty(gitCommitId))
                     {
                        var gitLabelName = Regex.Replace(currItem.Label, "[\\W]", "_");
                        gitAddTag(currItem.TxID + "_" + gitLabelName, gitCommitId, currItem.Comment);
                     }
                  }
               }

               //add ticks for git tags
               Progress?.Invoke(ProgressSpecialVersionTags, labelWatch.Elapsed);
            }
            finally
            {
               //complete
               ServerOperations.client.ClientInstance.EndLabelQuery(qryToken);
               vaultLogout();
               gitFinalize();
            }
            return true;
        }

        private void vaultGetVersion(string vaultPath, long vaultVersion, bool recursive)
        {
           // Allow exception to percolate up. Presume its due to a file missing from the latest Version 
           // that's in this Version. That is, this file is later deleted, moved or renamed.
           // apply version to the repo folder
           if (Verbose) Console.WriteLine($"get {vaultPath} version {vaultVersion}");
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
           if (Verbose) Console.WriteLine($"get {vaultPath} version {vaultVersion} SUCCESS!");
        }

        private void ProcessFileItem( String vaultRepoPath, String workingFolder, VaultTxDetailHistoryItem txdetailitem, bool moveFiles )
        {
            // Convert the Vault path to a file system path
            String ItemPath1 = String.Copy(txdetailitem.ItemPath1);
            String ItemPath2 = String.Copy(txdetailitem.ItemPath2);

            // Ensure the files are withing the folder we are working with. 
            // If the source path is outside the current branch, throw an exception and let vault handle the processing because
            // we do not have the correct state of files outside the current branch.
            // If the target path is outside, ignore a file copy and delete a file move.
            // E.g. A Share can be shared outside of the branch we are working with
            if (Verbose) Console.WriteLine($"Processing {ItemPath1} to {ItemPath2}. MoveFiles = {moveFiles})");
            bool ItemPath1WithinCurrentBranch = ItemPath1.StartsWith(vaultRepoPath, true, CultureInfo.CurrentCulture);
            bool ItemPath2WithinCurrentBranch = ItemPath2.StartsWith(vaultRepoPath, true, CultureInfo.CurrentCulture);

            if (!ItemPath1WithinCurrentBranch)
            {
               if (Verbose) Console.WriteLine("   Source file is outside of working folder. Error");
               throw new FileNotFoundException($"Source file is outside the current branch: {ItemPath1}");
            }

            // Don't copy files outside of the branch
            if (!moveFiles && !ItemPath2WithinCurrentBranch)
            {
               if (Verbose) Console.WriteLine("   Ignoring target file outside of working folder");
               return;
            }

            ItemPath1 = ItemPath1.Replace(vaultRepoPath, workingFolder, StringComparison.CurrentCultureIgnoreCase);
            ItemPath1 = ItemPath1.Replace('/', '\\');

            ItemPath2 = ItemPath2.Replace(vaultRepoPath, workingFolder, StringComparison.CurrentCultureIgnoreCase);
            ItemPath2 = ItemPath2.Replace('/', '\\');

            if (File.Exists(ItemPath1))
            {
               string directory2 = Path.GetDirectoryName(ItemPath2);
               if (!Directory.Exists(directory2))
               {
                  Directory.CreateDirectory(directory2);
               }

               if (ItemPath2WithinCurrentBranch && File.Exists(ItemPath2))
               {
                  if (Verbose) Console.WriteLine("   Deleting {0}", ItemPath2 );
                  File.Delete(ItemPath2);
               }

               if (moveFiles)
               {
                  // If target is outside of current branch, just delete the source file
                  if (!ItemPath2WithinCurrentBranch)
                  {
                     if (Verbose) Console.WriteLine("   Deleting {0}", ItemPath1);
                     File.Delete(ItemPath1);
                  }
                  else
                  {
                     if (Verbose) Console.WriteLine("   Moving {0}", ItemPath2);
                     File.Move(ItemPath1, ItemPath2);
                  }
               }
               else
               {
                  if (Verbose) Console.WriteLine("   Copying {0} to [1]", ItemPath1, ItemPath2);
                  File.Copy(ItemPath1, ItemPath2);
               }
            }
            else if (Directory.Exists(ItemPath1))
            {
               if (moveFiles)
               {
                  // If target is outside of current branch, just delete the source directory
                  if (!ItemPath2WithinCurrentBranch)
                  {
                     Directory.Delete(ItemPath1);
                  }
                  else
                  {
                     Directory.Move(ItemPath1, ItemPath2);
                  }
               }
               else
               {
                  DirectoryCopy(ItemPath1, ItemPath2, true);
               }
            }
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
           // Get the subdirectories for the specified directory.
           DirectoryInfo dir = new DirectoryInfo(sourceDirName);
           DirectoryInfo[] dirs = dir.GetDirectories();

           if (!dir.Exists)
           {
              throw new DirectoryNotFoundException(
                  "Source directory does not exist or could not be found: "
                  + sourceDirName);
           }

           // If the destination directory doesn't exist, create it. 
           if (!Directory.Exists(destDirName))
           {
              Directory.CreateDirectory(destDirName);
           }

           // Get the files in the directory and copy them to the new location.
           FileInfo[] files = dir.GetFiles();
           foreach (FileInfo file in files)
           {
              string temppath = Path.Combine(destDirName, file.Name);
              file.CopyTo(temppath, false);
           }

           // If copying subdirectories, copy them and their contents to new location. 
           if (copySubDirs)
           {
              foreach (DirectoryInfo subdir in dirs)
              {
                 string temppath = Path.Combine(destDirName, subdir.Name);
                 DirectoryCopy(subdir.FullName, temppath, copySubDirs);
              }
           }
        }
        
        private bool TryFindMatchingSubdir(string vaultRepo, string itemPath, out string matchingVaultSubdirectory)
        {
           foreach (var vaultSubdirectory in VaultSubdirectories)
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

        private void gitVaultVersion(string gitBranch, string vaultRepoPath, string vaultSubdirectory, ref long currentVersion)
        {
            currentVersion = 0;
            try
            {
               //get commit message
               gitLog(gitBranch, $"{vaultRepoPath}/{vaultSubdirectory}", out var msgs);
               //get vault version from commit message
               currentVersion = getVaultTrxIdFromGitLogMessage(msgs);

               if (currentVersion == 0)
               {
                  Console.WriteLine("Restart limit exceeded. Conversion will start from Version 1. Is this correct? Y/N");
                  var input = Console.ReadLine();
                  if (input != null && !(input[0] == 'Y' || input[0] == 'y'))
                  {
                     throw new Exception("Restart commit message not located in git");
                  }
               }
            }
            catch (InvalidOperationException)
            {
               Console.WriteLine("Searched all commits and failed to find a restart point. Conversion will start from Version 1. Is this correct? Y/N");
               var input = Console.ReadLine();
               if (input != null && !(input[0] == 'Y' || input[0] == 'y'))
               {
                  Environment.Exit(2);
               }
            }
        }

        private void Init(string vaultRepoPath, string gitBranch)
        {
            //set working folder
            setVaultWorkingFolder(vaultRepoPath);
            //checkout branch
            for (int tries = 0; ; tries++)
            {
                runGitCommand($"checkout --quiet --force {gitBranch}", string.Empty, out _);
                //confirm current branch (sometimes checkout failed)
                gitCurrentBranch(out var currentBranch);
                if (gitBranch.Equals(currentBranch, StringComparison.OrdinalIgnoreCase))
                    break;
                if (tries > 5)
                    throw new Exception("cannot switch branches");
            }
        }

        private void vaultFinalize(string vaultRepoPath)
        {
            unSetVaultWorkingFolder(vaultRepoPath);
            runGitCommand($"checkout --quiet --force {OriginalGitBranch}", string.Empty, out _); // Return to original Git branch
        }

        // vaultLogin is the user name as known in Vault e.g. 'robert' which needs to be mapped to rob.goodridge
        private void gitCommit(string vaultLogin, long vaultTrxid, string gitDomainName, string vaultCommitMessage, DateTime commitTimeStamp)
        {
            this.gitCurrentBranch(out var gitCurrentBranch);

            runGitCommand("add --force --all .", string.Empty, out var msgs);
            if (SkipEmptyCommits)
            {
                //checking status
                runGitCommand("status --porcelain", string.Empty, out msgs);
                if (!msgs.Any())
                    return;
            }
            runGitCommand($@"commit --allow-empty --all --date=""{commitTimeStamp:s}"" --author=""{vaultLogin} <{vaultLogin}@{gitDomainName}>"" -F -", vaultCommitMessage, out msgs);

            // Mapping Vault Transaction ID to Git Commit SHA-1 Hash
            if (msgs[0].StartsWith("[" + gitCurrentBranch))
            {
                var gitCommitId = msgs[0].Split(' ')[1];
                gitCommitId = gitCommitId.Substring(0, gitCommitId.Length - 1);
                if (!_txidMappings.TryGetValue(vaultTrxid, out var gitCommitIds))
                {
                   gitCommitIds = new HashSet<string>();
                   _txidMappings[vaultTrxid] = gitCommitIds;
                }
                gitCommitIds.Add(gitCommitId);
            }
        }

        private void gitCurrentBranch(out string currentBranch)
        {
            runGitCommand("branch", string.Empty, out var msgs);
            currentBranch = msgs.FirstOrDefault(s => s.StartsWith("*"))?.Substring(1).Trim();
            currentBranch = currentBranch ?? "master";
        }

        private string buildCommitMessage(string repoPath, long trxId, string comment)
        {
            //parse path repo$RepoPath@version/trx
            var r = new StringBuilder(comment);
            r.AppendLine();
            r.AppendLine($"{BuildVaultTag(repoPath)}@{trxId}");
            return r.ToString();
        }

        private string BuildVaultTag(string repoPath) => $"{VaultTag} {VaultRepository}{repoPath}";

        private long getVaultTrxIdFromGitLogMessage(IEnumerable<string> msg)
        {
            //get last string
            var stringToParse = msg.Last();
            //search for version tag
            var versionString = stringToParse.Split(new[] {VaultTag}, StringSplitOptions.None).LastOrDefault();
            //parse path reporepoPath@version/trx
            //get version/trx part
            var versionTrxTag = versionString?.Split('@').LastOrDefault();
            if (versionTrxTag == null)
                return 0;

            //get version
            long.TryParse(versionTrxTag, out var version);
            return version;
        }

        private void gitLog(string gitBranch, string vaultRepoPath, out string[] msg) => runGitCommand($"log {gitBranch} --grep \"{Regex.Escape(BuildVaultTag(vaultRepoPath))}\" -n 1", string.Empty, out msg);
        private void gitAddTag(string gitTagName, string gitCommitId, string gitTagComment) => runGitCommand($@"tag {gitTagName} {gitCommitId} -a -m ""{gitTagComment}""", string.Empty, out _);
        private void gitGC() => runGitCommand("gc --auto", string.Empty, out _);
        private void gitFinalize() => runGitCommand("update-server-info", string.Empty, out _);

        private void setVaultWorkingFolder(string repoPath)
        {
            // Save the current working folder
            var list = ServerOperations.GetWorkingFolderAssignments();
            foreach (DictionaryEntry dict in list)
            {
               if (dict.Key.ToString().Equals(repoPath, StringComparison.OrdinalIgnoreCase))
               {
                  OriginalWorkingFolder = dict.Value.ToString();
                  break;
               }
            }

            try
            {
               ServerOperations.SetWorkingFolder(repoPath, WorkingFolder, true );
            }
            catch (WorkingFolderConflictException ex)
            {
               // Remove the working folder assignment and try again
               ServerOperations.RemoveWorkingFolder((string)ex.ConflictList[0]);
               ServerOperations.SetWorkingFolder(repoPath, WorkingFolder, true );
            }
        }

        private void unSetVaultWorkingFolder(string repoPath)
        {
            //remove any assignment first
            //it is case sensitive, so we have to find how it is recorded first
            var exPath = ServerOperations.GetWorkingFolderAssignments().Cast<DictionaryEntry>()
                  .Select(e => e.Key.ToString()).FirstOrDefault(e => repoPath.Equals(e, StringComparison.OrdinalIgnoreCase));
            if (null != exPath)
               ServerOperations.RemoveWorkingFolder(exPath);

            if (OriginalWorkingFolder != null)
            {
               ServerOperations.SetWorkingFolder(repoPath, OriginalWorkingFolder, true);
               OriginalWorkingFolder = null;
            }
        }

        private bool IsSetRootVaultWorkingFolder()
        {
           var exPath = ServerOperations.GetWorkingFolderAssignments().Cast<DictionaryEntry>()
                 .Select(e => e.Key.ToString()).FirstOrDefault(e => "$".Equals(e, StringComparison.OrdinalIgnoreCase));
           if (null == exPath)
           {
              Console.WriteLine("Root working folder is not set. It must be set so that files referred to outside of git repo may be retrieved. Will terminate on enter" );
              Console.ReadLine();

              return false;
           }

           return true;
        }

        private void runGitCommand(string cmd, string stdInput, out string[] stdOutput) => runGitCommand(cmd, stdInput, out stdOutput, null);

        private void runGitCommand(string cmd, string stdInput, out string[] stdOutput, IDictionary<string, string> env)
        {
            var pi = new ProcessStartInfo(GitCmd, cmd)
                         {
                             WorkingDirectory = WorkingFolder,
                             UseShellExecute = false,
                             RedirectStandardOutput = true,
                             RedirectStandardInput = true
                         };
            //set env vars
            if (null != env)
                foreach (var e in env)
                    pi.EnvironmentVariables.Add(e.Key, e.Value);
            using (var p = new Process{ StartInfo = pi })
            {
                p.Start();
                p.StandardInput.Write(stdInput);
                p.StandardInput.Close();
                var msgs = new List<string>();
                while (!p.StandardOutput.EndOfStream)
                    msgs.Add(p.StandardOutput.ReadLine());
                stdOutput = msgs.ToArray();
                p.WaitForExit();
            }
        }

        private void vaultLogin()
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
        private void vaultLogout() => ServerOperations.Logout();
    }
}
