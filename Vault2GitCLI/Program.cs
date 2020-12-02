using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.IO;
using System.Threading;
using CommandLine;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Vault2Git.Lib;

namespace Vault2Git.CLI
{
    public static class Program
    {
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

            Log.Logger = MakeLogger(param.Verbose);

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

            Log.Information($"Using config file {configPath}");

            // Get access to the AppSettings properties in the chosen config file
            param.ApplyAppConfigIfParamNotPresent((AppSettingsSection)configuration.GetSection("appSettings"));

            var git2VaultRepoPaths = param.Paths.ToDictionary(pair => RemoveTrailingSlash(pair.Split('~')[1]),
                pair => RemoveTrailingSlash(pair.Split('~')[0]));
            if (!git2VaultRepoPaths.Keys.All(p => param.Branches.Contains(p)))
            {
                Console.Error.WriteLine($"Config git branches ({string.Join(",", git2VaultRepoPaths.Keys)}) are not a superset of branches ({string.Join(",", param.Branches)})");
                return -2;
            }
            param.Branches = git2VaultRepoPaths.Keys;

            // check working folder ends with trailing slash
            if (param.WorkingFolder.Last() != '\\')
            {
                param.WorkingFolder += '\\';
            }

            Log.Information(param.ToString());

            var git = new GitProvider(param.WorkingFolder, param.GitCmd, param.GitDomainName, param.SkipEmptyCommits, param.IgnoreGitIgnore);
            var vault = new VaultProvider(param.VaultServer, param.VaultRepo, param.VaultUser, param.VaultPassword);

            var processor = new Processor(git, vault, param.Directories.ToList(), param.Limit, param.DoGitPushOrigin, param.SampleTimeWhenNoFullPathAvailable, param.BeginDate)
            {
                WorkingFolder = param.WorkingFolder,
                ForceFullFolderGet= param.ForceFullFolderGet
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
                            processor.Pull(git2VaultRepoPathsSubset);
                            consecutiveErrorCount = 0;
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"Exception caught while pulling in new versions from vault. Current consecutive exception count: {consecutiveErrorCount}.\n{e}");
                        }
                        nextRun = DateTime.UtcNow.AddMinutes(1);
                        Log.Information($"Next run scheduled for {nextRun:u}");
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                } while (!cancelKeyPressed);
            }
            else
            {
                processor.Pull(git2VaultRepoPathsSubset);
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

        private static string RemoveTrailingSlash(string str) => str.EndsWith("/") ? str.Remove(str.Length - 1) : str;

        private static Logger MakeLogger(bool verbose)
        {
            var logger = new LoggerConfiguration().WriteTo.Console(outputTemplate: "[{Timestamp:u} {Level:u5}] {Message:lj}{NewLine}{Exception}", standardErrorFromLevel:LogEventLevel.Error);
            if (verbose)
            {
                logger.MinimumLevel.Verbose();
            }
            return logger.CreateLogger();
        }
    }
}
