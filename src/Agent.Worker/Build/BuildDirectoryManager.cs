using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.IO;
using Microsoft.TeamFoundation.Build.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    [ServiceLocator(Default = typeof(BuildDirectoryManager))]
    public interface IBuildDirectoryManager : IAgentService
    {
        TrackingConfig PrepareDirectory(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            ISourceProvider sourceProvider);

        void CreateDirectory(
            IExecutionContext executionContext,
            string description, string path,
            bool deleteExisting);
    }

    public sealed class BuildDirectoryManager : AgentService, IBuildDirectoryManager
    {
        public TrackingConfig PrepareDirectory(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            ISourceProvider sourceProvider)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(executionContext.Variables, nameof(executionContext.Variables));
            ArgUtil.NotNull(endpoint, nameof(endpoint));
            ArgUtil.NotNull(sourceProvider, nameof(sourceProvider));
            var trackingManager = HostContext.GetService<ITrackingManager>();

            // Defer to the source provider to calculate the hash key.
            Trace.Verbose("Calculating build directory hash key.");
            string hashKey = sourceProvider.GetBuildDirectoryHashKey(executionContext, endpoint);
            Trace.Verbose($"Hash key: {hashKey}");

            // Load the existing tracking file if one already exists.
            string trackingFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                Constants.Build.Path.TrackingConfigFile);
            Trace.Verbose($"Loading tracking config if exists: {trackingFile}");
            TrackingConfigBase existingConfig = trackingManager.LoadIfExists(executionContext, trackingFile);

            // Check if the build needs to be garbage collected. If the hash key
            // has changed, then the existing build directory cannot be reused.
            TrackingConfigBase garbageConfig = null;
            if (existingConfig != null
                && !string.Equals(existingConfig.HashKey, hashKey, StringComparison.OrdinalIgnoreCase))
            {
                // Just store a reference to the config for now. It can safely be
                // marked for garbage collection only after the new build directory
                // config has been created.
                Trace.Verbose($"Hash key from existing tracking config does not match. Existing key: {existingConfig.HashKey}");
                garbageConfig = existingConfig;
                existingConfig = null;
            }

            // Create a new tracking config if required.
            TrackingConfig newConfig;
            if (existingConfig == null)
            {
                Trace.Verbose("Creating a new tracking config file.");
                newConfig = trackingManager.Create(executionContext, endpoint, hashKey, trackingFile);
                ArgUtil.NotNull(newConfig, nameof(newConfig));
            }
            else
            {
                // Convert legacy format to the new format if required.
                newConfig = ConvertToNewFormat(executionContext, endpoint, existingConfig);

                // For existing tracking config files, update the job run properties.
                Trace.Verbose("Updating job run properties.");
                trackingManager.UpdateJobRunProperties(executionContext, newConfig, trackingFile);
            }

            // Mark the old configuration for garbage collection.
            if (garbageConfig != null)
            {
                Trace.Verbose("Marking existing config for garbage collection.");
                trackingManager.MarkForGarbageCollection(executionContext, garbageConfig);
            }

            // Prepare the build directory.
            // There are 2 ways to provide build directory clean policy.
            //     1> set definition variable build.clean or agent.clean.buildDirectory. (on-prem user need to use this, since there is no Web UI in TFS 2016)
            //     2> select source clean option in definition repository tab. (VSTS will have this option in definition designer UI)
            // Delete entire build directory if clean=all is set.
            // Always recreate artifactstaging dir and testresult dir.
            // Recreate binaries dir if clean=binary is set.
            // Recreate source dir if clean=src is set.
            BuildCleanOption cleanOption = GetBuildDirectoryCleanOption(executionContext, endpoint);

            CreateDirectory(
                executionContext,
                description: "build directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), newConfig.BuildDirectory),
                deleteExisting: cleanOption == BuildCleanOption.All);
            CreateDirectory(
                executionContext,
                description: "artifacts directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), newConfig.ArtifactsDirectory),
                deleteExisting: true);
            CreateDirectory(
                executionContext,
                description: "test results directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), newConfig.TestResultsDirectory),
                deleteExisting: true);
            CreateDirectory(
                executionContext,
                description: "binaries directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), newConfig.BuildDirectory, Constants.Build.Path.BinariesDirectory),
                deleteExisting: cleanOption == BuildCleanOption.Binary);
            CreateDirectory(
                executionContext,
                description: "source directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), newConfig.BuildDirectory, Constants.Build.Path.SourcesDirectory),
                deleteExisting: cleanOption == BuildCleanOption.Source);

            return newConfig;
        }

        private TrackingConfig ConvertToNewFormat(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            TrackingConfigBase config)
        {
            Trace.Entering();

            // If it's already in the new format, return it.
            TrackingConfig newConfig = config as TrackingConfig;
            if (newConfig != null)
            {
                return newConfig;
            }

            // Delete the legacy artifact/staging directories.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            DeleteDirectory(
                executionContext,
                description: "legacy artifacts directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyArtifactsDirectory));
            DeleteDirectory(
                executionContext,
                description: "legacy staging directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyStagingDirectory));

            // Determine the source directory name. Check if the directory is named "s" already.
            // Convert the source directory to be named "s" if there is a problem with the old name.
            string sourcesDirectoryNameOnly = Constants.Build.Path.SourcesDirectory;
            if (!Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, sourcesDirectoryNameOnly))
                && !String.Equals(endpoint.Name, Constants.Build.Path.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(endpoint.Name, Constants.Build.Path.LegacyArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(endpoint.Name, Constants.Build.Path.LegacyStagingDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(endpoint.Name, Constants.Build.Path.TestResultsDirectory, StringComparison.OrdinalIgnoreCase)
                && !endpoint.Name.Contains("\\")
                && !endpoint.Name.Contains("/")
                && Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, endpoint.Name)))
            {
                sourcesDirectoryNameOnly = endpoint.Name;
            }

            // Convert to the new format.
            newConfig = new TrackingConfig(
                executionContext,
                legacyConfig,
                sourcesDirectoryNameOnly,
                // The legacy artifacts directory has been deleted at this point - see above - so
                // switch the configuration to using the new naming scheme.
                useNewArtifactsDirectoryName: true);
            return newConfig;
        }

        public void CreateDirectory(IExecutionContext executionContext, string description, string path, bool deleteExisting)
        {
            // Delete.
            if (deleteExisting)
            {
                executionContext.Debug($"Delete existing {description}: '{path}'");
                DeleteDirectory(executionContext, description, path);
            }

            // Create.
            if (!Directory.Exists(path))
            {
                executionContext.Debug($"Creating {description}: '{path}'");
                Trace.Info($"Creating {description}.");
                Directory.CreateDirectory(path);
            }
        }

        private void DeleteDirectory(IExecutionContext executionContext, string description, string path)
        {
            Trace.Info($"Checking if {description} exists: '{path}'");
            if (Directory.Exists(path))
            {
                executionContext.Debug($"Deleting {description}: '{path}'");
                IOUtil.DeleteDirectory(path, executionContext.CancellationToken);
            }
        }

        private BuildCleanOption GetBuildDirectoryCleanOption(IExecutionContext executionContext, ServiceEndpoint endpoint)
        {
            BuildCleanOption? cleanOption = executionContext.Variables.Build_Clean;
            if (cleanOption != null)
            {
                return cleanOption.Value;
            }

            bool clean = false;
            if (endpoint.Data.ContainsKey(WellKnownEndpointData.Clean))
            {
                clean = StringUtil.ConvertToBoolean(endpoint.Data[WellKnownEndpointData.Clean]);
            }

            if (clean)
            {
                if (endpoint.Data.ContainsKey("cleanOptions"))
                {
                    RepositoryCleanOptions? cleanOptionFromEndpoint = EnumUtil.TryParse<RepositoryCleanOptions>(endpoint.Data["cleanOptions"]);
                    if (cleanOptionFromEndpoint != null)
                    {
                        if (cleanOptionFromEndpoint == RepositoryCleanOptions.All)
                        {
                            return BuildCleanOption.All;
                        }
                        else if (cleanOptionFromEndpoint == RepositoryCleanOptions.SourceAndOutput)
                        {
                            return BuildCleanOption.Binary;
                        }
                    }
                }
            }

            return BuildCleanOption.None;
        }
    }

    [Flags]
    public enum RepositoryCleanOptions
    {
        Source,
        SourceAndOutput,
        All,
    }
}