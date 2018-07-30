// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.IntegrationTesting
{
    public class TestApplication: IDisposable
    {
        public string ApplicationPath { get; }

        public static readonly string DotnetCommandName = "dotnet";

        private readonly Dictionary<DotnetPublishParameters, string> _publishCache = new Dictionary<DotnetPublishParameters, string>();

        public TestApplication(string applicationPath)
        {
            ApplicationPath = applicationPath;
        }

        public string Publish(DeploymentParameters deploymentParameters, ILogger logger)
        {
            if (ApplicationPath != deploymentParameters.ApplicationPath)
            {
                throw new InvalidOperationException("ApplicationPath mismatch");
            }

            if (deploymentParameters.PublishEnvironmentVariables.Any())
            {
                throw new InvalidOperationException("DeploymentParameters.PublishEnvironmentVariables not supported");
            }

            if (!string.IsNullOrEmpty(deploymentParameters.PublishedApplicationRootPath))
            {
                throw new InvalidOperationException("DeploymentParameters.PublishedApplicationRootPath not supported");
            }

            if (deploymentParameters.RestoreOnPublish)
            {
                throw new InvalidOperationException("DeploymentParameters.RestoreOnPublish not supported");
            }

            var dotnetPublishParameters = new DotnetPublishParameters
            {
                TargetFramework = deploymentParameters.TargetFramework,
                Configuration = deploymentParameters.Configuration,
                ApplicationType = deploymentParameters.ApplicationType,
                RuntimeArchitecture = deploymentParameters.RuntimeArchitecture
            };

            if (!_publishCache.TryGetValue(dotnetPublishParameters, out var path))
            {
                path = CreateTempDirectory().FullName;

                PublishApplication(deploymentParameters, logger, path);

                _publishCache.Add(dotnetPublishParameters, path);
            }

            return CopyPublishedOutput(path, logger);
        }

        internal static void PublishApplication(DeploymentParameters deploymentParameters, ILogger logger, string path)
        { 
            using (logger.BeginScope("dotnet-publish"))
            {
                if (string.IsNullOrEmpty(deploymentParameters.TargetFramework))
                {
                    throw new Exception($"A target framework must be specified in the deployment parameters for applications that require publishing before deployment");
                }

                deploymentParameters.PublishedApplicationRootPath = path;

                var parameters = $"publish "
                    + $" --output \"{deploymentParameters.PublishedApplicationRootPath}\""
                    + $" --framework {deploymentParameters.TargetFramework}"
                    + $" --configuration {deploymentParameters.Configuration}"
                    + (deploymentParameters.RestoreOnPublish 
                        ? string.Empty
                        : " --no-restore -p:VerifyMatchingImplicitPackageVersion=false");
                        // Set VerifyMatchingImplicitPackageVersion to disable errors when Microsoft.NETCore.App's version is overridden externally
                        // This verification doesn't matter if we are skipping restore during tests.

                if (deploymentParameters.ApplicationType == ApplicationType.Standalone)
                {
                    parameters += $" --runtime {GetRuntimeIdentifier(deploymentParameters)}";
                }

                parameters += $" {deploymentParameters.AdditionalPublishParameters}";

                var startInfo = new ProcessStartInfo
                {
                    FileName = DotnetCommandName,
                    Arguments = parameters,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = deploymentParameters.ApplicationPath,
                };

                ProcessHelpers.AddEnvironmentVariablesToProcess(startInfo, deploymentParameters.PublishEnvironmentVariables, logger);

                var hostProcess = new Process() { StartInfo = startInfo };

                logger.LogInformation($"Executing command {DotnetCommandName} {parameters}");

                hostProcess.StartAndCaptureOutAndErrToLogger("dotnet-publish", logger);

                // A timeout is passed to Process.WaitForExit() for two reasons:
                // 
                // 1. When process output is read asynchronously, WaitForExit() without a timeout blocks until child processes
                //    are killed, which can cause hangs due to MSBuild NodeReuse child processes started by dotnet.exe.
                //    With a timeout, WaitForExit() returns when the parent process is killed and ignores child processes.
                //    https://stackoverflow.com/a/37983587/102052
                // 
                // 2. If "dotnet publish" does hang indefinitely for some reason, tests should fail fast with an error message.
                const int timeoutMinutes = 5;
                if (hostProcess.WaitForExit(milliseconds: timeoutMinutes * 60 * 1000))
                {
                    if (hostProcess.ExitCode != 0)
                    {
                        var message = $"{DotnetCommandName} publish exited with exit code : {hostProcess.ExitCode}";
                        logger.LogError(message);
                        throw new Exception(message);
                    }
                }
                else
                {
                    var message = $"{DotnetCommandName} publish failed to exit after {timeoutMinutes} minutes";
                    logger.LogError(message);
                    throw new Exception(message);
                }

                logger.LogInformation($"{DotnetCommandName} publish finished with exit code : {hostProcess.ExitCode}");
            }
        }

        private string CopyPublishedOutput(string path, ILogger logger)
        {
            var target = CreateTempDirectory();

            var source = new DirectoryInfo(path);
            CopyFiles(source, target, logger);
            return target.FullName;
        }

        private static DirectoryInfo CreateTempDirectory()
        {
            var tempPath = Path.GetTempPath() + Guid.NewGuid().ToString("N");
            var target = new DirectoryInfo(tempPath);
            target.Create();
            return target;
        }

        public static void CopyFiles(DirectoryInfo source, DirectoryInfo target, ILogger logger)
        {
            foreach (DirectoryInfo directoryInfo in source.GetDirectories())
            {
                CopyFiles(directoryInfo, target.CreateSubdirectory(directoryInfo.Name), logger);
            }

            logger.LogDebug($"Processing {target.FullName}");
            foreach (FileInfo fileInfo in source.GetFiles())
            {
                logger.LogDebug($"  Copying {fileInfo.Name}");
                var destFileName = Path.Combine(target.FullName, fileInfo.Name);
                fileInfo.CopyTo(destFileName);
            }   
        }

        private static string GetRuntimeIdentifier(DeploymentParameters deploymentParameters)
        {
            var architecture = deploymentParameters.RuntimeArchitecture;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "win7-" + architecture;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux-" + architecture;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "osx-" + architecture;
            }
            else
            {
                throw new InvalidOperationException("Unrecognized operation system platform");
            }
        }

        public void Dispose()
        {
            foreach (var publishedApp in _publishCache)
            {
                RetryHelper.RetryOperation(() => Directory.Delete(publishedApp.Value, true), _ => { });
            }
        }
        
        private struct DotnetPublishParameters
        {
            public string TargetFramework { get; set; }
            public string Configuration { get; set; }
            public ApplicationType ApplicationType { get; set; }
            public RuntimeArchitecture RuntimeArchitecture { get; set; }
        }
    }
}