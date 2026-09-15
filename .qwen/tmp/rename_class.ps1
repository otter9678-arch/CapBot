# Renames class CapBot (CapBot.AI) -> CaptainBot to resolve namespace/class collision.
$ErrorActionPreference = 'Stop'
$root = 'D:\Creating Mods\CapBot'

$files = @(
    'AI\CapBot.cs','AI\AIRegistry.cs','AI\AIUtils.cs','AI\CapBotAIController.cs',
    'Leveling\LevelingSystem.cs','Leveling\XPEvents.cs',
    'Personality\PersonalityManager.cs',
    'Dialogue\DialogueManager.cs','Dialogue\DialogueTriggers.cs',
    'Loadouts\LoadoutManager.cs',
    'SaveLoad\SaveLoadManager.cs',
    'Networking\NetworkState.cs','Networking\NetworkSyncManager.cs',
    'Talents\TalentEffects.cs',
    'UI\CommandWheelManager.cs',
    'UI\IMGUI\IMGUI_DebugConsole.cs','UI\IMGUI\IMGUI_TalentTree.cs',
    'UI\IMGUI\IMGUI_CrewManagement.cs','UI\IMGUI\IMGUI_BehaviorEditor.cs',
    'UI\IMGUI\IMGUI_ScriptingConsole.cs'
)

$pairs = @(
    @{ old = 'public class CapBot'; new = 'public class CaptainBot' },
    @{ old = 'public CapBot(PLPlayer player)'; new = 'public CaptainBot(PLPlayer player)' },
    @{ old = 'public static CapBot Get('; new = 'public static CaptainBot Get(' },
    @{ old = 'public static CapBot GetLocalBot()'; new = 'public static CaptainBot GetLocalBot()' },
    @{ old = 'new CapBot('; new = 'new CaptainBot(' },
    @{ old = 'Dictionary<int, CapBot>'; new = 'Dictionary<int, CaptainBot>' },
    @{ old = 'CapBot bot'; new = 'CaptainBot bot' }
)

$changed = 0
foreach ($rel in $files) {
    $path = Join-Path $root $rel
    if (-not (Test-Path $path)) { Write-Output "MISSING: $rel"; continue }
    $t = [IO.File]::ReadAllText($path)
    $orig = $t
    foreach ($p in $pairs) { $t = $t.Replace($p.old, $p.new) }
    if ($t -ne $orig) {
        [IO.File]::WriteAllText($path, $t, (New-Object System.Text.UTF8Encoding($true)))
        $changed++
        Write-Output "RENAMED: $rel"
    }
}
Write-Output "FILES CHANGED: $changed"