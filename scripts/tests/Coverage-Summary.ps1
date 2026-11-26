# Sums up code coverage reports (cobertura XML) as Markdown: the lines run per
# assembly, and the files with the most lines that no test runs. CI adds it to
# the test run's summary page. The unit and contract tests (dotnet test with
# coverage.runsettings) and the end-to-end suite (E2E-Run.ps1 -Coverage) write
# such reports; see docs/testing.md.
#
# Several reports are combined, file by file and line by line: a line counts
# as run if any of them ran it.
#
#   -Path <paths>   coverage.cobertura.xml files, or folders holding them (the
#                   newest in each folder is read). Wildcards are allowed.
#                   "Name=<path>" puts reports in a named group: with two
#                   groups or more, each gets a column beside the combined one.
#                   By default the TestResults folders in the repository.
#   -Title "..."    the heading
#   -Note "..."     a line under it
#   -Top 15         how many files to list

param(
    [string[]]$Path = @(),
    [string]$Title = "Code coverage",
    [string]$Note = "",
    [int]$Top = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

# The newest report among these files and folders.
function Find-Report([string[]]$Places) {
    $Places | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        if (Test-Path -LiteralPath $_ -PathType Leaf) { Get-Item -LiteralPath $_ }
        else { Get-ChildItem -LiteralPath $_ -Filter "coverage.cobertura.xml" -Recurse -File }
    } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

$reports = @()
$missing = @()
if ($Path.Count -eq 0) {
    # dotnet test writes to TestResults in each test project's folder, or to
    # the folder given by --results-directory (TestResults at the root, on CI).
    $searched = @(Join-Path $repoRoot "TestResults") +
        @(Get-ChildItem -Path $repoRoot -Directory | ForEach-Object { Join-Path $_.FullName "TestResults" })
    $report = Find-Report $searched
    if ($report) { $reports += [pscustomobject]@{ Group = ""; File = $report.FullName } }
} else {
    foreach ($entry in $Path) {
        $group = ""
        if ($entry -match '^([^=\\/:*?]+)=(.+)$') {
            $group = $Matches[1]
            $entry = $Matches[2]
        }
        $places = @(Resolve-Path -Path $entry -ErrorAction SilentlyContinue | ForEach-Object { $_.ProviderPath })
        if ($places.Count -eq 0) { $missing += $entry }
        foreach ($place in $places) {
            $report = Find-Report @($place)
            if ($report) { $reports += [pscustomobject]@{ Group = $group; File = $report.FullName } }
            else { $missing += Split-Path -Leaf $place }
        }
    }
}
if ($reports.Count -eq 0) {
    throw "No coverage.cobertura.xml found. Run: dotnet test .\OpenCfDrive.slnx --settings coverage.runsettings"
}
$groups = @($reports | ForEach-Object { $_.Group } | Select-Object -Unique)
$byGroup = $groups.Count -ge 2 -and @($groups | Where-Object { -not $_ }).Count -eq 0

# A file's path as the report shows it: from the repository's root when the
# file is in it (or was, on the machine that made the report).
function Get-ShownPath([string]$fileName, [string[]]$roots) {
    $full = $fileName
    if (-not [System.IO.Path]::IsPathRooted($full) -and $roots.Count -gt 0) {
        $full = Join-Path $roots[0] $fileName
    }
    foreach ($root in @($repoRoot) + $roots) {
        $prefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if ($full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $full = $full.Substring($prefix.Length)
            break
        }
    }
    $full.Replace('\', '/')
}

# A line can be listed under more than one class (a partial class, or code
# the compiler moved into a class of its own), and in more than one report,
# so lines are counted once per file: run if any of its listings ran.
# Branches are shown only if every report has them: coverlet's do, those of
# Microsoft's tool (the end-to-end suite's) don't.
$files = @{}
$allHaveBranches = $true
foreach ($report in $reports) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($report.File)
    $roots = @($xml.SelectNodes("/coverage/sources/source") | ForEach-Object { $_.InnerText })
    if ($null -eq $xml.SelectSingleNode("//line[@branch='true' or @branch='True']")) { $allHaveBranches = $false }
    foreach ($package in $xml.SelectNodes("/coverage/packages/package")) {
        $assembly = $package.GetAttribute("name")
        foreach ($class in $package.SelectNodes("classes/class")) {
            $shown = Get-ShownPath $class.GetAttribute("filename") $roots
            if (-not $files.ContainsKey($shown)) {
                $files[$shown] = [pscustomobject]@{ Assembly = $assembly; Lines = @{}; ByGroup = @{}; Branches = @{} }
            }
            $file = $files[$shown]
            if (-not $file.ByGroup.ContainsKey($report.Group)) { $file.ByGroup[$report.Group] = @{} }
            $groupLines = $file.ByGroup[$report.Group]
            foreach ($line in $class.SelectNodes("lines/line")) {
                $number = [int]$line.GetAttribute("number")
                $ran = [long]$line.GetAttribute("hits") -gt 0
                $file.Lines[$number] = $ran -or ($file.Lines.ContainsKey($number) -and $file.Lines[$number])
                $groupLines[$number] = $ran -or ($groupLines.ContainsKey($number) -and $groupLines[$number])
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
}

$rows = foreach ($name in $files.Keys) {
    $file = $files[$name]
    $branches = @($file.Branches.Values)
    $runBy = @{}
    foreach ($group in $groups) {
        $runBy[$group] = 0
        if ($file.ByGroup.ContainsKey($group)) { $runBy[$group] = @($file.ByGroup[$group].Values | Where-Object { $_ }).Count }
    }
    [pscustomobject]@{
        File          = $name
        Assembly      = $file.Assembly
        Lines         = $file.Lines.Count
        LinesRun      = @($file.Lines.Values | Where-Object { $_ }).Count
        RunBy         = $runBy
        Branches      = [int]($branches | ForEach-Object { $_[1] } | Measure-Object -Sum).Sum
        BranchesTaken = [int]($branches | ForEach-Object { $_[0] } | Measure-Object -Sum).Sum
    }
}

function Format-Rate([double]$part, [double]$whole, [switch]$Short) {
    if ($whole -eq 0) { return "-" }
    if ($Short) { return [string]::Format($invariant, "{0:0.0}%", 100.0 * $part / $whole) }
    [string]::Format($invariant, "{0:0.0}% ({1:#,0} of {2:#,0})", 100.0 * $part / $whole, $part, $whole)
}

function Get-Sum($set, [scriptblock]$value) {
    [double]($set | ForEach-Object $value | Measure-Object -Sum).Sum
}

# A row's cells after its name, for a set of files: the lines each group ran
# (with groups), the lines all of them ran, and the branches taken. Every
# column counts the same lines, those of all the reports.
$headers = @(if ($byGroup) { $groups; "All" } else { "Lines run" }) + @(if ($allHaveBranches) { "Branches taken" })
function Get-Cells($set, [switch]$NoBranches) {
    $lines = Get-Sum $set { $_.Lines }
    if ($byGroup) {
        foreach ($group in $groups) { Format-Rate (Get-Sum $set { $_.RunBy[$group] }) $lines -Short }
    }
    Format-Rate (Get-Sum $set { $_.LinesRun }) $lines
    if ($allHaveBranches -and -not $NoBranches) {
        Format-Rate (Get-Sum $set { $_.BranchesTaken }) (Get-Sum $set { $_.Branches })
    }
}

"## $Title"
""
if ($Note) {
    $Note
    ""
}
if ($reports.Count -gt 1) {
    $from = "$($reports.Count) reports"
    if ($byGroup) {
        $from += " (" + (($groups | ForEach-Object {
            $group = $_
            "$group $(@($reports | Where-Object { $_.Group -eq $group }).Count)"
        }) -join ", ") + ")"
    }
    $sentence = "Combined from ${from}: a line counts as run if any of them ran it."
    if ($byGroup) { $sentence += " Every column counts the same lines, those of all the reports." }
    if (-not $allHaveBranches) { $sentence += " Not all of them count branches, so only lines are shown." }
    $sentence
    ""
}
if ($missing.Count -gt 0) {
    $names = $missing | ForEach-Object { '`' + $_ + '`' }
    "No report in: $($names -join ', ')."
    ""
}
"| Assembly | " + ($headers -join " | ") + " |"
"|---|" + (($headers | ForEach-Object { "---:" }) -join "|") + "|"
foreach ($assembly in $rows | Group-Object Assembly | Sort-Object Name) {
    "| $($assembly.Name) | " + ((Get-Cells $assembly.Group) -join " | ") + " |"
}
"| **All** | " + ((Get-Cells $rows) -join " | ") + " |"
""
"<details><summary>The $Top files with the most lines that no test runs</summary>"
""
$fileHeaders = @("Lines not run") + @($headers | Where-Object { $_ -ne "Branches taken" })
"| File | " + ($fileHeaders -join " | ") + " |"
"|---|" + (($fileHeaders | ForEach-Object { "---:" }) -join "|") + "|"
$rows | Sort-Object @{ Expression = { $_.Lines - $_.LinesRun }; Descending = $true }, File |
    Select-Object -First $Top | ForEach-Object {
        "| ``$($_.File)`` | $($_.Lines - $_.LinesRun) | " + ((Get-Cells @($_) -NoBranches) -join " | ") + " |"
    }
""
"</details>"
