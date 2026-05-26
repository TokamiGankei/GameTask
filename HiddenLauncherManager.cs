using System.IO;

namespace GameTaskPlugin
{
    public class HiddenLauncherManager
    {
        private readonly Logger logger;

        private readonly string createLauncherPath;
        private readonly string deleteLauncherPath;
        private readonly string cleanOrphansLauncherPath;

        private readonly string createPs1Path;
        private readonly string deletePs1Path;
        private readonly string cleanOrphansPs1Path;

        public readonly string ResultFile; // written by CreateTasks.ps1 for success notification

        public HiddenLauncherManager(Logger logger, string pluginDataPath)
        {
            this.logger = logger;

            string cacheFolder = Path.Combine(pluginDataPath, "Cache");
            Directory.CreateDirectory(cacheFolder);

            createPs1Path       = Path.Combine(cacheFolder, "CreateTasks.ps1");
            deletePs1Path       = Path.Combine(cacheFolder, "DeleteTasks.ps1");
            cleanOrphansPs1Path = Path.Combine(cacheFolder, "CleanOrphanTasks.ps1");
            ResultFile          = Path.Combine(cacheFolder, "LastCreateResult.txt");

            File.WriteAllText(createPs1Path,       GetCreateTasksScript());
            File.WriteAllText(deletePs1Path,       GetDeleteTasksScript());
            File.WriteAllText(cleanOrphansPs1Path, GetCleanOrphansScript());

            createLauncherPath       = Path.Combine(cacheFolder, "HiddenCreateTasks.vbs");
            deleteLauncherPath       = Path.Combine(cacheFolder, "HiddenDeleteTasks.vbs");
            cleanOrphansLauncherPath = Path.Combine(cacheFolder, "HiddenCleanOrphanTasks.vbs");

            WriteVbs(createLauncherPath,       createPs1Path);
            WriteVbs(deleteLauncherPath,       deletePs1Path);
            WriteVbs(cleanOrphansLauncherPath, cleanOrphansPs1Path);

            logger.Log("Hidden launchers and PowerShell helpers created.");
        }

        private static void WriteVbs(string vbsPath, string ps1Path)
        {
            // Use chr(34) for embedded quotes — avoids 800A0401 syntax errors
            // that occur when escaping quotes directly inside VBScript strings.
            File.WriteAllText(vbsPath,
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                $"shell.Run \"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File \" & Chr(34) & \"{ps1Path}\" & Chr(34), 0, False\r\n");
        }

        public string GetCreateLauncherPath()       => createLauncherPath;
        public string GetDeleteLauncherPath()       => deleteLauncherPath;
        public string GetCleanOrphansLauncherPath() => cleanOrphansLauncherPath;

        // =====================================================
        // CREATE TASKS SCRIPT
        // Writes LastCreateResult.txt with "created=N|failed=M"
        // so the plugin can show a success/failure notification.
        // =====================================================

