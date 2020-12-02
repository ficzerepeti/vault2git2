using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using CommandLine;

namespace Vault2Git.CLI
{
    public class Params
    {
        [Option("limit", Default = null, HelpText = "Max number of versions to take from Vault for each branch. Default all versions")]
        public long? Limit { get; set; }

        [Option("sample-time-when-no-full-path", Default = null, HelpText = "Sample time when no full time available in transaction detail. This seem to happen on old SourceGear versions and SourceSafe. If specified then merge will jump ahead this much and commit changes in a batch to avoid getting root or subdirectories recursively")]
        public TimeSpan? SampleTimeWhenNoFullPathAvailable { get; set; }

        [Option("skip-empty-commits", Default = false, HelpText = "Do not create empty commits in Git")]
        public bool SkipEmptyCommits { get; set; }

        [Option("ignore-git-ignore", Default = false, HelpText = "Ignore .gitignore files and add --force to gid add command")]
        public bool IgnoreGitIgnore { get; set; }

        [Option("ignore-labels", Default = false, HelpText = "Do not create Git tags from Vault labels")]
        public bool IgnoreLabels { get; set; }

        [Option("verbose", Default = false, HelpText = "Output detailed messages")]
        public bool Verbose { get; set; }

        [Option("force-full-folder-get", Default = false, HelpText = "Every change set gets entire folder structure. Required for shared file updates to be picked up. Otherwise such changes will only be picked up when the entire folder is retrieved due to a subsequent changeset which necessitates a whole folder retrieval.")]
        public bool ForceFullFolderGet { get; set; }

        [Option("run-continuously", Default = false, HelpText = "Keep running and attempt pulling in new commits until ctrl-c is pressed")]
        public bool RunContinuously { get; set; }

        [Option("git-push-origin", Default = false, HelpText = "Git push origin")]
        public bool DoGitPushOrigin { get; set; }

        [Option("begin-date", HelpText = "Date to start merge from")]
        public DateTime? BeginDate { get; set; }

        // Config settable from both config file and command line. Command line is used if present.

        [Option("branches", Default = new []{"master"}, HelpText = "Git branches to process. May not be a superset of branches defined in config", Separator = ';')]
        public IEnumerable<string> Branches { get; set; }

        [Option("directories", HelpText = "Subdirectories to process within Sourcegear Vault repo", Separator = ';')]
        public IEnumerable<string> Directories { get; set; }

        [Option("vault-server", HelpText = "Sourcegear Vault server address")]
        public string VaultServer { get; set; }

        [Option("vault-user", HelpText = "Sourcegear Vault user")]
        public string VaultUser { get; set; }

        [Option("vault-password", HelpText = "Sourcegear Vault password")]
        public string VaultPassword { get; set; }

        [Option("vault-repo", HelpText = "Sourcegear Vault repository")]
        public string VaultRepo { get; set; }

        [Option("git-domain-name", HelpText = "Git domain name")]
        public string GitDomainName { get; set; }

        [Option("working-folder", HelpText = "WorkingFolder to override setting in .config. --work=. is most common")]
        public string WorkingFolder { get; set; }

        [Option("git-cmd", HelpText = "Git executable path")]
        public string GitCmd { get; set; }

        [Option("branch-mappings", HelpText = "Branch mappings", Separator = ';')]
        public IEnumerable<string> Paths { get; set; }

        public override string ToString() => $"{nameof(Limit)}: {Limit}, {nameof(SkipEmptyCommits)}: {SkipEmptyCommits}, {nameof(IgnoreLabels)}: {IgnoreLabels}, {nameof(Verbose)}: {Verbose}, {nameof(ForceFullFolderGet)}: {ForceFullFolderGet}, {nameof(RunContinuously)}: {RunContinuously}, {nameof(DoGitPushOrigin)}: {DoGitPushOrigin}, {nameof(BeginDate)}: {BeginDate}, {nameof(Branches)}: {string.Join(",", Branches)}, {nameof(Directories)}: {string.Join(",", Directories)}, {nameof(VaultServer)}: {VaultServer}, {nameof(VaultUser)}: {VaultUser}, {nameof(VaultPassword)}: {new string('*', VaultPassword.Length)}, {nameof(VaultRepo)}: {VaultRepo}, {nameof(GitDomainName)}: {GitDomainName}, {nameof(WorkingFolder)}: {WorkingFolder}, {nameof(GitCmd)}: {GitCmd}, {nameof(Paths)}: {string.Join(",", Paths)}";

        public void ApplyAppConfigIfParamNotPresent(AppSettingsSection appSettings)
        {
            var appCfgDirectories = appSettings.Settings["Convertor.Directories"];
            if (!Directories.Any() && appCfgDirectories != null)
            {
                Directories = appCfgDirectories.Value.Split(';').ToList();
            }
            
            VaultServer ??= appSettings.Settings["Vault.Server"].Value;
            VaultUser ??= appSettings.Settings["Vault.User"].Value;
            VaultPassword ??= appSettings.Settings["Vault.Password"].Value;
            VaultRepo ??= appSettings.Settings["Vault.Repo"].Value;
            GitDomainName ??= appSettings.Settings["Git.DomainName"].Value;
            WorkingFolder ??= appSettings.Settings["Convertor.WorkingFolder"].Value;
            GitCmd ??= appSettings.Settings["Convertor.GitCmd"].Value;
            
            if (!Paths.Any())
            {
                Paths = appSettings.Settings["Convertor.Paths"].Value.Split(';').ToList();
            }
        }
    }
}