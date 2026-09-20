<#
.SYNOPSIS
    Hardens the NTFS ACLs on the TidalSql data directory so that only a dedicated
    TidalSql service account (plus Administrators and SYSTEM) can read or modify the
    underlying Parquet and system files.

.DESCRIPTION
    TidalSql stores every database as plain Parquet files and its security catalog as
    JSON under a data root (default: %ProgramData%\TidalSql). By default that folder
    inherits permissions that let ordinary interactive users browse and edit those
    files directly from the OS, bypassing the engine's logins, roles and GRANT/DENY
    checks entirely.

    This script locks the directory down so the *only* principals with access are:
      * the dedicated TidalSql service account the engine runs as,
      * the local Administrators group (needed for backup/maintenance), and
      * NT AUTHORITY\SYSTEM.

    Interactive users must then go through the engine (logins + permissions) to see
    any data — the same trust model SQL Server uses for its data files.

    IMPORTANT (trust boundary): NTFS ACLs are only an effective lock when the TidalSql
    engine runs as a *different* account than the interactive users, and those users
    are NOT local administrators. A local administrator can always take ownership and
    reset ACLs. This is the identical limitation SQL Server has; it is not a bug.

    The script is idempotent and can be re-run safely after adding new databases.

.PARAMETER Path
    The TidalSql data root to secure. Defaults to $env:ProgramData\TidalSql.

.PARAMETER ServiceAccount
    The account TidalSql runs as, which must retain full access
    (e.g. 'MACHINE\TidalSqlSvc', '.\TidalSqlSvc', or 'DOMAIN\svc-tidalsql').
    For a Windows Service running under a virtual account, pass
    'NT SERVICE\<ServiceName>'.

.PARAMETER WhatIf
    Shows the icacls commands that would run without applying them.

.EXAMPLE
    # Run elevated (Administrator):
    .\Secure-DataDir.ps1 -ServiceAccount '.\TidalSqlSvc'

.EXAMPLE
    .\Secure-DataDir.ps1 -Path 'D:\TidalSqlData' -ServiceAccount 'NT SERVICE\TidalSql'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Path = (Join-Path $env:ProgramData 'TidalSql'),

    [Parameter(Mandatory = $true)]
    [string]$ServiceAccount
)

$ErrorActionPreference = 'Stop'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw "This script must be run from an elevated (Administrator) PowerShell session."
}

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Data root '$Path' does not exist. Start TidalSql once (or create the folder) before securing it."
}

$Path = (Resolve-Path -LiteralPath $Path).Path

# Validate the service account resolves before we strip inheritance.
try {
    $null = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])
}
catch {
    throw "Service account '$ServiceAccount' could not be resolved to a SID. Create the account first, or check the name (e.g. '.\TidalSqlSvc' or 'NT SERVICE\TidalSql')."
}

Write-Host "Securing TidalSql data root: $Path" -ForegroundColor Cyan
Write-Host "Granting exclusive access to: $ServiceAccount, Administrators, SYSTEM" -ForegroundColor Cyan

# 1. Take ownership so we can always rewrite the ACL (Administrators).
if ($PSCmdlet.ShouldProcess($Path, "Take ownership (Administrators)")) {
    & takeown.exe /F "$Path" /R /D Y | Out-Null
}

# 2. Reset to inherited defaults first (clean, known baseline), then we override.
if ($PSCmdlet.ShouldProcess($Path, "Reset ACLs to a clean baseline")) {
    & icacls.exe "$Path" /reset /T /C /Q | Out-Null
}

# 3. Disable inheritance and drop all inherited ACEs.
if ($PSCmdlet.ShouldProcess($Path, "Disable inheritance / remove inherited ACEs")) {
    & icacls.exe "$Path" /inheritance:r | Out-Null
}

# 4. Grant the three principals that are allowed. (OI)(CI) => applies to files and
#    subfolders; grants propagate to the whole tree via /T on a subsequent pass.
$grants = @(
    "$ServiceAccount:(OI)(CI)F",
    "*S-1-5-32-544:(OI)(CI)F",   # BUILTIN\Administrators (well-known SID, locale-independent)
    "*S-1-5-18:(OI)(CI)F"        # NT AUTHORITY\SYSTEM
)

foreach ($g in $grants) {
    if ($PSCmdlet.ShouldProcess($Path, "Grant $g")) {
        & icacls.exe "$Path" /grant:r "$g" | Out-Null
    }
}

# 5. Explicitly remove broad principals in case any survived (defense in depth).
$removals = @(
    '*S-1-5-32-545',  # BUILTIN\Users
    '*S-1-5-11',      # NT AUTHORITY\Authenticated Users
    '*S-1-1-0'        # Everyone
)
foreach ($r in $removals) {
    if ($PSCmdlet.ShouldProcess($Path, "Remove $r")) {
        # /remove may report "None ... were successfully removed" if absent — that's fine.
        & icacls.exe "$Path" /remove "$r" /T /C /Q | Out-Null
    }
}

# 6. Propagate the explicit ACEs to the existing tree.
if ($PSCmdlet.ShouldProcess($Path, "Apply ACLs recursively")) {
    & icacls.exe "$Path" /grant:r "$ServiceAccount:(OI)(CI)F" /T /C /Q | Out-Null
}

Write-Host "`nDone. Effective permissions:" -ForegroundColor Green
& icacls.exe "$Path"

Write-Host "`nReminder: this lock is only enforceable if the TidalSql engine runs as '$ServiceAccount'" -ForegroundColor Yellow
Write-Host "and interactive users are NOT local administrators. Local admins can always bypass NTFS ACLs." -ForegroundColor Yellow
