using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.IO;
using System.Threading;
using CommandLine;
using Serilog;
using Vault2Git.Lib;

namespace Vault2Git.CLI
{
    static class Program
    {
        class Params
        {
            [Option("limit", Default = null, HelpText = "Max number of versions to take from Vault for each branch. Default all versions")]
            public long? Limit { get; set; }
            [Option("skip-empty-commits", Default = false, HelpText = "Do not create empty commits in Git")]
            public bool SkipEmptyCommits { get; set; }
            [Option("ignore-labels", Default = false, HelpText = "Do not create Git tags from Vault labels")]
            public bool IgnoreLabels { get; set; }
            [Option("verbose", Default = false, HelpText = "Output detailed messages")]
            public bool Verbose { get; set; }
            [Option("ForceFullFolderGet", Default = false, HelpText = "Every change set gets entire folder structure. Required for shared file updates to be picked up. Otherwise such changes will only be picked up when the entire folder is retrieved due to a subsequent changeset which necessitates a whole folder retrieval.")]
            public bool ForceFullFolderGet { get; set; }
            [Option("paths", HelpText = "paths to override setting in .config", Separator = ';')]
            public IEnumerable<string> Paths { get; set; }
            [Option("work", HelpText = "WorkingFolder to override setting in .config. --work=. is most common")]
            public string Work { get; set; }
            [Option("run-continuously", Default = false, HelpText = "Keep running and attempt pulling in new commits until ctrl-c is pressed")]
            public bool RunContinuously { get; set; }
            [Option("branches", Default = new []{"master"}, HelpText = "Git branches to process. May not be a superset of branches defined in config", Separator = ';')]
            public IEnumerable<string> Branches { get; set; }
            [Option("directories", HelpText = "Subdirectories to process within Sourcegear Vault repo", Separator = ';')]
            public IEnumerable<string> Directories { get; set; }
            [Option("git-push-origin", Default = false, HelpText = "Git push origin")]
            public bool DoGitPushOrigin { get; set; }
            [Option("begin-date", Default = "1990-1-1", HelpText = "Date to start merge from")]
            public DateTime? BeginDate { get; set; }

            public override string ToString() => $"{nameof(Limit)}: {Limit}, {nameof(SkipEmptyCommits)}: {SkipEmptyCommits}, {nameof(IgnoreLabels)}: {IgnoreLabels}, {nameof(Verbose)}: {Verbose}, {nameof(ForceFullFolderGet)}: {ForceFullFolderGet}, {nameof(Paths)}: {string.Join(",", Paths)}, {nameof(Work)}: {Work}, {nameof(RunContinuously)}: {RunContinuously}, {nameof(Branches)}: {string.Join(",", Branches)}, {nameof(Directories)}: {string.Join(",", Directories)}, {nameof(DoGitPushOrigin)}: {DoGitPushOrigin}, {nameof(BeginDate)}: {BeginDate}";
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        //[STAThread]
        static int Main(string[] args)
        {
            Params param = null;
            Parser.Default.ParseArguments<Params>(args)
                .WithParsed(opts => param = opts)
                .WithNotParsed(PrintErrorAndExit);
            
            if (param.Verbose)
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Console()
                    .CreateLogger();
            }
            else
            {
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Console()
                    .CreateLogger();
            }

            Log.Information("Vault2Git -- converting history from Vault repositories to Git");
            Console.InputEncoding = System.Text.Encoding.UTF8;
            
            Configuration configuration;

            // First look for Config file in the current directory - allows for repository-based config files
            var configPath = Path.Combine(Environment.CurrentDirectory, "Vault2Git.exe.config");
            if (File.Exists(configPath))
            {
                var configFileMap = new ExeConfigurationFileMap {ExeConfigFilename = configPath};
                configuration = ConfigurationManager.OpenMappedExeConfiguration(configFileMap, ConfigurationUserLevel.None);
            }
            else
            {
               // Get normal exe file config. 
               // This is what happens by default when using ConfigurationManager.AppSettings["setting"] 
               // to access config properties
               var applicationName = Environment.GetCommandLineArgs()[0];
            #if !DEBUG
               applicationName += ".exe";
            #endif

               configPath = Path.Combine(Environment.CurrentDirectory, applicationName);
               configuration = ConfigurationManager.OpenExeConfiguration(configPath);
            }

            // Get access to the AppSettings properties in the chosen config file
            var appSettings = (AppSettingsSection)configuration.GetSection("appSettings");

