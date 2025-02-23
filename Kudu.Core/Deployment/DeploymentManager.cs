﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core.Helpers;
using Kudu.Core.Hooks;
using Kudu.Core.Infrastructure;
using Kudu.Core.Settings;
using Kudu.Core.SourceControl;
using Kudu.Core.Tracing;
using Newtonsoft.Json;

namespace Kudu.Core.Deployment
{
    public class DeploymentManager : IDeploymentManager
    {
        public readonly static string DeploymentScriptFileName = OSDetector.IsOnWindows() ? "deploy.cmd" : "deploy.sh";

        private static readonly Random _random = new Random(Guid.NewGuid().GetHashCode());

        private readonly ISiteBuilderFactory _builderFactory;
        private readonly IEnvironment _environment;
        private readonly ITraceFactory _traceFactory;
        private readonly IAnalytics _analytics;
        private readonly IOperationLock _deploymentLock;
        private readonly ILogger _globalLogger;
        private readonly IDeploymentSettingsManager _settings;
        private readonly IDeploymentStatusManager _status;
        private readonly IWebHooksManager _hooksManager;

        private const string RestartTriggerReason = "App deployment";
        private const string XmlLogFile = "log.xml";
        public const string TextLogFile = "log.log";
        private const string TemporaryDeploymentIdPrefix = "temp-";
        public const int MaxSuccessDeploymentResults = 10;

        public DeploymentManager(ISiteBuilderFactory builderFactory,
                                 IEnvironment environment,
                                 ITraceFactory traceFactory,
                                 IAnalytics analytics,
                                 IDeploymentSettingsManager settings,
                                 IDeploymentStatusManager status,
                                 IOperationLock deploymentLock,
                                 ILogger globalLogger,
                                 IWebHooksManager hooksManager)
        {
            _builderFactory = builderFactory;
            _environment = environment;
            _traceFactory = traceFactory;
            _analytics = analytics;
            _deploymentLock = deploymentLock;
            _globalLogger = globalLogger ?? NullLogger.Instance;
            _settings = settings;
            _status = status;
            _hooksManager = hooksManager;
        }

        private bool IsDeploying
        {
            get
            {
                return _deploymentLock.IsHeld;
            }
        }

        public IEnumerable<DeployResult> GetResults()
        {
            ITracer tracer = _traceFactory.GetTracer();
            using (tracer.Step("DeploymentManager.GetResults"))
            {
                return PurgeAndGetDeployments();
            }
        }

        public DeployResult GetResult(string id)
        {
            return GetResult(id, _status.ActiveDeploymentId, IsDeploying);
        }

        public IEnumerable<LogEntry> GetLogEntries(string id)
        {
            ITracer tracer = _traceFactory.GetTracer();
            using (tracer.Step($"DeploymentManager.GetLogEntries(id:{id})"))
            {
                string path = GetLogPath(id, ensureDirectory: false);

                if (!FileSystemHelpers.FileExists(path))
                {
                    throw new FileNotFoundException(String.Format(CultureInfo.CurrentCulture, Resources.Error_NoLogFound, id));
                }

                VerifyDeployment(id, IsDeploying);

                var logger = GetLoggerForFile(path);
                List<LogEntry> entries = logger.GetLogEntries().ToList();

                // Determine if there's details to show at all
                foreach (var e in entries)
                {
                    e.HasDetails = logger.GetLogEntryDetails(e.Id).Any();
                }

                return entries;
            }
        }

        public IEnumerable<LogEntry> GetLogEntryDetails(string id, string entryId)
        {
            ITracer tracer = _traceFactory.GetTracer();
            using (tracer.Step($"DeploymentManager.GetLogEntryDetails(id:{id}, entryId:{entryId}"))
            {
                string path = GetLogPath(id, ensureDirectory: false);

                if (!FileSystemHelpers.FileExists(path))
                {
                    throw new FileNotFoundException(String.Format(CultureInfo.CurrentCulture, Resources.Error_NoLogFound, id));
                }

                VerifyDeployment(id, IsDeploying);

                var logger = GetLoggerForFile(path);

                return logger.GetLogEntryDetails(entryId).ToList();
            }
        }

        public void Delete(string id)
        {
            ITracer tracer = _traceFactory.GetTracer();
            using (tracer.Step($"DeploymentManager.Delete(id:{id})"))
            {
                string path = GetRoot(id, ensureDirectory: false);

                if (!FileSystemHelpers.DirectoryExists(path))
                {
                    throw new DirectoryNotFoundException(String.Format(CultureInfo.CurrentCulture, Resources.Error_UnableToDeleteNoDeploymentFound, id));
                }

                if (IsActive(id))
                {
                    throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, Resources.Error_UnableToDeleteDeploymentActive, id));
                }

