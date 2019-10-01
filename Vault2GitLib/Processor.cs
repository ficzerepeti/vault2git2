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
        /// <param name="git2VaultRepoPath">Key=git, Value=vault</param>
        /// <param name="limitCount"></param>
        /// <returns></returns>
        public bool Pull(IEnumerable<KeyValuePair<string, string>> git2VaultRepoPath, long limitCount)
        {
            //get git current branch name
            var ticks = gitCurrentBranch(out OriginalGitBranch);
            Console.WriteLine($"Starting git branch is {OriginalGitBranch}");
            
            //reorder target branches to start from current branch, so don't need to do checkout for first branch
            var targetList = git2VaultRepoPath.OrderByDescending(p => p.Key.Equals(OriginalGitBranch, StringComparison.CurrentCultureIgnoreCase));

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

                    Console.WriteLine($"\nProcessing git branch {gitBranch}");

                    //reset ticks
                    ticks = 0;

                    var txIds = new SortedSet<long>();
                    foreach (var vaultSubdirectory in VaultSubdirectories)
                    {
                       //get current Git version
                       long currentGitVaultVersion = 0; // TODO: this should become TxID
                       ticks += gitVaultVersion(gitBranch, vaultRepoPath, vaultSubdirectory, ref currentGitVaultVersion);
                       //get vaultVersions
                       ticks += vaultPopulateInfo(vaultRepoPath, vaultSubdirectory, txIds, currentGitVaultVersion);
                    }

                    //do init only if there is something to work on
                    if (!txIds.Any())
                        return false;

                    //report init
                    if (null != Progress)
                        if (Progress(ProgressSpecialVersionInit, ticks))
                            return true;

                    var counter = 0;
                    var movedRenamedPaths = new Dictionary<string, string>();
                    foreach (var txId in txIds)
                    {
                        var affectedSubdirsToVersion = new Dictionary<string, long>();
                        var txnInfo = ServerOperations.ProcessCommandTxDetail(txId);
                        movedRenamedPaths.Clear();
                        // It has been noticed that renames tend to be listed at the end of txnInfo.items. Make sure this event precedes content updates
                        var orderedItems = txnInfo.items.OrderBy(x => x.RequestType != VaultRequestType.Rename && x.RequestType != VaultRequestType.Move).ToList();
                        foreach (var txDetailItem in orderedItems)
                        {
                           if (!TryFindMatchingSubdir(vaultRepoPath, txDetailItem.ItemPath1, out var vaultSubdirectory))
                           {
                              continue;
                           }
                           affectedSubdirsToVersion[vaultSubdirectory] = txDetailItem.ID;
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
                                 var movedTo = Path.Combine(Path.GetDirectoryName(txDetailItem.ItemPath1), txDetailItem.ItemPath2).Replace(@"\", "/");
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
                           vaultGetFile(vaultRepoPath, vaultSubdirectory, txDetailItem, movedRenamedPaths);

                           if (!File.Exists(txDetailItem.ItemPath1)) continue;
                        
                           // Remove Source Code Control
                           //change all sln files
                           if (txDetailItem.ItemPath1.EndsWith("sln", true, CultureInfo.CurrentCulture))
                           {
                              removeSCCFromSln(txDetailItem.ItemPath1);
                           }

                           //change all csproj files
                           if (txDetailItem.ItemPath1.EndsWith("csproj", true, CultureInfo.CurrentCulture))
                           {
                              removeSCCFromCSProj(txDetailItem.ItemPath1);
                           }

                           //change all vdproj files
                           if (txDetailItem.ItemPath1.EndsWith("vdproj", true, CultureInfo.CurrentCulture))
                           {
                              removeSCCFromVDProj(txDetailItem.ItemPath1);
                           }
                        }
                        
                        ticks = Environment.TickCount - ticks;

                        if (Pause)
                        {
                           Console.WriteLine("Pause before commit. Enter to continue.");
                           Console.ReadLine();
                        }

                        //commit
                        foreach (var affectedSubdirAndVersion in affectedSubdirsToVersion)
                        {
                           var subdirVer = affectedSubdirAndVersion.Value;
                           var commitMsg = buildCommitMessage($"{vaultRepoPath}/{affectedSubdirAndVersion.Key}", subdirVer, txId, txnInfo.changesetComment);
                           
                           ticks += gitCommit(txnInfo.userlogin, txId, GitDomainName, commitMsg, new DateTime(txnInfo.items.First().ModDate.Ticks));
                           if (null != Progress)
                              if (Progress(subdirVer, ticks))
                                 return true;
                           counter++;
                           //call gc
                           if (0 == counter%GitGCInterval)
                           {
                              ticks = gitGC();
                              if (null != Progress && Progress(ProgressSpecialVersionGc, ticks)) return true;
                           }
                        }
                        //check if limit is reached
                        if (counter >= limitCount)
                           break;
                    }

                    ticks = vaultFinalize(vaultRepoPath);
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

        private int vaultPopulateInfo(string repoPath, string subdirectory, ISet<long> txIds, long currentGitVaultVersion)
        {
            var ticks = Environment.TickCount;
            var beginDate = new VaultDateTime(1990,1,1);
            var endDate = new VaultDateTime(2090,1,1);
            foreach (var i in ServerOperations.ProcessCommandVersionHistory($"{repoPath}/{subdirectory}", 1, beginDate, endDate,  0))
            {
               if (i.TxID > currentGitVaultVersion)
               {
                  txIds.Add(i.TxID);
               }
            }
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

        private void vaultGetFile(string repoPath, string vaultSubdirectory, VaultTxDetailHistoryItem txdetailitem, Dictionary<string, string> movedRenamedPaths)
        {
           // Allow exception to percolate up. Presume its due to a file missing from the latest Version 
           // thats in this Version. That is, this file is later deleted, moved or renamed.
           if (movedRenamedPaths.TryGetValue(txdetailitem.ItemPath1, out var itemPath))
           {
              if (Verbose) Console.WriteLine($"Handling rename/move from {txdetailitem.ItemPath1} to {itemPath}");
           }
           else
           {
              itemPath = txdetailitem.ItemPath1;
           }

           //apply version to the repo folder
           if (Verbose) Console.WriteLine($"get {itemPath} version {txdetailitem.Version}");
           vaultProcessCommandGetVersion(itemPath, vaultSubdirectory, txdetailitem.Version, false);
           if (Verbose) Console.WriteLine($"get {itemPath} version {txdetailitem.Version} SUCCESS!");

           //now process deletions, moves, and renames (due to vault bug)
           switch (txdetailitem.RequestType)
           {
                 case VaultRequestType.Delete:
                 case VaultRequestType.Move:
                 case VaultRequestType.Rename:
                    break;
                 default:
                    return;
           }
           //delete file
           //check if it is within current branch
           if (!itemPath.StartsWith(repoPath, StringComparison.CurrentCultureIgnoreCase)) return;
           var pathToDelete = Path.Combine(WorkingFolder, itemPath.Substring(repoPath.Length + 1));

           if (Verbose) Console.WriteLine($"delete {itemPath} => {pathToDelete}");

           if (File.Exists(pathToDelete))
              File.Delete(pathToDelete);
           else if (Directory.Exists(pathToDelete))
           {
              Directory.Delete(pathToDelete, true);
              // Ensure its really deleted so iteration of directories by caller does not cause dir not exist exception
              Thread.Sleep(500);
           }
        }

        private void vaultProcessCommandGetVersion(string fullRepoPath, string subdirectory, long version, bool recursive)
        {
           // Must delete everything first otherwise deleted files are not deleted.
           if (recursive)
           {
              if (Verbose) Console.WriteLine($"Getting entire vault path {fullRepoPath}");
              try
              {
                 Statics.DeleteWorkingDirectory(Path.Combine(WorkingFolder, subdirectory));
              }
              catch (IOException)
              {
                 // Directory not empty? Presume its a handle still opened by Explorer or a permissions issue. Just continue. Vault get will fail if there is a real issue.
              }
              Thread.Sleep(500); // Allow file system to apply directory changes
           }

           //apply version to the repo folder
           GetOperations.ProcessCommandGetVersion(fullRepoPath, Convert.ToInt32(version),
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
            if (Verbose) Console.WriteLine($"Processing {ItemPath1} to {ItemPath2}. MoveFiles = {moveFiles})");
            bool ItemPath1WithinCurrentBranch = ItemPath1.StartsWith(vaultRepoPath, true, CultureInfo.CurrentCulture);
            bool ItemPath2WithinCurrentBranch = ItemPath2.StartsWith(vaultRepoPath, true, CultureInfo.CurrentCulture);

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
        
        private bool TryFindMatchingSubdir(string vaultRepo, string itemPath, out string matchingVaultSubdirectory)
        {
           foreach (var vaultSubdirectory in VaultSubdirectories)
           {
              if (itemPath.StartsWith($"{vaultRepo}/{vaultSubdirectory}", true, CultureInfo.CurrentCulture))
              {
                 matchingVaultSubdirectory = vaultSubdirectory;
                 return true;
              }
           }

           matchingVaultSubdirectory = null;
           return false;
        }

        private int gitVaultVersion(string gitBranch, string vaultRepoPath, string vaultSubdirectory, ref long currentVersion)
        {
            var ticks = 0;
            currentVersion = 0;
            try
            {
               //get commit message
               ticks += gitLog(gitBranch, $"{vaultRepoPath}/{vaultSubdirectory}", out var msgs);
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
                ticks += runGitCommand($"checkout --quiet --force {gitBranch}", string.Empty, out _);
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

        private string buildCommitMessage(string repoPath, long version, long trxId, string comment)
        {
            //parse path repo$RepoPath@version/trx
            var r = new StringBuilder(comment);
            r.AppendLine();
            r.AppendLine($"{BuildVaultTag(repoPath)}@{version}/{trxId}");
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