            Log.Information($"Using config file {configPath}");

            if (!param.Directories.Any())
            {
                param.Directories = appSettings.Settings["Convertor.Directories"].Value.Split(';').ToList();
            }
            if (!param.Paths.Any())
            {
                param.Paths = appSettings.Settings["Convertor.Paths"].Value.Split(';').ToList();
            }
            
            var git2VaultRepoPaths = param.Paths.ToDictionary(pair => RemoveTrailingSlash(pair.Split('~')[1]),
                pair => RemoveTrailingSlash(pair.Split('~')[0]));
            if (!git2VaultRepoPaths.Keys.All(p => param.Branches.Contains(p)))
            {
                Console.Error.WriteLine($"Config git branches ({string.Join(",", git2VaultRepoPaths.Keys)}) are not a superset of branches ({string.Join(",", param.Branches)})");
                return -2;
            }
            param.Branches = git2VaultRepoPaths.Keys;

            Log.Information("   use Vault2Git --help to get additional info");
            var workingFolder = param.Work ?? appSettings.Settings["Convertor.WorkingFolder"].Value;

            // check working folder ends with trailing slash
            if (workingFolder.Last() != '\\')
            {
                workingFolder += '\\';
            }

            Log.Information($"GitCmd = {appSettings.Settings["Convertor.GitCmd"].Value}");
            Log.Information($"GitDomainName = {appSettings.Settings["Git.DomainName"].Value}");
            Log.Information($"VaultServer = {appSettings.Settings["Vault.Server"].Value}");
            Log.Information($"VaultRepository = {appSettings.Settings["Vault.Repo"].Value}");
            Log.Information($"VaultUser = {appSettings.Settings["Vault.User"].Value}" );
            Log.Information(param.ToString());

            var git = new GitProvider(workingFolder, 
                appSettings.Settings["Convertor.GitCmd"].Value, 
                appSettings.Settings["Git.DomainName"].Value,
                param.SkipEmptyCommits);
            
            var vault = new VaultProvider(appSettings.Settings["Vault.Server"].Value,
                appSettings.Settings["Vault.Repo"].Value, 
                appSettings.Settings["Vault.User"].Value, 
                appSettings.Settings["Vault.Password"].Value);
            
            var processor = new Processor(git, vault, param.BeginDate)
            {
                WorkingFolder = workingFolder,
                ForceFullFolderGet= param.ForceFullFolderGet,
                VaultSubdirectories = param.Directories.ToList()
            };

            var git2VaultRepoPathsSubset = new Dictionary<string, string>();
            foreach (var branch in param.Branches)
            {
                git2VaultRepoPathsSubset[branch] = git2VaultRepoPaths[branch];
            }

            if (param.RunContinuously)
            {
                var consecutiveErrorCount = 0;
                var cancelKeyPressed = false;
                Console.CancelKeyPress += delegate { cancelKeyPressed = true; Log.Information("Stop process requested"); };
                var nextRun = DateTime.UtcNow;
                do
                {
                    if (nextRun <= DateTime.UtcNow)
                    {
                        try
                        {
                            processor.Pull(git2VaultRepoPathsSubset, param.Limit, param.DoGitPushOrigin);
                            consecutiveErrorCount = 0;
                        }
                        catch (Exception e)
                        {
                            const int maxErrorCount = 3;
                            Log.Warning($"Exception caught while pulling in new versions from vault. Current consecutive exception count: {consecutiveErrorCount}, max: {maxErrorCount}.\n{e}");
                            if (++consecutiveErrorCount >= maxErrorCount)
                            {
                                Log.Fatal($"Exiting after {consecutiveErrorCount} consecutive errors");
                                return -1 * consecutiveErrorCount;
                            }
                        }
                        nextRun = DateTime.UtcNow.AddMinutes(1);
                        Log.Information($"Next run scheduled for {nextRun}");
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                } while (!cancelKeyPressed);
            }
            else
            {
                processor.Pull(git2VaultRepoPathsSubset, param.Limit, param.DoGitPushOrigin);
            }

            if (!param.IgnoreLabels)
                processor.CreateTagsFromLabels();

            return 0;
        }

        static void PrintErrorAndExit(IEnumerable<Error> errs)
        {
            foreach (var error in errs)
            {
                Console.Error.WriteLine(error);
            }
            Environment.Exit(-1);
        }

        static string RemoveTrailingSlash(string str) => str.EndsWith("/") ? str.Remove(str.Length - 1) : str;
    }
}
