param(
    [Parameter(Mandatory = $true)]
    [string]$SetupPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$resolvedPath = (Resolve-Path -LiteralPath $SetupPath).Path
$bytes = [System.IO.File]::ReadAllBytes($resolvedPath)
$decoded = [System.Text.Encoding]::Unicode.GetString($bytes)

function Remove-VersionPadding {
    param(
        [string]$Value,
        [int]$Width
    )

    $padded = $Value.PadRight($Width)
    $index = $decoded.IndexOf($padded, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw "Padded version resource value was not found: $Value"
    }
    if ($decoded.IndexOf($padded, $index + $padded.Length, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Padded version resource value was found more than once: $Value"
    }

    $terminatorIndex = ($index + $Value.Length) * 2
    $bytes[$terminatorIndex] = 0
    $bytes[$terminatorIndex + 1] = 0
}

Remove-VersionPadding -Value 'Milestone Dialer' -Width 60
Remove-VersionPadding -Value $Version -Width 50

$verifyPath = "$resolvedPath.metadata.tmp"
try {
    [System.IO.File]::WriteAllBytes($verifyPath, $bytes)
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($verifyPath)
    if ($info.ProductName -cne 'Milestone Dialer') {
        throw "Normalized ProductName is invalid: [$($info.ProductName)]"
    }
    if ($info.ProductVersion -cne $Version) {
        throw "Normalized ProductVersion is invalid: [$($info.ProductVersion)]"
    }
    [System.IO.File]::Copy($verifyPath, $resolvedPath, $true)
}
finally {
    Remove-Item -LiteralPath $verifyPath -Force -ErrorAction SilentlyContinue
}

Write-Output "Normalized Setup metadata: $Version"
