using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.IO;
using CommandLine;
using Vault2Git.Lib;

namespace Vault2Git.CLI
{
    static class Program
    {
        class Params
        {
            [Option("limit", Default = 999999999, HelpText = "Max number of versions to take from Vault for each branch. Default all versions")]
            public int Limit { get; set; }
            [Option("console-output", Default = false, HelpText = "Use console output (default=no output)")]
            public bool UseConsole { get; set; }
            [Option("caps-lock", Default = false, HelpText = "")]
            public bool UseCapsLock { get; set; }
            [Option("skip-empty-commits", Default = false, HelpText = "Do not create empty commits in Git")]
            public bool SkipEmptyCommits { get; set; }
            [Option("ignore-labels", Default = false, HelpText = "Do not create Git tags from Vault labels")]
            public bool IgnoreLabels { get; set; }
            [Option("verbose", Default = false, HelpText = "Output detailed messages")]
            public bool Verbose { get; set; }
            [Option("ForceFullFolderGet", Default = false, HelpText = "Every change set gets entire folder structure. Required for shared file updates to be picked up. Otherwise such changes will only be picked up when the entire folder is retrieved due to a subsequent changeset which necessitates a whole folder retrieval.")]
            public bool ForceFullFolderGet { get; set; }
            [Option("pause", Default = false, HelpText = "Pause just before commit so local state may be checked")]
            public bool Pause { get; set; }
            [Option("paths", HelpText = "paths to override setting in .config", Separator = ';')]
            public IEnumerable<string> Paths { get; set; }
            [Option("work", HelpText = "WorkingFolder to override setting in .config. --work=. is most common")]
            public string Work { get; set; }
            [Option("branches", Default = new []{"master"}, HelpText = "Git branches to process. May not be a superset of branches defined in config", Separator = ';')]
            public IEnumerable<string> Branches { get; set; }
            [Option("directories", HelpText = "Subdirectories to process within Sourcegear Vault repo", Separator = ';')]
            public IEnumerable<string> Directories { get; set; }

            public override string ToString() => $"{nameof(Limit)}: {Limit}, {nameof(UseConsole)}: {UseConsole}, {nameof(UseCapsLock)}: {UseCapsLock}, {nameof(SkipEmptyCommits)}: {SkipEmptyCommits}, {nameof(IgnoreLabels)}: {IgnoreLabels}, {nameof(Verbose)}: {Verbose}, {nameof(ForceFullFolderGet)}: {ForceFullFolderGet}, {nameof(Pause)}: {Pause}, {nameof(Paths)}: {string.Join(",", Paths)}, {nameof(Work)}: {Work}, {nameof(Branches)}: {string.Join(",", Branches)}, {nameof(Directories)}: {string.Join(",", Directories)}";
        }
        
        private static bool _useCapsLock;
        private static bool _useConsole;

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
            Configuration configuration;

            Console.WriteLine("Vault2Git -- converting history from Vault repositories to Git");
            Console.InputEncoding = System.Text.Encoding.UTF8;

            // First look for Config file in the current directory - allows for repository-based config files
            string configPath = Path.Combine(Environment.CurrentDirectory, "Vault2Git.exe.config");
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

            Console.WriteLine($"Using config file {configPath}");

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

            Console.WriteLine("   use Vault2Git --help to get additional info");

            _useConsole = param.UseConsole;
            _useCapsLock = param.UseCapsLock;
            var workingFolder = param.Work ?? appSettings.Settings["Convertor.WorkingFolder"].Value;

            // check working folder ends with trailing slash
            if (workingFolder.Last() != '\\')
            {
                workingFolder += '\\';
            }

            if (param.Verbose) 
            {
               Console.WriteLine($"GitCmd = {appSettings.Settings["Convertor.GitCmd"].Value}");
               Console.WriteLine($"GitDomainName = {appSettings.Settings["Git.DomainName"].Value}");
               Console.WriteLine($"VaultServer = {appSettings.Settings["Vault.Server"].Value}");
               Console.WriteLine($"VaultRepository = {appSettings.Settings["Vault.Repo"].Value}");
               Console.WriteLine($"VaultUser = {appSettings.Settings["Vault.User"].Value}" );
               Console.WriteLine(param.ToString());
            }

            var processor = new Processor
            {
                WorkingFolder = workingFolder,
                GitCmd = appSettings.Settings["Convertor.GitCmd"].Value,
                GitDomainName = appSettings.Settings["Git.DomainName"].Value,
                VaultServer = appSettings.Settings["Vault.Server"].Value,
                VaultRepository = appSettings.Settings["Vault.Repo"].Value,
                VaultUser = appSettings.Settings["Vault.User"].Value,
                VaultPassword = appSettings.Settings["Vault.Password"].Value,
                Progress = ShowProgress,
                SkipEmptyCommits = param.SkipEmptyCommits,
                Verbose = param.Verbose,
                Pause = param.Pause,
                ForceFullFolderGet= param.ForceFullFolderGet,
                VaultSubdirectories = param.Directories.ToList()
            };

            var git2VaultRepoPathsSubset = new Dictionary<string, string>();
            foreach (var branch in param.Branches)
            {
                git2VaultRepoPathsSubset[branch] = git2VaultRepoPaths[branch];
            }
            processor.Pull(git2VaultRepoPathsSubset, param.Limit);

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

        static bool ShowProgress(long version, TimeSpan timeSpan)
        {
            if (_useConsole)
            {
                switch (version)
                {
                    case Processor.ProgressSpecialVersionInit:
                        Console.WriteLine($"init took {timeSpan}");
                        break;
                    case Processor.ProgressSpecialVersionGc:
                        Console.WriteLine($"gc took {timeSpan}");
                        break;
                    case Processor.ProgressSpecialVersionFinalize:
                        Console.WriteLine($"finalization took {timeSpan}");
                        break;
                    case Processor.ProgressSpecialVersionTags:
                        Console.WriteLine($"tags creation took {timeSpan}");
                        break;
                    default:
                        Console.WriteLine($"processing version {version} took {timeSpan}");
                        break;
                }
            }

            return _useCapsLock && Console.CapsLock; //cancel flag
        }

        static string RemoveTrailingSlash(string str) => str.EndsWith("/") ? str.Remove(str.Length - 1) : str;
    }
}
