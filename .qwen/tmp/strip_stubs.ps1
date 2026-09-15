# Strips the appended empty stub class blocks (generation corruption) from CapBot source files.
# Pattern: 5-usings block + "namespace X { internal class Y { } }" at end of file.
$ErrorActionPreference = 'Stop'
$root = 'D:\Creating Mods\CapBot'
$exclude = @('Patch.cs', 'Mod.cs', 'AssemblyInfo.cs', 'NonCaptainMenu.cs')

$stubPattern = '(?m)\r?\n?\s*using System;\s*\r?\n\s*using System\.Collections\.Generic;\s*\r?\n\s*using System\.Linq;\s*\r?\n\s*using System\.Text;\s*\r?\n\s*using System\.Threading\.Tasks;\s*\r?\n\s*\r?\n\s*namespace [\w.]+\s*\r?\n\s*\{\s*\r?\n\s*internal class \w+\s*\r?\n\s*\{\s*\r?\n\s*\}\s*\r?\n\s*\}\s*\r?\n?\s*$'

$files = Get-ChildItem $root -Recurse -Filter *.cs | Where-Object { $exclude -notcontains $_.Name }
$stripped = @()
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    $nt = [regex]::Replace($t, $stubPattern, '')
    if ($nt -ne $t) {
        if ($nt.Trim().Length -eq 0) {
            Write-Output ("EMPTY-RESULT (skipped, needs manual): " + $f.FullName)
        } else {
            [IO.File]::WriteAllText($f.FullName, $nt, (New-Object System.Text.UTF8Encoding($true)))
            $stripped += $f.FullName
        }
    }
}
Write-Output ("STRIPPED COUNT: " + $stripped.Count)
$stripped | ForEach-Object { Write-Output ("  " + $_.Replace($root, '')) }