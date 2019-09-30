﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VaultClientIntegrationLib;
using VaultClientOperationsLib;
using VaultLib;
using System.Xml;
using System.Threading;

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
           string [] fileEntries = Directory.GetFiles(targetDirectory);
           foreach (string fileName in fileEntries)
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

        public int GitGCInterval = 200;

        //callback
        public Func<long, int, bool> Progress;

        //flags
        public bool SkipEmptyCommits = false;
        public bool Verbose = false;
        public bool Pause = false;
        public bool ForceFullFolderGet = false;

        //private vars
        /// <summary>
        /// Maps Vault TransactionID to Git Commit SHA-1 Hash
        /// </summary>
        private readonly IDictionary<long, String> _txidMappings = new Dictionary<long, String>();

        //constants
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
        /// <param name="git2vaultRepoPath">Key=git, Value=vault</param>
        /// <param name="limitCount"></param>
        /// <returns></returns>
        public bool Pull(IEnumerable<KeyValuePair<string, string>> git2vaultRepoPath, long limitCount)
        {
            int ticks = 0;

            //get git current branch name
            ticks += gitCurrentBranch(out OriginalGitBranch);
            Console.WriteLine($"Starting git branch is {OriginalGitBranch}");
            
            //reorder target branches to start from current branch, so don't need to do checkout for first branch
            var targetList =
                git2vaultRepoPath.OrderByDescending(p => p.Key.Equals(OriginalGitBranch, StringComparison.CurrentCultureIgnoreCase));

            ticks += vaultLogin();

            if (!IsSetRootVaultWorkingFolder())
            {
               Environment.Exit(1);
            }

            try
            {
                foreach (var pair in targetList)
                {
                    var gitBranch = pair.Key;
                    var vaultRepoPath = pair.Value;

                    Console.WriteLine("\nProcessing git branch {0}", gitBranch);

                    long currentGitVaultVersion = 0;

                    //reset ticks
                    ticks = 0;

                    //get current version
                    ticks += gitVaultVersion(gitBranch, vaultRepoPath, ref currentGitVaultVersion);

                    //get vaultVersions
                    IDictionary<long, VaultVersionInfo> vaultVersions = new SortedList<long, VaultVersionInfo>();
                    ticks += vaultPopulateInfo(vaultRepoPath, vaultVersions);
                    var versionsToProcess = vaultVersions.Where(p => p.Key > currentGitVaultVersion).ToList();

                    //do init only if there is something to work on
                    if (versionsToProcess.Any())
                        ticks += Init(vaultRepoPath, gitBranch);

                    //report init
                    if (null != Progress)
                        if (Progress(ProgressSpecialVersionInit, ticks))
                            return true;

                    var counter = 0;
                    foreach (var version in versionsToProcess)
                    {
                        ticks = Environment.TickCount;

                        // Obtain just the ChangeSet for this Version of the repo
                        try
                        {
                           if (ForceFullFolderGet)
                           {
                              throw new FileNotFoundException("Forcing full folder get");
                           }

                           // Get a list of the changed files
                           var txnInfo = ServerOperations.ProcessCommandTxDetail(version.Value.TrxId);
                           foreach (VaultTxDetailHistoryItem txdetailitem in txnInfo.items)
                           {
                              // Do deletions, renames and moves ourselves
                              if (txdetailitem.RequestType == VaultRequestType.Delete)
                              {
                                 // Convert the Vault path to a file system path
                                 var ItemPath1 = String.Copy( txdetailitem.ItemPath1 );

                                 // Ensure the file is within the folder we are working with. 
                                 if (ItemPath1.StartsWith(vaultRepoPath, true, System.Globalization.CultureInfo.CurrentCulture))
                                 {
                                    ItemPath1 = ItemPath1.Replace(vaultRepoPath, WorkingFolder, StringComparison.CurrentCultureIgnoreCase);
                                    ItemPath1 = ItemPath1.Replace('/', '\\');

                                    if (File.Exists(ItemPath1))
                                    {
                                       File.Delete(ItemPath1);
                                    }

                                    if (Directory.Exists(ItemPath1))
                                    {
                                       Directory.Delete(ItemPath1, true);
                                    }
                                 }
                                 continue;
                              }
                              else if (txdetailitem.RequestType == VaultRequestType.Move ||
                                       txdetailitem.RequestType == VaultRequestType.Rename)
                              {
                                 ProcessFileItem(vaultRepoPath, WorkingFolder, txdetailitem, true);
                                 continue;
                              }
                              else if (txdetailitem.RequestType == VaultRequestType.Share)
                              {
                                 ProcessFileItem(vaultRepoPath, WorkingFolder, txdetailitem, false);
                                 continue;
                              }
                              else if (txdetailitem.RequestType == VaultRequestType.AddFolder)
                              {
                                 // Git doesn't add empty folders
                                 continue;
                              }
                              else if (txdetailitem.RequestType == VaultRequestType.CopyBranch)
                              {
                                 // Nothing in a CopyBranch to do. Its just a place marker
                                 continue;
                              }

                              // Shared file changes may be checked in as a file that's in a different vault tree,
                              // so throw an exception to cause whole tree to be refreshed.

                              if (txdetailitem.ItemPath1.StartsWith(vaultRepoPath, true, System.Globalization.CultureInfo.CurrentCulture))
                              {
                                 // Apply the changes from vault of the correct version for this file 
                                 vaultGetFile(vaultRepoPath, txdetailitem);
                              }
                              else
                              {
                                 if (Verbose) Console.WriteLine("{0} is outside current branch; getting whole folder", txdetailitem.ItemPath1);

                                 throw new FileNotFoundException(
                                    "Source file is outside the current branch: "
                                    + txdetailitem.ItemPath1);
                              }

                              if (File.Exists(vaultRepoPath))
                              {
                                 //
                                 // Remove Source Code Control
                                 //

                                 //change all sln files
                                 if (txdetailitem.ItemPath1.EndsWith("sln", true, System.Globalization.CultureInfo.CurrentCulture))
                                 {
                                    removeSCCFromSln(txdetailitem.ItemPath1);
                                 }

                                 //change all csproj files
                                 if (txdetailitem.ItemPath1.EndsWith("csproj", true, System.Globalization.CultureInfo.CurrentCulture))
                                 {
                                    removeSCCFromCSProj(txdetailitem.ItemPath1);
                                 }

                                 //change all vdproj files
                                 if (txdetailitem.ItemPath1.EndsWith("vdproj", true, System.Globalization.CultureInfo.CurrentCulture))
                                 {
                                    removeSCCFromVDProj(txdetailitem.ItemPath1);
                                 }
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
                              vaultGetFolder(vaultRepoPath, version.Key, version.Value.TrxId);

                              //change all sln files
                              Directory.GetFiles(
                                  WorkingFolder,
                                  "*.sln",
                                  SearchOption.AllDirectories)
                                 //remove temp files created by vault
                                  .Where(f => !f.Contains("~"))
                                  .ToList()
                                  .ForEach(f => ticks += removeSCCFromSln(f));
                              //change all csproj files
                              Directory.GetFiles(
                                  WorkingFolder,
                                  "*.csproj",
                                  SearchOption.AllDirectories)
                                 //remove temp files created by vault
                                  .Where(f => !f.Contains("~"))
                                  .ToList()
                                  .ForEach(f => ticks += removeSCCFromCSProj(f));
                              //change all vdproj files
                              Directory.GetFiles(
                                  WorkingFolder,
                                  "*.vdproj",
                                  SearchOption.AllDirectories)
                                 //remove temp files created by vault
                                  .Where(f => !f.Contains("~"))
                                  .ToList()
                                  .ForEach(f => ticks += removeSCCFromVDProj(f));
                           }
                           catch (Exception)
                           {
                              throw new Exception("Cannot get transaction details for " + version.Value.TrxId);
                           }
                        }

                        ticks = Environment.TickCount - ticks;

                        //get vault version info
                        var info = vaultVersions[version.Key];

                        if (Pause)
                        {
                           Console.WriteLine("Pause before commit. Enter to continue.");
                           Console.ReadLine();
                        }


                        //commit
                        ticks += gitCommit(info.Login, info.TrxId, GitDomainName,
                                           buildCommitMessage(vaultRepoPath, version.Key, info), info.TimeStamp);
                        if (null != Progress)
                            if (Progress(version.Key, ticks))
                                return true;
                        counter++;
                        //call gc
                        if (0 == counter%GitGCInterval)
                        {
                            ticks = gitGC();
                            if (null != Progress)
                                if (Progress(ProgressSpecialVersionGc, ticks))
                                    return true;
                        }
                        //check if limit is reached
                        if (counter >= limitCount)
                            break;
                    }

                    if (versionsToProcess.Any())
                    {
                       ticks = vaultFinalize(vaultRepoPath);
                    }
                }
            }
            finally
            {
               Console.WriteLine("\n");

               //complete
                ticks += vaultLogout();

                //finalize git (update server info for dumb clients)
                ticks += gitFinalize();
                Progress?.Invoke(ProgressSpecialVersionFinalize, ticks);
            }
            return false;
        }


        /// <summary>
        /// removes Source control refs from sln files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        private static int removeSCCFromSln(string filePath)
        {
            var ticks = Environment.TickCount;
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
            return Environment.TickCount - ticks;
        }

        /// <summary>
        /// removes Source control refs from csProj files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        public static int removeSCCFromCSProj(string filePath)
        {
            var ticks = Environment.TickCount;
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
            return Environment.TickCount - ticks;
        }

        /// <summary>
        /// removes Source control refs from vdProj files
        /// </summary>
        /// <param name="filePath">path to sln file</param>
        /// <returns></returns>
        private static int removeSCCFromVDProj(string filePath)
        {
            var ticks = Environment.TickCount;
            var lines = File.ReadAllLines(filePath).ToList();
            File.WriteAllLines(filePath, lines.Where(l => !l.Trim().StartsWith(@"""Scc")).ToArray(), Encoding.UTF8);
            return Environment.TickCount - ticks;
        }

        private int vaultPopulateInfo(string repoPath, IDictionary<long, VaultVersionInfo> info)
        {
            var ticks = Environment.TickCount;

            foreach (var i in ServerOperations.ProcessCommandVersionHistory(repoPath,
                                                                            1,
                                                                            VaultDateTime.Parse("2000-01-01"),
                                                                            VaultDateTime.Parse("2020-01-01"),
                                                                            0))
                info.Add(i.Version, new VaultVersionInfo()
                                        {
                                            TrxId = i.TxID,
                                            Comment = i.Comment,
                                            Login = i.UserLogin,
                                            TimeStamp = i.TxDate.GetDateTime()
                                        });
            return Environment.TickCount - ticks;
        }

        /// <summary>
        /// Creates Git tags from Vault labels
        /// </summary>
        /// <param name="repositoryFolderPath"></param>
        /// <returns></returns>
        public bool CreateTagsFromLabels(string repositoryFolderPath = "$")
        {
            Console.WriteLine( "Creating tags from labels...");

            int ticks = Environment.TickCount;

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

            ticks = Environment.TickCount - ticks;

            try
            {
               if (labelItems != null)
               {
                  foreach (VaultLabelItemX currItem in labelItems)
                  {
                     if (!_txidMappings.ContainsKey(currItem.TxID))
                        continue;

                     string gitCommitId = _txidMappings.First(s => s.Key.Equals(currItem.TxID)).Value;

                     if (!string.IsNullOrEmpty(gitCommitId))
                     {
                        string gitLabelName = Regex.Replace(currItem.Label, "[\\W]", "_");
                        ticks += gitAddTag(currItem.TxID + "_" + gitLabelName, gitCommitId, currItem.Comment);
                     }
                  }
               }

               //add ticks for git tags
               Progress?.Invoke(ProgressSpecialVersionTags, ticks);
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

        private void vaultGetFolder(string repoPath, long version, long txId)
        {
           try
           {
              //apply version to the repo folder
              vaultProcessCommandGetVersion(repoPath, version, true);
           }
           catch (Exception e)
           {
              Console.WriteLine("Exception " + e.Message + " getting Version " + version + " from Vault repo. Waiting 5 secs and retrying...");

              // if an error occurs, wait and then retry the operation. We may be running too fast for Vault
              Thread.Sleep(TimeSpan.FromSeconds(5.0));

              vaultProcessCommandGetVersion(repoPath, version, true);
           }

           //now process deletions, moves, and renames (due to vault bug)
           var allowedRequests = new[]
                                      {
                                          VaultRequestType.Delete,
                                          VaultRequestType.Move, 
                                          VaultRequestType.Rename
                                      };
           foreach (var item in ServerOperations.ProcessCommandTxDetail(txId).items
               .Where(i => allowedRequests.Contains(i.RequestType)))
           {
              //delete file
              //check if it is within current branch
              if (item.ItemPath1.StartsWith(repoPath, StringComparison.CurrentCultureIgnoreCase))
              {
                 var pathToDelete = Path.Combine(WorkingFolder, item.ItemPath1.Substring(repoPath.Length + 1));
                 if (Verbose) Console.WriteLine("delete {0} => {1}", item.ItemPath1, pathToDelete);
                 if (File.Exists(pathToDelete))
                    File.Delete(pathToDelete);
                 if (Directory.Exists(pathToDelete))
                 {
                    Directory.Delete(pathToDelete, true);
                    // Ensure its really deleted so iteration of directories by caller does not cause dir not exist exception
                    Thread.Sleep(500);
                 }
              }
           }
        }

        private void vaultGetFile(string repoPath, VaultTxDetailHistoryItem txdetailitem)
        {
           // Allow exception to percolate up. Presume its due to a file missing from the latest Version 
           // thats in this Version. That is, this file is later deleted, moved or renamed.

           //apply version to the repo folder
           if (Verbose) Console.WriteLine("get {0} version {1}", txdetailitem.ItemPath1, txdetailitem.Version);
           vaultProcessCommandGetVersion(txdetailitem.ItemPath1, txdetailitem.Version, false);
           if (Verbose) Console.WriteLine("get {0} version {1} SUCCESS!", txdetailitem.ItemPath1, txdetailitem.Version);

           //now process deletions, moves, and renames (due to vault bug)
           var allowedRequests = new[]
                                      {
                                          VaultRequestType.Delete,
                                          VaultRequestType.Move, 
                                          VaultRequestType.Rename
                                      };
           if (allowedRequests.Contains(txdetailitem.RequestType))
           {
              //delete file
              //check if it is within current branch
              if (txdetailitem.ItemPath1.StartsWith(repoPath, StringComparison.CurrentCultureIgnoreCase))
              {
                 var pathToDelete = Path.Combine(WorkingFolder, txdetailitem.ItemPath1.Substring(repoPath.Length + 1));

                 if (Verbose) Console.WriteLine("delete {0} => {1}", txdetailitem.ItemPath1, pathToDelete);

                 if (File.Exists(pathToDelete))
                    File.Delete(pathToDelete);
                 if (Directory.Exists(pathToDelete))
                 {
                    Directory.Delete(pathToDelete, true);
                    // Ensure its really deleted so iteration of directories by caller does not cause dir not exist exception
                    Thread.Sleep(500);
                 }
              }
           }
        }

        private void vaultProcessCommandGetVersion(string repoPath, long version, bool recursive)
        {
           // Must delete everything first otherwise deleted files are not deleted.
           if (recursive)
           {
              if ( Verbose) Console.WriteLine("Getting entire vault path " + repoPath );
              try
              {
                 Statics.DeleteWorkingDirectory(WorkingFolder);
              }
              catch (IOException)
              {
                 // Directory not empty? Presume its a handle still opened by Explorer or a permissions issue. Just continue. Vault get will fail if there is a real issue.
              }
              Thread.Sleep(500); // Allow file system to apply directory changes
           }

           //apply version to the repo folder

           GetOperations.ProcessCommandGetVersion(
               repoPath,
               Convert.ToInt32(version),
               new GetOptions()
               {
                  MakeWritable = MakeWritableType.MakeAllFilesWritable,
                  Merge = MergeType.OverwriteWorkingCopy,
                  OverrideEOL = VaultEOL.None,
                  //remove working copy does not work -- bug http://support.sourcegear.com/viewtopic.php?f=5&t=11145
                  PerformDeletions = PerformDeletionsType.RemoveWorkingCopy,
                  SetFileTime = SetFileTimeType.Modification,
                  Recursive = recursive
               });
        }

        public void ProcessFileItem( String vaultRepoPath, String workingFolder, VaultTxDetailHistoryItem txdetailitem, bool moveFiles )
        {
            // Convert the Vault path to a file system path
            String ItemPath1 = String.Copy(txdetailitem.ItemPath1);
            String ItemPath2 = String.Copy(txdetailitem.ItemPath2);

            // Ensure the files are withing the folder we are working with. 
            // If the source path is outside the current branch, throw an exception and let vault handle the processing because
            // we do not have the correct state of files outside the current branch.
            // If the target path is outside, ignore a file copy and delete a file move.
            // E.g. A Share can be shared outside of the branch we are working with
            if (Verbose) Console.WriteLine("Processing {0} to {1}. MoveFiles = {2})", ItemPath1, ItemPath2, moveFiles);
            bool ItemPath1WithinCurrentBranch = ItemPath1.StartsWith(vaultRepoPath, true, System.Globalization.CultureInfo.CurrentCulture);
            bool ItemPath2WithinCurrentBranch = ItemPath2.StartsWith(vaultRepoPath, true, System.Globalization.CultureInfo.CurrentCulture);

            if (!ItemPath1WithinCurrentBranch)
            {
               if (Verbose) Console.WriteLine("   Source file is outside of working folder. Error");
               throw new FileNotFoundException(
                  "Source file is outside the current branch: "
                  + ItemPath1);
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

        struct VaultVersionInfo
        {
            public long TrxId;
            public string Comment;
            public string Login;
            public DateTime TimeStamp;
        }

        private int gitVaultVersion(string gitBranch, string vaultRepoPath, ref long currentVersion)
        {
            var ticks = 0;
            currentVersion = 0;
            try
            {
               //get commit message
               ticks += gitLog(gitBranch, vaultRepoPath, out var msgs);
               //get vault version from commit message
               currentVersion = getVaultVersionFromGitLogMessage(msgs);

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

            return ticks; 
        }

        private int Init(string vaultRepoPath, string gitBranch)
        {
            //set working folder
            var ticks = setVaultWorkingFolder(vaultRepoPath);
            //checkout branch
            for (int tries = 0; ; tries++)
            {
                ticks += runGitCommand($"checkout --quiet --force {gitBranch}", string.Empty, out var msgs);
                //confirm current branch (sometimes checkout failed)
                ticks += gitCurrentBranch(out var currentBranch);
                if (gitBranch.Equals(currentBranch, StringComparison.OrdinalIgnoreCase))
                    break;
                if (tries > 5)
                    throw new Exception("cannot switch branches");
            }
            return ticks;
        }

        private int vaultFinalize(string vaultRepoPath)
        {
            int ticks = 0;

            //unset working folder
            ticks =  unSetVaultWorkingFolder(vaultRepoPath);

            // Return to original Git branch
            ticks += runGitCommand($"checkout --quiet --force {OriginalGitBranch}", string.Empty, out var msgs);

            return ticks;
        }

        // vaultLogin is the user name as known in Vault e.g. 'robert' which needs to be mapped to rob.goodridge
        private int gitCommit(string vaultLogin, long vaultTrxid, string gitDomainName, string vaultCommitMessage, DateTime commitTimeStamp)
        {
            this.gitCurrentBranch(out var gitCurrentBranch);

            var ticks = runGitCommand("add --force --all .", string.Empty, out var msgs);
            if (SkipEmptyCommits)
            {
                //checking status
                ticks += runGitCommand("status --porcelain", string.Empty, out msgs);
                if (!msgs.Any())
                    return ticks;
            }
            ticks += runGitCommand($@"commit --allow-empty --all --date=""{commitTimeStamp:s}"" --author=""{vaultLogin} <{vaultLogin}@{gitDomainName}>"" -F -", vaultCommitMessage, out msgs);

            // Mapping Vault Transaction ID to Git Commit SHA-1 Hash
            if (msgs[0].StartsWith("[" + gitCurrentBranch))
            {
                string gitCommitId = msgs[0].Split(' ')[1];
                gitCommitId = gitCommitId.Substring(0, gitCommitId.Length - 1);
                _txidMappings.Add(vaultTrxid, gitCommitId);
            }
            return ticks;
        }

        private int gitCurrentBranch(out string currentBranch)
        {
            var ticks = runGitCommand("branch", string.Empty, out var msgs);
            currentBranch = msgs.FirstOrDefault(s => s.StartsWith("*"))?.Substring(1).Trim();
            currentBranch = currentBranch ?? "master";
            return ticks;
        }

        private string buildCommitMessage(string repoPath, long version, VaultVersionInfo info)
        {
            //parse path repo$RepoPath@version/trx
            var r = new StringBuilder(info.Comment);
            r.AppendLine();
            r.AppendLine($"{BuildVaultTag(repoPath)}@{version}/{info.TrxId}");
            return r.ToString();
        }

        private string BuildVaultTag(string repoPath) => $"{VaultTag} {VaultRepository}{repoPath}";

        private long getVaultVersionFromGitLogMessage(IEnumerable<string> msg)
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
            long.TryParse(versionTrxTag.Split('/').First(), out var version);
            return version;
        }

        private int gitLog(string gitBranch, string vaultRepoPath, out string[] msg) => runGitCommand($"log {gitBranch} --grep \"{Regex.Escape(BuildVaultTag(vaultRepoPath))}\" -n 1", string.Empty, out msg);
        private int gitAddTag(string gitTagName, string gitCommitId, string gitTagComment) => runGitCommand($@"tag {gitTagName} {gitCommitId} -a -m ""{gitTagComment}""", string.Empty, out _);
        private int gitReset() => runGitCommand("reset --hard", string.Empty, out _);
        private int gitClean() => runGitCommand("clean -f -x", string.Empty, out _);
        private int gitGC() => runGitCommand("gc --auto", string.Empty, out _);
        private int gitFinalize() => runGitCommand("update-server-info", string.Empty, out _);

        private int setVaultWorkingFolder(string repoPath)
        {
            var ticks = Environment.TickCount;

            // Save the current working folder
            SortedList list = ServerOperations.GetWorkingFolderAssignments();
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
            return Environment.TickCount - ticks;
        }

        private int unSetVaultWorkingFolder(string repoPath)
        {
            var ticks = Environment.TickCount;

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
            return Environment.TickCount - ticks;
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

        private int runGitCommand(string cmd, string stdInput, out string[] stdOutput)
        {
            return runGitCommand(cmd, stdInput, out stdOutput, null);
        }
        private int runGitCommand(string cmd, string stdInput, out string[] stdOutput, IDictionary<string, string> env)
        {
            var ticks = Environment.TickCount;

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
            using (var p = new Process()
                               {
                                   StartInfo = pi
                               })
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
            return Environment.TickCount - ticks;
        }

        private int vaultLogin()
        {
            var ticks = Environment.TickCount;
            ServerOperations.client.LoginOptions.URL = $"http://{VaultServer}/VaultService";
            ServerOperations.client.LoginOptions.User = VaultUser;
            ServerOperations.client.LoginOptions.Password = VaultPassword;
            ServerOperations.client.LoginOptions.Repository = VaultRepository;
            ServerOperations.Login();
            ServerOperations.client.MakeBackups = false;
            ServerOperations.client.AutoCommit = false;
            ServerOperations.client.Verbose = true;
            return Environment.TickCount - ticks;
        }
        private int vaultLogout()
        {
            var ticks = Environment.TickCount;
            ServerOperations.Logout();
            return Environment.TickCount - ticks;
        }

    }
}