                _status.Delete(id);
            }
        }

        public async Task DeployAsync(
            IRepository repository,
            ChangeSet changeSet,
            string deployer,
            bool clean,
            DeploymentInfoBase deploymentInfo = null,
            bool needFileUpdate = true,
            bool fullBuildByDefault = true)
        {
            using (var deploymentAnalytics = new DeploymentAnalytics(_analytics, _settings))
            {
                Exception exception = null;
                ITracer tracer = _traceFactory.GetTracer();
                IDisposable deployStep = null;
                ILogger innerLogger = null;
                string targetBranch = null;

                // If we don't get a changeset, find out what branch we should be deploying and get the commit ID from it
                if (changeSet == null)
                {
                    targetBranch = _settings.GetBranch();

                    changeSet = repository.GetChangeSet(targetBranch);

                    if (changeSet == null)
                    {
                        throw new InvalidOperationException(String.Format("The current deployment branch is '{0}', but nothing has been pushed to it", targetBranch));
                    }
                }

                string id = changeSet.Id;
                IDeploymentStatusFile statusFile = null;
                try
                {
                    deployStep = tracer.Step($"DeploymentManager.Deploy(id:{id})");
                    // Remove the old log file for this deployment id
                    string logPath = GetLogPath(id);
                    FileSystemHelpers.DeleteFileSafe(logPath);

                    statusFile = GetOrCreateStatusFile(changeSet, tracer, deployer);
                    statusFile.MarkPending();

                    ILogger logger = GetLogger(changeSet.Id, tracer, deploymentInfo);

                    if (needFileUpdate)
                    {
                        using (tracer.Step("Updating to specific changeset"))
                        {
                            innerLogger = logger.Log(Resources.Log_UpdatingBranch, targetBranch ?? id);

                            using (var writer = new ProgressWriter())
                            {
                                // Update to the specific changeset or branch
                                repository.Update(targetBranch ?? id);
                            }
                        }
                    }

                    if (_settings.ShouldUpdateSubmodules())
                    {
                        using (tracer.Step("Updating submodules"))
                        {
                            innerLogger = logger.Log(Resources.Log_UpdatingSubmodules);

                            repository.UpdateSubmodules();
                        }
                    }

                    if (clean)
                    {
                        tracer.Trace("Cleaning {0} repository", repository.RepositoryType);

                        innerLogger = logger.Log(Resources.Log_CleaningRepository, repository.RepositoryType);

                        repository.Clean();
                    }

                    // set to null as Build() below takes over logging
                    innerLogger = null;

                    // Perform the build deployment of this changeset
                    await Build(changeSet, tracer, deployStep, repository, deploymentInfo, deploymentAnalytics, fullBuildByDefault);
                }
                catch (Exception ex)
                {
                    exception = ex;

                    if (innerLogger != null)
                    {
                        innerLogger.Log(ex);
                    }

                    if (statusFile != null)
                    {
                        MarkStatusComplete(statusFile, success: false, deploymentAnalytics: deploymentAnalytics);
                    }

                    tracer.TraceError(ex);

                    deploymentAnalytics.Error = ex.ToString();

                    if (deployStep != null)
                    {
                        deployStep.Dispose();
                    }
                }

                // Remove leftover AppOffline file
                PostDeploymentHelper.RemoveAppOfflineIfLeft(_environment, null, tracer);

                // Reload status file with latest updates
                statusFile = _status.Open(id);
                if (statusFile != null)
                {
                    await _hooksManager.PublishEventAsync(HookEventTypes.PostDeployment, statusFile);
                }

                if (exception != null)
                {
                    throw new DeploymentFailedException(exception);
                }

                if (statusFile != null && statusFile.Status == DeployStatus.Success && _settings.RunFromLocalZip())
                {
                    var zipDeploymentInfo = deploymentInfo as ArtifactDeploymentInfo;
                    if (zipDeploymentInfo != null)
                    {
                        await PostDeploymentHelper.UpdateSiteVersion(zipDeploymentInfo, _environment, tracer);
                    }
                }

                if (statusFile.Status == DeployStatus.Success
                    && ScmHostingConfigurations.FunctionsSyncTriggersDelaySeconds > 0)
                {
                    await PostDeploymentHelper.SyncTriggersIfFunctionsSite(_environment.RequestId, new PostDeploymentTraceListener(tracer),
                        deploymentInfo?.SyncFunctionsTriggersPath, tracePath: _environment.TracePath);
                }
            }
        }

        public async Task<bool> SendDeployStatusUpdate(DeployStatusApiResult updateStatusObj)
        {
            ITracer tracer = _traceFactory.GetTracer();
            int attemptCount = 0;

            try
            {
                await OperationManager.AttemptAsync(async () =>
                {
                    attemptCount++;

                    tracer.Trace($" PostAsync - Trying to send {updateStatusObj.DeploymentStatus} deployment status to {Constants.UpdateDeployStatusPath}. Deployment Id is {updateStatusObj.DeploymentId}");
                    await PostDeploymentHelper.PostAsync(Constants.UpdateDeployStatusPath, _environment.RequestId, content: JsonConvert.SerializeObject(updateStatusObj));

                }, 3, 5 * 1000);

                // If no exception is thrown, the operation was a success
                return true;
            }
            catch (Exception ex)
            {
                tracer.TraceError($"Failed to request a post deployment status. Number of attempts: {attemptCount}. Exception: {ex}");
                // Do not throw the exception
                // We fail silently so that we do not fail the build altogether if this call fails
                //throw;

                return false;
            }
        }

        public async Task RestartMainSiteIfNeeded(ITracer tracer, ILogger logger, DeploymentInfoBase deploymentInfo)
        {
            // If post-deployment restart is disabled, do nothing.
            if (!_settings.RestartAppOnGitDeploy())
            {
                return;
            }

            // Proceed only if 'restart' is allowed for this deployment
            if (deploymentInfo != null && !deploymentInfo.RestartAllowed)
            {
                return;
            }

            if (deploymentInfo != null && !string.IsNullOrEmpty(deploymentInfo.DeploymentTrackingId))
            {
                // Send deployment status update to FE
                // FE will modify the operations table with the PostBuildRestartRequired status
                DeployStatusApiResult updateStatusObj = new DeployStatusApiResult(Constants.PostBuildRestartRequired, deploymentInfo.DeploymentTrackingId);
                bool isSuccess = await SendDeployStatusUpdate(updateStatusObj);

                if (isSuccess)
                {
                    // If operation is a success and PostBuildRestartRequired was posted successfully to the operations DB, then return
                    // Else fallthrough to RestartMainSiteAsync
                    return;
                }
            }

            if (deploymentInfo != null && deploymentInfo.Deployer == Constants.OneDeploy)
            {
                await PostDeploymentHelper.RestartMainSiteAsync(_environment.RequestId, new PostDeploymentTraceListener(tracer, logger));
                return;
            }

            if (_settings.RecylePreviewEnabled())
            {
                logger.Log("Triggering recycle (preview mode enabled).");
                await PostDeploymentHelper.RestartMainSiteAsync(_environment.RequestId, new PostDeploymentTraceListener(tracer, logger));
            }
            else
            {
                logger.Log("Triggering recycle (preview mode disabled).");
                DockerContainerRestartTrigger.RequestContainerRestart(_environment, RestartTriggerReason, tracer);
            }
        }

        public IDisposable CreateTemporaryDeployment(string statusText, out ChangeSet tempChangeSet, ChangeSet changeSet = null, string deployedBy = null)
        {
            var tracer = _traceFactory.GetTracer();
            using (tracer.Step("Creating temporary deployment"))
            {
                changeSet = changeSet != null && changeSet.IsTemporary ? changeSet : CreateTemporaryChangeSet();
                IDeploymentStatusFile statusFile = _status.Create(changeSet.Id);
                statusFile.Id = changeSet.Id;
                statusFile.Message = changeSet.Message;
                statusFile.Author = changeSet.AuthorName;
                statusFile.Deployer = deployedBy;
                statusFile.AuthorEmail = changeSet.AuthorEmail;
                statusFile.Status = DeployStatus.Pending;
                statusFile.StatusText = statusText;
                statusFile.IsTemporary = changeSet.IsTemporary;
                statusFile.IsReadOnly = changeSet.IsReadOnly;
                statusFile.Save();
            }

            tempChangeSet = changeSet;

            // Return a handle that deletes the deployment on dispose.
            return new DisposableAction(() =>
            {
                if (changeSet.IsTemporary)
                {
                    _status.Delete(changeSet.Id);
                }
            });
        }

        public static ChangeSet CreateTemporaryChangeSet(string authorName = null, string authorEmail = null, string message = null)
        {
            string unknown = Resources.Deployment_UnknownValue;
            return new ChangeSet(GenerateTemporaryId(), authorName ?? unknown, authorEmail ?? unknown, message ?? unknown, DateTimeOffset.MinValue)
            {
                IsTemporary = true
            };
        }

        private IEnumerable<DeployResult> PurgeAndGetDeployments(bool throwOnError = true)
        {
            // Order the results by date (newest first). Previously, we supported OData to allow
            // arbitrary queries, but that was way overkill and brought in too many large binaries.
            IEnumerable<DeployResult> results = null;
            try
            {
                results = EnumerateResults().OrderByDescending(t => t.ReceivedTime).ToList();
            }
            catch (Exception)
            {
                if (throwOnError)
                {
                    throw;
                }

                return results;
            }

            try
            {
                results = PurgeDeployments(results);
            }
            catch (Exception ex)
            {
                // tolerate purge error
                _analytics.UnexpectedException(ex);
            }

            return results;
        }

        private void MarkStatusComplete(IDeploymentStatusFile status, bool success, DeploymentAnalytics deploymentAnalytics = null)
        {
            status.ProjectType = deploymentAnalytics?.ProjectType;
            status.VsProjectId = deploymentAnalytics?.VsProjectId;

            if (success)
            {
                status.MarkSuccess();
            }
            else
            {
                status.MarkFailed();
            }

            // Report deployment completion
            DeploymentCompletedInfo.Persist(_environment.RequestId, status);

            // Cleanup old deployments
            PurgeAndGetDeployments(throwOnError: false);
        }

        // since the expensive part (reading all files) is done,
        // we opt for simplicity rather than performance when purging.
        // the input must be in desc order of ReceivedTime (newest first).
        internal IEnumerable<DeployResult> PurgeDeployments(IEnumerable<DeployResult> results)
        {
            if (results.Any())
            {
                var toDelete = new List<DeployResult>();
                toDelete.AddRange(GetPurgeTemporaryDeployments(results));
                toDelete.AddRange(GetPurgeFailedDeployments(results));
                toDelete.AddRange(this.GetPurgeObsoleteDeployments(results));

                if (toDelete.Any())
                {
                    var tracer = _traceFactory.GetTracer();
                    using (tracer.Step("Purge deployment items"))
                    {
                        foreach (DeployResult delete in toDelete)
                        {
                            _status.Delete(delete.Id);

                            tracer.Trace("Remove {0}, {1}, received at {2}",
                                         delete.Id.Substring(0, Math.Min(delete.Id.Length, 9)),
                                         delete.Status,
                                         delete.ReceivedTime);
                        }
                    }

                    results = results.Where(r => !toDelete.Any(i => i.Id == r.Id));
                }
            }

            return results;
        }

        private static IEnumerable<DeployResult> GetPurgeTemporaryDeployments(IEnumerable<DeployResult> results)
        {
            var toDelete = new List<DeployResult>();

            // more than one pending/building, remove all temporary pending
            var pendings = results.Where(r => r.Status != DeployStatus.Failed && r.Status != DeployStatus.Success);
            if (pendings.Count() > 1)
            {
                if (pendings.Any(r => !r.IsTemporary))
                {
                    // if there is non-temporary, remove all pending temporary
                    toDelete.AddRange(pendings.Where(r => r.IsTemporary));
                }
                else
                {
                    if (pendings.First().Id == results.First().Id)
                    {
                        pendings = pendings.Skip(1);
                    }

                    // if first item is not pending temporary, remove all pending temporary
                    toDelete.AddRange(pendings);
                }
            }

            return toDelete;
        }

        private static IEnumerable<DeployResult> GetPurgeFailedDeployments(IEnumerable<DeployResult> results)
        {
            var toDelete = new List<DeployResult>();

            // if one or more fail that never succeeded, only keep latest first one.
            var fails = results.Where(r => r.Status == DeployStatus.Failed && r.LastSuccessEndTime == null);
            if (fails.Any())
            {
                if (fails.First().Id == results.First().Id)
                {
                    fails = fails.Skip(1);
                }

                toDelete.AddRange(fails);
            }

            return toDelete;
        }

        private IEnumerable<DeployResult> GetPurgeObsoleteDeployments(IEnumerable<DeployResult> results)
        {
            var toDelete = new List<DeployResult>();

            // limit number of ever-success items
            // the assumption is user will no longer be interested on these items
            var succeed = results.Where(r => r.LastSuccessEndTime != null);
            if (succeed.Count() > MaxSuccessDeploymentResults)
            {
                // always maintain active and inprogress item
                var activeId = _status.ActiveDeploymentId;
                var purge = succeed.Skip(MaxSuccessDeploymentResults).Where(r =>
                    r.Id != activeId && (r.Status == DeployStatus.Failed || r.Status == DeployStatus.Success));

                toDelete.AddRange(purge);
            }

            return toDelete;
        }

        private static string GenerateTemporaryId(int length = 8)
        {
            const string HexChars = "0123456789abcdfe";

            var strb = new StringBuilder();
            strb.Append(TemporaryDeploymentIdPrefix);
            for (int i = 0; i < length; ++i)
            {
                strb.Append(HexChars[_random.Next(HexChars.Length)]);
            }

            return strb.ToString();
        }

        internal IDeploymentStatusFile GetOrCreateStatusFile(ChangeSet changeSet, ITracer tracer, string deployer)
        {
            string id = changeSet.Id;

            using (tracer.Step("Collecting changeset information"))
            {
                // Check if the status file already exists. This would happen when we're doing a redeploy
                IDeploymentStatusFile statusFile = _status.Open(id);
                if (statusFile == null)
                {
                    // Create the status file and store information about the commit
                    statusFile = _status.Create(id);
                }
                statusFile.Message = changeSet.Message;
                statusFile.Author = changeSet.AuthorName;
                statusFile.Deployer = deployer;
                statusFile.AuthorEmail = changeSet.AuthorEmail;
                statusFile.IsReadOnly = changeSet.IsReadOnly;
                statusFile.Save();

                return statusFile;
            }
        }

        private DeployResult GetResult(string id, string activeDeploymentId, bool isDeploying)
        {
            var file = VerifyDeployment(id, isDeploying);

            if (file == null)
            {
                return null;
            }

            return new DeployResult
            {
                Id = file.Id,
                Author = file.Author,
                Deployer = file.Deployer,
                AuthorEmail = file.AuthorEmail,
                Message = file.Message,
                Progress = file.Progress,
                StartTime = file.StartTime,
                EndTime = file.EndTime,
                Status = file.Status,
                StatusText = file.StatusText,
                Complete = file.Complete,
                IsTemporary = file.IsTemporary,
                IsReadOnly = file.IsReadOnly,
                Current = file.Id == activeDeploymentId,
                ReceivedTime = file.ReceivedTime,
                LastSuccessEndTime = file.LastSuccessEndTime,
                SiteName = file.SiteName
            };
        }

        /// <summary>
        /// Builds and deploys a particular changeset. Puts all build artifacts in a deployments/{id}
        /// </summary>
        private async Task Build(
            ChangeSet changeSet,
            ITracer tracer,
            IDisposable deployStep,
            IRepository repository,
            DeploymentInfoBase deploymentInfo,
            DeploymentAnalytics deploymentAnalytics,
            bool fullBuildByDefault)
        {
            if (changeSet == null || String.IsNullOrEmpty(changeSet.Id))
            {
                throw new ArgumentException("The changeSet.Id parameter is null or empty", "changeSet.Id");
            }

            ILogger logger = null;
            IDeploymentStatusFile currentStatus = null;
            string buildTempPath = null;
            string id = changeSet.Id;

            try
            {
                logger = GetLogger(id, tracer, deploymentInfo);
                ILogger innerLogger = logger.Log(Resources.Log_PreparingDeployment, TrimId(id));

                currentStatus = _status.Open(id);
                currentStatus.Complete = false;
                currentStatus.StartTime = DateTime.UtcNow;
                currentStatus.Status = DeployStatus.Building;
                currentStatus.StatusText = String.Format(CultureInfo.CurrentCulture, Resources.Status_BuildingAndDeploying, id);
                currentStatus.Save();

                ISiteBuilder builder = null;

                // Add in per-deploy default settings values based on the details of this deployment
                var perDeploymentDefaults = new Dictionary<string, string> { { SettingsKeys.DoBuildDuringDeployment, fullBuildByDefault.ToString() } };
                var settingsProviders = _settings.SettingsProviders.Concat(
                    new[] { new BasicSettingsProvider(perDeploymentDefaults, SettingsProvidersPriority.PerDeploymentDefault) });

                var perDeploymentSettings = DeploymentSettingsManager.BuildPerDeploymentSettingsManager(repository.RepositoryPath, settingsProviders);

                string delayMaxInStr = perDeploymentSettings.GetValue(SettingsKeys.MaxRandomDelayInSec);
                if (!String.IsNullOrEmpty(delayMaxInStr))
                {
                    int maxDelay;
                    if (!Int32.TryParse(delayMaxInStr, out maxDelay) || maxDelay < 0)
                    {
                        tracer.Trace("Invalid {0} value, expect a positive integer, received {1}", SettingsKeys.MaxRandomDelayInSec, delayMaxInStr);
                    }
                    else
                    {
                        tracer.Trace("{0} is set to {1}s", SettingsKeys.MaxRandomDelayInSec, maxDelay);
                        int gap = _random.Next(maxDelay);
                        using (tracer.Step("Randomization applied to {0}, Start sleeping for {1}s", maxDelay, gap))
                        {
                            logger.Log(Resources.Log_DelayingBeforeDeployment, gap);
                            await Task.Delay(TimeSpan.FromSeconds(gap));
                        }
                    }
                }

                try
                {
                    using (tracer.Step("Determining deployment builder"))
                    {
                        builder = _builderFactory.CreateBuilder(tracer, innerLogger, perDeploymentSettings, repository, deploymentInfo);
                        deploymentAnalytics.ProjectType = builder.ProjectType;
                        tracer.Trace("Builder is {0}", builder.GetType().Name);
                    }
                }
                catch (Exception ex)
                {
                    // If we get a TargetInvocationException, use the inner exception instead to avoid
                    // useless 'Exception has been thrown by the target of an invocation' messages
                    var targetInvocationException = ex as System.Reflection.TargetInvocationException;
                    if (targetInvocationException != null)
                    {
                        ex = targetInvocationException.InnerException;
                    }

                    _globalLogger.Log(ex);

                    innerLogger.Log(ex);

                    MarkStatusComplete(currentStatus, success: false, deploymentAnalytics: deploymentAnalytics);

                    FailDeployment(tracer, deployStep, deploymentAnalytics, ex, GetLogger(id, tracer, deploymentInfo));

                    return;
                }

                // Create a directory for the script output temporary artifacts
                // Use tick count (in hex) instead of guid to keep the path for getting to long
                buildTempPath = Path.Combine(_environment.TempPath, DateTime.UtcNow.Ticks.ToString("x"));
                FileSystemHelpers.EnsureDirectory(buildTempPath);

                var context = new DeploymentContext
                {
                    NextManifestFilePath = GetDeploymentManifestPath(id),
                    PreviousManifestFilePath = GetActiveDeploymentManifestPath(),
                    IgnoreManifest = deploymentInfo != null && deploymentInfo.CleanupTargetDirectory,
                    // Ignoring the manifest will cause kudusync to delete sub-directories / files
                    // in the destination directory that are not present in the source directory,
                    // without checking the manifest to see if the file was copied over to the destination
                    // during a previous kudusync operation. This effectively performs a clean deployment
                    // from the source to the destination directory.
                    Tracer = tracer,
                    Logger = logger,
                    GlobalLogger = _globalLogger,
                    OutputPath = GetOutputPath(deploymentInfo, _environment, perDeploymentSettings),
                    BuildTempPath = buildTempPath,
                    CommitId = id,
                    Message = changeSet.Message
                };

                if (context.PreviousManifestFilePath == null)
                {
                    // this file (/site/firstDeploymentManifest) capture the last active deployment when disconnecting SCM
                    context.PreviousManifestFilePath = Path.Combine(_environment.SiteRootPath, Constants.FirstDeploymentManifestFileName);
                    if (!FileSystemHelpers.FileExists(context.PreviousManifestFilePath))
                    {
                        // In the first deployment we want the wwwroot directory to be cleaned, we do that using a manifest file
                        // That has the expected content of a clean deployment (only one file: hostingstart.html)
                        // This will result in KuduSync cleaning this file.
                        context.PreviousManifestFilePath = Path.Combine(_environment.ScriptPath, Constants.FirstDeploymentManifestFileName);
                    }
                }

                PreDeployment(tracer);

                using (tracer.Step("Building"))
                {
                    try
                    {
                        await builder.Build(context);
                        builder.PostBuild(context);

                        await RestartMainSiteIfNeeded(tracer, logger, deploymentInfo);

                        if (ScmHostingConfigurations.FunctionsSyncTriggersDelaySeconds == 0)
                        {
                            await PostDeploymentHelper.SyncTriggersIfFunctionsSite(_environment.RequestId, new PostDeploymentTraceListener(tracer, logger),
                                deploymentInfo?.SyncFunctionsTriggersPath, tracePath: _environment.TracePath);
                        }

                        TouchWatchedFileIfNeeded(_settings, deploymentInfo, context);

                        FinishDeployment(id, deployStep, deploymentAnalytics, GetLogger(id, tracer, deploymentInfo));

                        deploymentAnalytics.VsProjectId = TryGetVsProjectId(context);
                        deploymentAnalytics.Result = DeployStatus.Success.ToString();
                    }
                    catch (Exception ex)
                    {
                        MarkStatusComplete(currentStatus, success: false, deploymentAnalytics: deploymentAnalytics);

                        FailDeployment(tracer, deployStep, deploymentAnalytics, ex, GetLogger(id, tracer, deploymentInfo));

                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                FailDeployment(tracer, deployStep, deploymentAnalytics, ex, GetLogger(id, tracer, deploymentInfo));
            }
            finally
            {
                // Clean the temp folder up
                CleanBuild(tracer, buildTempPath);
            }
        }

        private static void TouchWatchedFileIfNeeded(IDeploymentSettingsManager settings, DeploymentInfoBase deploymentInfo, DeploymentContext context)
        {
            if (deploymentInfo != null && !deploymentInfo.WatchedFileEnabled)
            {
                return;
            }

            if (!settings.RunFromZip() && settings.TouchWatchedFileAfterDeployment())
            {
                TryTouchWatchedFile(context, deploymentInfo);
            }
        }

        private void PreDeployment(ITracer tracer)
        {
            if (Environment.IsAzureEnvironment()
                && FileSystemHelpers.DirectoryExists(_environment.SSHKeyPath)
                && OSDetector.IsOnWindows())
            {
                string src = Path.GetFullPath(_environment.SSHKeyPath);
                string dst = Path.GetFullPath(Path.Combine(System.Environment.GetEnvironmentVariable("USERPROFILE"), Constants.SSHKeyPath));

                if (!String.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    // copy %HOME%\.ssh to %USERPROFILE%\.ssh key to workaround
                    // npm with private ssh git dependency
                    using (tracer.Step("Copying SSH keys"))
                    {
                        FileSystemHelpers.CopyDirectoryRecursive(src, dst, overwrite: true);
                    }
                }
            }
        }

        private static void CleanBuild(ITracer tracer, string buildTempPath)
        {
            if (buildTempPath != null)
            {
                using (tracer.Step("Cleaning up temp files"))
                {
                    try
                    {
                        FileSystemHelpers.DeleteDirectorySafe(buildTempPath);
                    }
                    catch (Exception ex)
                    {
                        tracer.TraceError(ex);
                    }
                }
            }
        }

        private static void FailDeployment(ITracer tracer, IDisposable deployStep, DeploymentAnalytics deploymentAnalytics, Exception ex, ILogger logger)
        {
            // End the deploy step
            deployStep.Dispose();

            tracer.TraceError(ex);

            deploymentAnalytics.Result = "Failed";
            deploymentAnalytics.Error = ex.ToString();

            logger.Log(Resources.Log_DeploymentFailed);
        }

        private static string GetOutputPath(DeploymentInfoBase deploymentInfo, IEnvironment environment, IDeploymentSettingsManager perDeploymentSettings)
        {
            if (deploymentInfo?.Deployer == Constants.OneDeploy || deploymentInfo?.Deployer == Constants.ZipDeploy)
            {
                if (!string.IsNullOrWhiteSpace(deploymentInfo.TargetRootPath))
                {
                    return deploymentInfo.TargetRootPath;
                }
            }

            string targetSubDirectoryRelativePath = perDeploymentSettings.GetTargetPath();

            if (String.IsNullOrWhiteSpace(targetSubDirectoryRelativePath))
            {
                targetSubDirectoryRelativePath = deploymentInfo?.TargetSubDirectoryRelativePath;
            }

            if (!String.IsNullOrWhiteSpace(targetSubDirectoryRelativePath))
            {
                targetSubDirectoryRelativePath = targetSubDirectoryRelativePath.Trim('\\', '/');
                return Path.GetFullPath(Path.Combine(environment.WebRootPath, targetSubDirectoryRelativePath));
            }

            return environment.WebRootPath;
        }

        private IEnumerable<DeployResult> EnumerateResults()
        {
            if (!FileSystemHelpers.DirectoryExists(_environment.DeploymentsPath))
            {
                yield break;
            }

            string activeDeploymentId = _status.ActiveDeploymentId;
            bool isDeploying = IsDeploying;

            foreach (var id in FileSystemHelpers.GetDirectoryNames(_environment.DeploymentsPath).Where(p => !p.Equals(@"tools", StringComparison.OrdinalIgnoreCase)))
            {
                DeployResult result = GetResult(id, activeDeploymentId, isDeploying);

                if (result != null)
                {
                    yield return result;
                }
            }
        }

        /// <summary>
        /// Ensure the deployment is in a valid state.
        /// </summary>
        private IDeploymentStatusFile VerifyDeployment(string id, bool isDeploying)
        {
            IDeploymentStatusFile statusFile = _status.Open(id);

            if (statusFile == null)
            {
                return null;
            }

            if (statusFile.Complete)
            {
                return statusFile;
            }

            // There's an incomplete deployment, yet nothing is going on, mark this deployment as failed
            // since it probably means something died
            if (!isDeploying)
            {
                ILogger logger = GetLogger(id);
                logger.LogUnexpectedError();

                MarkStatusComplete(statusFile, success: false);
            }

            return statusFile;
        }

        /// <summary>
        /// Runs post deployment steps.
        /// - Marks the active deployment
        /// - Sets the complete flag
        /// </summary>
        private void FinishDeployment(string id, IDisposable deployStep, DeploymentAnalytics deploymentAnalytics, ILogger logger)
        {
            using (deployStep)
            {
                logger.Log(Resources.Log_DeploymentSuccessful);

                IDeploymentStatusFile currentStatus = _status.Open(id);
                MarkStatusComplete(currentStatus, success: true, deploymentAnalytics: deploymentAnalytics);

                _status.ActiveDeploymentId = id;

                // Delete first deployment manifest since it is no longer needed
                FileSystemHelpers.DeleteFileSafe(Path.Combine(_environment.SiteRootPath, Constants.FirstDeploymentManifestFileName));
            }
        }

        // Touch watched file (web.config, web.xml, etc)
        private static void TryTouchWatchedFile(DeploymentContext context, DeploymentInfoBase deploymentInfo)
        {
            try
            {
                string watchedFileRelativePath = deploymentInfo?.WatchedFilePath;
                if (string.IsNullOrWhiteSpace(watchedFileRelativePath))
                {
                    watchedFileRelativePath = "web.config";
                }

                string watchedFileAbsolutePath = Path.Combine(context.OutputPath, watchedFileRelativePath);

                if (File.Exists(watchedFileAbsolutePath))
                {
                    File.SetLastWriteTimeUtc(watchedFileAbsolutePath, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                context.Tracer.TraceError(ex);
            }
        }

        private static string TryGetVsProjectId(DeploymentContext context)
        {
            try
            {
                // Read web.config
                string webConfigPath = Path.Combine(context.OutputPath, "web.config");
                if (File.Exists(webConfigPath))
                {
                    using (var stream = File.OpenRead(webConfigPath))
                    {
                        Guid? projectId = ProjectGuidParser.GetProjectGuidFromWebConfig(stream);
                        return projectId.HasValue ? projectId.Value.ToString() : null;
                    }
                }
            }
            catch (Exception ex)
            {
                context.Tracer.TraceError(ex);
            }

            return null;
        }

        private static string TrimId(string id)
        {
            return id.Substring(0, 10);
        }

        public ILogger GetLogger(string id)
        {
            var path = GetLogPath(id);
            var logger = GetLoggerForFile(path);
            return new ProgressLogger(id, _status, new CascadeLogger(logger, _globalLogger));
        }

        public ILogger GetLogger(string id, ITracer tracer, DeploymentInfoBase deploymentInfo)
        {
            var path = GetLogPath(id);
            var logger = GetLoggerForFile(path); 
            ProgressLogger progressLogger = new ProgressLogger(id, _status, new CascadeLogger(logger, new DeploymentLogger(_globalLogger, tracer, deploymentInfo)));
            return progressLogger;
        }                

        /// <summary>
        /// Prepare a directory with the deployment script and .deployment file.
        /// </summary>
        /// <returns>The directory path for the files or null if no deployment script exists.</returns>
        public string GetDeploymentScriptContent()
        {
            var cachedDeploymentScriptPath = GetCachedDeploymentScriptPath(_environment);

            if (!FileSystemHelpers.FileExists(cachedDeploymentScriptPath))
            {
                return null;
            }

            return FileSystemHelpers.ReadAllText(cachedDeploymentScriptPath);
        }

        public static string GetCachedDeploymentScriptPath(IEnvironment environment)
        {
            return Path.GetFullPath(Path.Combine(environment.DeploymentToolsPath, DeploymentScriptFileName));
        }

        private string GetActiveDeploymentManifestPath()
        {
            string id = _status.ActiveDeploymentId;

            // We've seen rare cases of corruption where the file is full of NUL characters.
            // If we see the first char is 0, treat the file as corrupted and ignore it
            if (String.IsNullOrEmpty(id) || id[0] == 0)
            {
                return null;
            }

            string manifestPath = GetDeploymentManifestPath(id);

            // If the manifest file doesn't exist, don't return it as it could confuse kudusync.
            // This can happen if the deployment was created with just metadata but no actually deployment took place.
            if (!FileSystemHelpers.FileExists(manifestPath))
            {
                return null;
            }

            return manifestPath;
        }

        private string GetDeploymentManifestPath(string id)
        {
            return Path.Combine(GetRoot(id), Constants.ManifestFileName);
        }

        /// <summary>
        /// This function handles getting the path for the log file.
        /// If 'log.xml' exists then use that which will use XmlLogger, this is to support existing log files
        /// else use 'log.log' which will use TextLogger. Moving forward deployment will always use text logger.
        /// The logic to get the right logger is in GetLoggerForFile()
        /// </summary>
        /// <param name="id">deploymentId which is part of the path for the log file</param>
        /// <param name="ensureDirectory">Create the directory if it doesn't exist</param>
        /// <returns>log file path</returns>
        private string GetLogPath(string id, bool ensureDirectory = true)
        {
            var logPath = Path.Combine(GetRoot(id, ensureDirectory), XmlLogFile);
            return FileSystemHelpers.FileExists(logPath)
                ? logPath
                : Path.Combine(GetRoot(id, ensureDirectory), TextLogFile);
        }

        private string GetRoot(string id, bool ensureDirectory = true)
        {
            string path = Path.Combine(_environment.DeploymentsPath, id);

            if (ensureDirectory)
            {
                return FileSystemHelpers.EnsureDirectory(path);
            }

            return path;
        }

        private bool IsActive(string id)
        {
            return id.Equals(_status.ActiveDeploymentId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// use XmlLogger if the file ends with .xml
        /// Otherwise use StructuredTextLogger
        /// </summary>
        /// <param name="logPath"></param>
        /// <returns>XmlLogger or StructuredTextLogger</returns>
        private IDetailedLogger GetLoggerForFile(string logPath)
        {
            if (logPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return new XmlLogger(logPath, _analytics);
            }
            else
            {
                return new StructuredTextLogger(logPath, _analytics);
            }
        }

        private class DeploymentAnalytics : IDisposable
        {
            private readonly IAnalytics _analytics;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private bool _disposed;
            private string _siteMode;

            public DeploymentAnalytics(IAnalytics analytics, IDeploymentSettingsManager settings)
            {
                _analytics = analytics;
                _siteMode = settings.GetWebSiteSku();
            }

            public string ProjectType { get; set; }

            public string Result { get; set; }

            public string Error { get; set; }

            public string VsProjectId { get; set; }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _stopwatch.Stop();
                    _analytics.ProjectDeployed(ProjectType, Result, Error, _stopwatch.ElapsedMilliseconds, _siteMode, VsProjectId);
                    _disposed = true;
                }
            }
        }
    }
}