        private string GetCreateTasksScript()
        {
            return @"
$taskFolder  = '\GameTask\'
$pendingFile = Join-Path $PSScriptRoot 'PendingTasks.txt'
$resultFile  = Join-Path $PSScriptRoot 'LastCreateResult.txt'
$logFile     = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null
Add-Content $logFile (""`n===== CREATE START "" + (Get-Date) + "" ====="")`n"")

$created = 0
$failed  = 0

if (!(Test-Path $pendingFile)) {
    Add-Content $logFile 'No PendingTasks.txt found.'
    Set-Content $resultFile 'created=0|failed=0'
    exit
}

$service = New-Object -ComObject Schedule.Service
$service.Connect()

try   { $service.GetFolder($taskFolder) | Out-Null }
catch {
    $root = $service.GetFolder('\')
    $root.CreateFolder('GameTask')
    Add-Content $logFile 'Task Scheduler folder \GameTask created.'
}

$lines = [System.IO.File]::ReadAllLines($pendingFile, [System.Text.Encoding]::UTF8)

foreach ($line in $lines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    $parts = $line.Split('|')
    if ($parts.Count -lt 2) {
        Add-Content $logFile ""SKIP invalid line: $line""
        $failed++
        continue
    }

    $gameName = $parts[0].Trim()
    $exePath  = $parts[1].Trim()

    if (!(Test-Path $exePath)) {
        Add-Content $logFile ""SKIP exe not found: $gameName -> $exePath""
        $failed++
        continue
    }

    $safeName = $gameName -replace '[^a-zA-Z0-9_\- ]', '_'
    $taskName = 'GameTask_v1_' + $safeName

    try {
        Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskFolder -Confirm:$false -ErrorAction SilentlyContinue
    } catch {}

    try {
        $exeDir    = Split-Path $exePath -Parent
        $action    = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $exeDir
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive
        $settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $task      = New-ScheduledTask -Action $action -Principal $principal -Settings $settings
        $task.Settings.Priority = 4

        Register-ScheduledTask -TaskName $taskName -TaskPath $taskFolder -InputObject $task -Force
        Add-Content $logFile ""CREATED: $taskName -> $exePath""
        $created++
    }
    catch {
        Add-Content $logFile ""ERROR creating task: $taskName -> $_""
        $failed++
    }
}

Clear-Content $pendingFile
Set-Content $resultFile ""created=$created|failed=$failed""
Add-Content $logFile ""created=$created failed=$failed""
Add-Content $logFile ('===== CREATE END ' + (Get-Date) + ' =====')
";
        }

        private string GetDeleteTasksScript()
        {
            return @"
$taskFolder = '\GameTask\'
$deleteFile = Join-Path $PSScriptRoot 'DeleteTasks.txt'
$logFile    = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null
Add-Content $logFile (""`n===== DELETE START "" + (Get-Date) + "" ====="")`n"")

if (!(Test-Path $deleteFile)) {
    Add-Content $logFile 'No DeleteTasks.txt found.'
    exit
}

$lines = [System.IO.File]::ReadAllLines($deleteFile, [System.Text.Encoding]::UTF8)

foreach ($taskName in $lines) {
    if ([string]::IsNullOrWhiteSpace($taskName)) { continue }

    try {
        Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskFolder -Confirm:$false -ErrorAction SilentlyContinue
        Add-Content $logFile ""DELETED OR NOT FOUND: $taskName""
    }
    catch {
        Add-Content $logFile ""ERROR deleting task: $taskName -> $_""
    }
}

Clear-Content $deleteFile
Add-Content $logFile ('===== DELETE END ' + (Get-Date) + ' =====')
";
        }

        private string GetCleanOrphansScript()
        {
            return @"
$taskFolder     = '\GameTask\'
$knownTasksFile = Join-Path $PSScriptRoot 'KnownTasks.txt'
$logFile        = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null
Add-Content $logFile (""`n===== CLEAN ORPHANS START "" + (Get-Date) + "" ====="")`n"")

$knownTasks = @()
if (Test-Path $knownTasksFile) {
    $knownTasks = [System.IO.File]::ReadAllLines($knownTasksFile, [System.Text.Encoding]::UTF8) |
                  Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$service = New-Object -ComObject Schedule.Service
$service.Connect()

try {
    $folder = $service.GetFolder($taskFolder)
} catch {
    Add-Content $logFile 'GameTask folder not found in Task Scheduler - nothing to clean.'
    exit
}

$tasks = $folder.GetTasks(0)

foreach ($task in $tasks) {
    $name = $task.Name
    if ($knownTasks -notcontains $name) {
        try {
            $folder.DeleteTask($name, 0)
            Add-Content $logFile ""ORPHAN REMOVED: $name""
        } catch {
            Add-Content $logFile ""ERROR removing orphan: $name -> $_""
        }
    } else {
        Add-Content $logFile ""KEPT: $name""
    }
}

Add-Content $logFile ('===== CLEAN ORPHANS END ' + (Get-Date) + ' =====')
";
        }

        // =====================================================
        // DELETE TASKS SCRIPT
        // =====================================================

        private string GetDeleteTasksScript()
        {
            return @"
$taskFolder = '\GameTask\'
$deleteFile = Join-Path $PSScriptRoot 'DeleteTasks.txt'
$logFile    = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null
Add-Content $logFile (""`n===== DELETE START "" + (Get-Date) + "" ====="")`n"")

if (!(Test-Path $deleteFile)) {
    Add-Content $logFile 'No DeleteTasks.txt found.'
    exit
}

$lines = [System.IO.File]::ReadAllLines($deleteFile, [System.Text.Encoding]::UTF8)

foreach ($taskName in $lines) {
    if ([string]::IsNullOrWhiteSpace($taskName)) { continue }

    try {
        Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskFolder -Confirm:$false -ErrorAction SilentlyContinue
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

        // =====================================================
        // CLEAN ORPHAN TASKS SCRIPT
        // =====================================================

        private string GetCleanOrphansScript()
        {
            return @"
$taskFolder      = '\GameTask\'
$knownTasksFile  = Join-Path $PSScriptRoot 'KnownTasks.txt'
$logFile         = Join-Path $PSScriptRoot '..\Logs\PS1.log'

New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null
Add-Content $logFile (""`n===== CLEAN ORPHANS START "" + (Get-Date) + "" ====="")`n"")

$knownTasks = @()
if (Test-Path $knownTasksFile) {
    $knownTasks = [System.IO.File]::ReadAllLines($knownTasksFile, [System.Text.Encoding]::UTF8) |
                  Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$service = New-Object -ComObject Schedule.Service
$service.Connect()

try {
    $folder = $service.GetFolder($taskFolder)
} catch {
    Add-Content $logFile 'GameTask folder not found in Task Scheduler — nothing to clean.'
    exit
}

$tasks = $folder.GetTasks(0)

foreach ($task in $tasks) {
    $name = $task.Name
    if ($knownTasks -notcontains $name) {
        try {
            $folder.DeleteTask($name, 0)
            Add-Content $logFile ""ORPHAN REMOVED: $name""
        } catch {
            Add-Content $logFile ""ERROR removing orphan: $name -> $_""
        }
    } else {
        Add-Content $logFile ""KEPT: $name""
    }
}

Add-Content $logFile ('===== CLEAN ORPHANS END ' + (Get-Date) + ' =====`n')
";
        }
    }
}
