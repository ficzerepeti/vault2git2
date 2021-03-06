using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

namespace Vault2Git.Lib
{
    public interface IGitProvider
    {
        long? GitVaultVersion(string gitBranch, string vaultTagStem, string vaultTag);
        void GitCurrentBranch(out string originalGitBranch);
        string GitCommit(string author, string commitMsg, DateTime dateTime);
        void GitFinalize();
        void GitAddTag(string tagName, string gitCommitId, string comment);
        void GitCheckout(string branch);
        void GitPushOrigin(string branch);
    }

    public class GitProvider : IGitProvider
    {
        /// <summary>
        /// path where conversion will take place. If it not already set as value working folder, it will be set automatically
        /// </summary>
        private readonly string _workingFolder;
        private readonly string _pathToGitExe;
        private readonly string _gitDomainName;
        private readonly bool _skipEmptyCommits;
        private readonly bool _ignoreGitIgnore;
        private const int GitGcInterval = 200;

        private long _commitCount;

        public GitProvider(string workingFolder, string pathToGitExe, string gitDomainName, bool skipEmptyCommits, bool ignoreGitIgnore)
        {
            _workingFolder = workingFolder;
            _pathToGitExe = pathToGitExe;
            _gitDomainName = gitDomainName;
            _skipEmptyCommits = skipEmptyCommits;
            _ignoreGitIgnore = ignoreGitIgnore;
        }

        public long? GitVaultVersion(string gitBranch, string vaultTagStem, string vaultTag)
        {
            try
            {
                //get commit message
                GitLog(gitBranch, vaultTagStem, out var msgs);

                //get vault version from commit message
                var currentVersion = GetVaultTrxIdFromGitLogMessage(msgs);
                if (currentVersion != 0)
                {
                    if (!msgs.Any(x => x.Contains(vaultTag)))
                    {
                        Log.Warning($"Latest version {currentVersion} was not created for {vaultTag} but instead was: {msgs.Last()}");
                    }

                    return currentVersion;
                }

                Log.Warning("Conversion will start from Version 0");
                return null;
            }
            catch (InvalidOperationException)
            {
                Log.Warning("Searched all commits and failed to find a restart point. Conversion will start from Version 0.");
                return null;
            }
        }

        public string GitCommit(string author, string vaultCommitMessage, DateTime commitTimeStamp)
        {
            GitCurrentBranch(out var gitCurrentBranch);

            var forceStr = _ignoreGitIgnore ? "--force" : "";
            RunGitCommand($"add {forceStr} --all", string.Empty, out var msgs);
            if (_skipEmptyCommits)
            {
                //checking status
                RunGitCommand("status --porcelain", string.Empty, out msgs);
                if (!msgs.Any())
                    return null;
            }

            RunGitCommand($@"commit --allow-empty --all --date=""{commitTimeStamp:s}"" --author=""{author} <{author}@{_gitDomainName}>"" -F -", vaultCommitMessage, out msgs);
            
            //call gc
            if (0 == ++_commitCount % GitGcInterval)
            {
                var gcWatch = Stopwatch.StartNew();
                RunGitCommand("gc --auto", string.Empty, out _);
                Log.Information($"gc took {gcWatch.Elapsed}");
            }

            // Mapping Vault Transaction ID to Git Commit SHA-1 Hash
            if (msgs[0].StartsWith("[" + gitCurrentBranch))
            {
                var gitCommitId = msgs[0].Split(' ')[1];
                gitCommitId = gitCommitId.Substring(0, gitCommitId.Length - 1);
                return gitCommitId;
            }

            return null;
        }

        public void GitCurrentBranch(out string currentBranch)
        {
            RunGitCommand("branch", string.Empty, out var msgs);
            currentBranch = msgs.FirstOrDefault(s => s.StartsWith("*"))?.Substring(1).Trim() ?? "master";
        }

        private void GitLog(string gitBranch, string vaultTag, out List<string> msg) => RunGitCommand($"log {gitBranch} --grep \"{Regex.Escape(vaultTag)}\" -n 1", string.Empty, out msg);
        public void GitAddTag(string tagName, string gitCommitId, string comment) => RunGitCommand($@"tag {tagName} {gitCommitId} -a -m ""{comment}""", string.Empty, out _);
        public void GitCheckout(string branch) => RunGitCommand($"checkout --quiet --force {branch}", string.Empty, out _);
        public void GitPushOrigin(string branch) => RunGitCommand($"push --set-upstream origin {branch}", string.Empty, out _);
        public void GitFinalize() => RunGitCommand("update-server-info", string.Empty, out _);

        private void RunGitCommand(string cmd, string stdInput, out List<string> stdOutput)
        {
            var pi = new ProcessStartInfo(_pathToGitExe, cmd)
            {
                WorkingDirectory = _workingFolder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };

            using var p = new Process {StartInfo = pi};
            p.Start();
            p.StandardInput.Write(stdInput);
            p.StandardInput.Close();
            var msgs = new List<string>();
            while (!p.StandardOutput.EndOfStream)
                msgs.Add(p.StandardOutput.ReadLine());
            stdOutput = msgs.ToList();
            p.WaitForExit();
        }

        private static long GetVaultTrxIdFromGitLogMessage(List<string> msg)
        {
            var lastLine = msg.LastOrDefault();
            if (lastLine == null || !lastLine.Trim().StartsWith(Processor.VaultTag))
            {
                return 0;
            }

            var versionTrxTag = lastLine.Split('@').LastOrDefault();
            if (versionTrxTag == null)
                return 0;

            //get version
            long.TryParse(versionTrxTag, out var version);
            return version;
        }
    }
}