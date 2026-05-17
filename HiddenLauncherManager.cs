using System.IO;

namespace GameTaskPlugin
{
    public class HiddenLauncherManager
    {
        private readonly Logger logger;

        private readonly string createLauncherPath;
        private readonly string deleteLauncherPath;

        public HiddenLauncherManager(Logger logger, string pluginDataPath)
        {
            this.logger = logger;

            string cacheFolder = Path.Combine(pluginDataPath, "Cache");
            Directory.CreateDirectory(cacheFolder);

            string createPs1Path = Path.Combine(cacheFolder, "CreateTasks.ps1");
            string deletePs1Path = Path.Combine(cacheFolder, "DeleteTasks.ps1");

            File.WriteAllText(createPs1Path, GetCreateTasksScript());
            File.WriteAllText(deletePs1Path, GetDeleteTasksScript());

            createLauncherPath = Path.Combine(cacheFolder, "HiddenCreateTasks.vbs");
            deleteLauncherPath = Path.Combine(cacheFolder, "HiddenDeleteTasks.vbs");

            File.WriteAllText(
                createLauncherPath,
$@"Set shell = CreateObject(""WScript.Shell"")
shell.Run ""powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File """"{createPs1Path}"""" "", 0, False
");

            File.WriteAllText(
                deleteLauncherPath,
$@"Set shell = CreateObject(""WScript.Shell"")
shell.Run ""powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File """"{deletePs1Path}"""" "", 0, False
");

            logger.Log("Hidden launchers and PowerShell helpers created.");
        }

        public string GetCreateLauncherPath()
        {
            return createLauncherPath;
        }

        public string GetDeleteLauncherPath()
        {
            return deleteLauncherPath;
        }

        private string GetCreateTasksScript()
        {
            return @"
$taskFolder = '\GameTask\'
$pendingFile = Join-Path $PSScriptRoot 'PendingTasks.txt'
$logFile = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null

Add-Content $logFile ('`n===== CREATE START ' + (Get-Date) + ' =====`n')

if (!(Test-Path $pendingFile)) {
    Add-Content $logFile 'No PendingTasks.txt found.'
    exit
}

$service = New-Object -ComObject Schedule.Service
$service.Connect()

try {
    $service.GetFolder($taskFolder) | Out-Null
}
catch {
    $root = $service.GetFolder('\')
    $root.CreateFolder('GameTask')
    Add-Content $logFile 'Task Scheduler folder \GameTask created.'
}

$lines = [System.IO.File]::ReadAllLines(
    $pendingFile,
    [System.Text.Encoding]::UTF8
)

foreach ($line in $lines) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $parts = $line.Split('|')

    if ($parts.Count -lt 2) {
        Add-Content $logFile ""SKIP invalid line: $line""
        continue
    }

    $gameName = $parts[0].Trim()
    $exePath = $parts[1].Trim()

    if (!(Test-Path $exePath)) {
        Add-Content $logFile ""SKIP exe not found: $gameName -> $exePath""
        continue
    }

    $safeName = $gameName -replace '[^a-zA-Z0-9_\- ]', '_'
    $taskName = 'GameTask_v1_' + $safeName

    try {
        Unregister-ScheduledTask `
            -TaskName $taskName `
            -TaskPath $taskFolder `
            -Confirm:$false `
            -ErrorAction SilentlyContinue
    }
    catch {}

    try {
        $action = New-ScheduledTaskAction -Execute $exePath

        $principal = New-ScheduledTaskPrincipal `
            -UserId $env:USERNAME `
            -RunLevel Highest `
            -LogonType Interactive

        $settings = New-ScheduledTaskSettingsSet `
            -StartWhenAvailable `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries

        $task = New-ScheduledTask `
            -Action $action `
            -Principal $principal `
            -Settings $settings

        Register-ScheduledTask `
            -TaskName $taskName `
            -TaskPath $taskFolder `
            -InputObject $task `
            -Force

        Add-Content $logFile ""CREATED: $taskName -> $exePath""
    }
    catch {
        Add-Content $logFile ""ERROR creating task: $taskName -> $_""
    }
}

Clear-Content $pendingFile
Add-Content $logFile ('===== CREATE END ' + (Get-Date) + ' =====`n')
";
        }

        private string GetDeleteTasksScript()
        {
            return @"
$taskFolder = '\GameTask\'
$deleteFile = Join-Path $PSScriptRoot 'DeleteTasks.txt'
$logFile = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null

Add-Content $logFile ('`n===== DELETE START ' + (Get-Date) + ' =====`n')

if (!(Test-Path $deleteFile)) {
    Add-Content $logFile 'No DeleteTasks.txt found.'
    exit
}

$lines = [System.IO.File]::ReadAllLines(
    $deleteFile,
    [System.Text.Encoding]::UTF8
)

foreach ($taskName in $lines) {
    if ([string]::IsNullOrWhiteSpace($taskName)) {
        continue
    }

    try {
        Unregister-ScheduledTask `
            -TaskName $taskName `
            -TaskPath $taskFolder `
            -Confirm:$false `
            -ErrorAction SilentlyContinue

        Add-Content $logFile ""DELETED OR NOT FOUND: $taskName""
    }
    catch {
        Add-Content $logFile ""ERROR deleting task: $taskName -> $_""
    }
}

Clear-Content $deleteFile
Add-Content $logFile ('===== DELETE END ' + (Get-Date) + ' =====`n')
";
        }
    }
}