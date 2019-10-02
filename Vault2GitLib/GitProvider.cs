using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;
using VaultClientOperationsLib;

namespace Vault2Git.Lib
{
    public interface IGitProvider
    {
        void GitVaultVersion(string gitBranch, string vaultTag, ref long currentVersion);
        void GitCurrentBranch(out string originalGitBranch);
        string GitCommit(string author, string commitMsg, DateTime dateTime);
        void GitFinalize();
        void GitAddTag(string tagName, string gitCommitId, string comment);
        void GitCheckout(string branch);
    }

    public class GitProvider : IGitProvider
    {
        /// <summary>
        /// path where conversion will take place. If it not already set as value working folder, it will be set automatically
        /// </summary>
        public string WorkingFolder;
        
        /// <summary>
        /// path to git.exe
        /// </summary>
        public string GitCmd;

        public string GitDomainName;
        public bool SkipEmptyCommits = false;
        private const int GitGcInterval = 200;

        private long _commitCount;

        public void GitVaultVersion(string gitBranch, string vaultTag, ref long currentVersion)
        {
            currentVersion = 0;
            try
            {
                //get commit message
                GitLog(gitBranch, vaultTag, out var msgs);
                //get vault version from commit message
                currentVersion = GetVaultTrxIdFromGitLogMessage(msgs);

                if (currentVersion == 0)
                {
                    Log.Warning("Conversion will start from Version 0");
                }
            }
            catch (InvalidOperationException)
            {
                Log.Warning("Searched all commits and failed to find a restart point. Conversion will start from Version 0.");
            }
        }

        public string GitCommit(string author, string vaultCommitMessage, DateTime commitTimeStamp)
        {
            GitCurrentBranch(out var gitCurrentBranch);

            RunGitCommand("add --force --all .", string.Empty, out var msgs);
            if (SkipEmptyCommits)
            {
                //checking status
                RunGitCommand("status --porcelain", string.Empty, out msgs);
                if (!msgs.Any())
                    return null;
            }

            RunGitCommand($@"commit --allow-empty --all --date=""{commitTimeStamp:s}"" --author=""{author} <{author}@{GitDomainName}>"" -F -", vaultCommitMessage, out msgs);
            
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
            currentBranch = msgs.FirstOrDefault(s => s.StartsWith("*"))?.Substring(1).Trim();
            currentBranch = currentBranch ?? "master";
        }

        private void GitLog(string gitBranch, string vaultTag, out string[] msg) => RunGitCommand($"log {gitBranch} --grep \"{Regex.Escape(vaultTag)}\" -n 1", string.Empty, out msg);
        public void GitAddTag(string tagName, string gitCommitId, string comment) => RunGitCommand($@"tag {tagName} {gitCommitId} -a -m ""{comment}""", string.Empty, out _);
        public void GitCheckout(string branch) => RunGitCommand($"checkout --quiet --force {branch}", string.Empty, out _);

        public void GitFinalize() => RunGitCommand("update-server-info", string.Empty, out _);
        private void RunGitCommand(string cmd, string stdInput, out string[] stdOutput) => RunGitCommand(cmd, stdInput, out stdOutput, null);

        private void RunGitCommand(string cmd, string stdInput, out string[] stdOutput, IDictionary<string, string> env)
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
            using (var p = new Process {StartInfo = pi})
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

        private long GetVaultTrxIdFromGitLogMessage(IEnumerable<string> msg)
        {
            //get last string
            var stringToParse = msg.Last();
            //search for version tag
            var versionString = stringToParse.Split(new[] {Processor.VaultTag}, StringSplitOptions.None).LastOrDefault();
            //parse path reporepoPath@version/trx
            //get version/trx part
            var versionTrxTag = versionString?.Split('@').LastOrDefault();
            if (versionTrxTag == null)
                return 0;

            //get version
            long.TryParse(versionTrxTag, out var version);
            return version;
        }
    }
}