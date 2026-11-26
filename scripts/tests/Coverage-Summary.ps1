# Sums up a code coverage report from dotnet test (see coverage.runsettings)
# as Markdown: line and branch coverage per assembly, and the files with the
# most lines that no test runs. CI adds it to the test run's summary page.
#
#   -Path <file or folder>   a coverage.cobertura.xml, or a folder holding
#                            them (the newest is read). By default the
#                            TestResults folders in the repository.
#   -Top 15                  how many files to list

param(
    [string]$Path = "",
    [int]$Top = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

if ($Path) {
    $searched = @($Path)
} else {
    # dotnet test writes to TestResults in each test project's folder, or to
    # the folder given by --results-directory (TestResults at the root, on CI).
    $searched = @(Join-Path $repoRoot "TestResults") +
        @(Get-ChildItem -Path $repoRoot -Directory | ForEach-Object { Join-Path $_.FullName "TestResults" })
}
$report = $searched | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
    if (Test-Path -LiteralPath $_ -PathType Leaf) { Get-Item -LiteralPath $_ }
    else { Get-ChildItem -LiteralPath $_ -Filter "coverage.cobertura.xml" -Recurse -File }
} | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $report) {
    throw "No coverage.cobertura.xml found. Run: dotnet test .\OpenCfDrive.slnx --settings coverage.runsettings"
}

$xml = New-Object System.Xml.XmlDocument
$xml.Load($report.FullName)
$sources = @($xml.SelectNodes("/coverage/sources/source") | ForEach-Object { $_.InnerText })

# A file's path as the report shows it: from the repository's root when the
# file is in it (or was, on the machine that made the report).
function Get-ShownPath([string]$fileName) {
    $full = $fileName
    if (-not [System.IO.Path]::IsPathRooted($full) -and $sources.Count -gt 0) {
        $full = Join-Path $sources[0] $fileName
    }
    foreach ($root in @($repoRoot) + $sources) {
        $prefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if ($full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $full = $full.Substring($prefix.Length)
            break
        }
    }
    $full.Replace('\', '/')
}

# A line can be listed under more than one class (a partial class, or code
# the compiler moved into a class of its own), so lines are counted once per
# file: run if any of its listings ran.
$files = @{}
foreach ($package in $xml.SelectNodes("/coverage/packages/package")) {
    $assembly = $package.GetAttribute("name")
    foreach ($class in $package.SelectNodes("classes/class")) {
        $shown = Get-ShownPath $class.GetAttribute("filename")
        if (-not $files.ContainsKey($shown)) {
            $files[$shown] = [pscustomobject]@{ Assembly = $assembly; Lines = @{}; Branches = @{} }
        }
        $file = $files[$shown]
        foreach ($line in $class.SelectNodes("lines/line")) {
            $number = [int]$line.GetAttribute("number")
            $ran = [long]$line.GetAttribute("hits") -gt 0
            $file.Lines[$number] = $ran -or ($file.Lines.ContainsKey($number) -and $file.Lines[$number])
            # condition-coverage reads like "50% (1/2)": branches taken, of all.
            if ($line.GetAttribute("branch") -eq "true" -and
                $line.GetAttribute("condition-coverage") -match '\((\d+)/(\d+)\)') {
                $taken = [int]$Matches[1]
                if (-not $file.Branches.ContainsKey($number) -or $file.Branches[$number][0] -lt $taken) {
                    $file.Branches[$number] = @($taken, [int]$Matches[2])
                }
            }
        }
    }
}

$rows = foreach ($name in $files.Keys) {
    $file = $files[$name]
    $ranLines = @($file.Lines.Values | Where-Object { $_ }).Count
    $branches = @($file.Branches.Values)
    [pscustomobject]@{
        File          = $name
        Assembly      = $file.Assembly
        Lines         = $file.Lines.Count
        LinesRun      = $ranLines
        Branches      = [int]($branches | ForEach-Object { $_[1] } | Measure-Object -Sum).Sum
        BranchesTaken = [int]($branches | ForEach-Object { $_[0] } | Measure-Object -Sum).Sum
    }
}

function Format-Rate([double]$part, [double]$whole) {
    if ($whole -eq 0) { return "-" }
    [string]::Format($invariant, "{0:0.0}% ({1:#,0} of {2:#,0})", 100.0 * $part / $whole, $part, $whole)
}

function Format-Totals([string]$label, $group) {
    $lines = ($group | Measure-Object -Property Lines -Sum).Sum
    $linesRun = ($group | Measure-Object -Property LinesRun -Sum).Sum
    $branches = ($group | Measure-Object -Property Branches -Sum).Sum
    $taken = ($group | Measure-Object -Property BranchesTaken -Sum).Sum
    "| $label | $(Format-Rate $linesRun $lines) | $(Format-Rate $taken $branches) |"
}

"## Code coverage"
""
"Unit and contract tests (``dotnet test``). The end-to-end suite isn't counted."
""
"| Assembly | Lines run | Branches taken |"
"|---|---:|---:|"
foreach ($group in $rows | Group-Object Assembly | Sort-Object Name) {
    Format-Totals $group.Name $group.Group
}
Format-Totals "**All**" $rows
""
"<details><summary>The $Top files with the most lines that no test runs</summary>"
""
"| File | Lines not run | Lines run |"
"|---|---:|---:|"
$rows | Sort-Object @{ Expression = { $_.Lines - $_.LinesRun }; Descending = $true }, File |
    Select-Object -First $Top | ForEach-Object {
        "| ``$($_.File)`` | $($_.Lines - $_.LinesRun) | $(Format-Rate $_.LinesRun $_.Lines) |"
    }
""
"</details>"
