using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Processes;

namespace NzbDrone.Update.UpdateEngine
{
    public interface IInstallUpdateService
    {
        void Start(string installationFolder, int processId);
    }

    public class InstallUpdateService : IInstallUpdateService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDetectApplicationType _detectApplicationType;
        private readonly ITerminateNzbDrone _terminateNzbDrone;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IBackupAndRestore _backupAndRestore;
        private readonly IBackupAppData _backupAppData;
        private readonly IStartNzbDrone _startNzbDrone;
        private readonly IProcessProvider _processProvider;
        private readonly Logger _logger;

        public InstallUpdateService(IDiskProvider diskProvider,
                                    IDiskTransferService diskTransferService,
                                    IDetectApplicationType detectApplicationType,
                                    ITerminateNzbDrone terminateNzbDrone,
                                    IAppFolderInfo appFolderInfo,
                                    IBackupAndRestore backupAndRestore,
                                    IBackupAppData backupAppData,
                                    IStartNzbDrone startNzbDrone,
                                    IProcessProvider processProvider,
                                    Logger logger)
        {
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _detectApplicationType = detectApplicationType;
            _terminateNzbDrone = terminateNzbDrone;
            _appFolderInfo = appFolderInfo;
            _backupAndRestore = backupAndRestore;
            _backupAppData = backupAppData;
            _startNzbDrone = startNzbDrone;
            _processProvider = processProvider;
            _logger = logger;
        }

        private void Verify(string targetFolder, int processId)
        {
            _logger.Info("Verifying requirements before update...");

            if (String.IsNullOrWhiteSpace(targetFolder))
                throw new ArgumentException("Target folder can not be null or empty");

            if (!_diskProvider.FolderExists(targetFolder))
                throw new DirectoryNotFoundException("Target folder doesn't exist " + targetFolder);

            if (processId < 1)
            {
                throw new ArgumentException("Invalid process ID: " + processId);
            }

            if (!_processProvider.Exists(processId))
            {
                throw new ArgumentException("Process with ID doesn't exist " + processId);
            }

            _logger.Info("Verifying Update Folder");
            if (!_diskProvider.FolderExists(_appFolderInfo.GetUpdatePackageFolder()))
                throw new DirectoryNotFoundException("Update folder doesn't exist " + _appFolderInfo.GetUpdatePackageFolder());
        }

        public void Start(string installationFolder, int processId)
        {
            Verify(installationFolder, processId);

            var appType = _detectApplicationType.GetAppType();

            try
            {
                _processProvider.FindProcessByName(ProcessProvider.NZB_DRONE_CONSOLE_PROCESS_NAME);
                _processProvider.FindProcessByName(ProcessProvider.NZB_DRONE_PROCESS_NAME);

                if (OsInfo.IsWindows)
                {
                    _terminateNzbDrone.Terminate(processId);
                }

                _backupAndRestore.Backup(installationFolder);
                _backupAppData.Backup();

                try
                {
                    _logger.Info("Emptying installation folder");
                    _diskProvider.EmptyFolder(installationFolder);

                    _logger.Info("Copying new files to target folder");
                    _diskTransferService.TransferFolder(_appFolderInfo.GetUpdatePackageFolder(), installationFolder, TransferMode.Copy, false);

                    // Set executable flag on Sonarr app
                    if (OsInfo.IsOsx)
                    {
                        _diskProvider.SetPermissions(Path.Combine(installationFolder, "Sonarr"), "0755", null, null);
                    }
                }
                catch (Exception e)
                {
                    _logger.FatalException("Failed to copy upgrade package to target folder.", e);
                    _backupAndRestore.Restore(installationFolder);
                }
            }
            finally
            {
                if (OsInfo.IsWindows)
                {
                    _startNzbDrone.Start(appType, installationFolder);
                }
                else
                {
                    _terminateNzbDrone.Terminate(processId);

                    _logger.Info("Waiting for external auto-restart.");
                    for (int i = 0; i < 5; i++)
                    {
                        System.Threading.Thread.Sleep(1000);

                        if (_processProvider.Exists(ProcessProvider.NZB_DRONE_PROCESS_NAME))
                        {
                            _logger.Info("Sonarr was restarted by external process.");
                            break;
                        }
                    }

                    if (!_processProvider.Exists(ProcessProvider.NZB_DRONE_PROCESS_NAME))
                    {
                        _startNzbDrone.Start(appType, installationFolder);
                    }
                }
            }

        }
    }
}
