param(
    [string]$KBPath = "C:\KBs\academicoLocal",
    [string]$OutputPath = "C:\Projetos\GenexusMCP\publish\kb_inventory.json",
    [string]$GxPath = $env:GX_PATH
)

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\gx-version-catalog.ps1')
$gxCatalog = Get-GxVersionCatalog -Root $root
if ([string]::IsNullOrWhiteSpace($GxPath)) { $GxPath = Get-GxPrimaryInstallPath -Catalog $gxCatalog }
$gxPath = $GxPath
[Reflection.Assembly]::LoadFrom((Join-Path $gxPath "Artech.Common.Helpers.dll")) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $gxPath "Artech.Architecture.Common.dll")) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $gxPath "Artech.Genexus.Common.dll")) | Out-Null

try {
    Write-Host "Opening KB: $KBPath"
    $options = New-Object Artech.Architecture.Common.Objects.KnowledgeBase+OpenOptions -ArgumentList $KBPath
    $kb = [Artech.Architecture.Common.Objects.KnowledgeBase]::Open($options)
    $model = $kb.DesignModel

    Write-Host "Inventoring Objects..."
    $results = @()
    $count = 0
    
    # We use the universal iterator that we know works in PowerShell
    foreach ($obj in $model.Objects) {
        $results += @{
            Guid = $obj.Guid.ToString()
            Name = $obj.Name
            Type = $obj.TypeDescriptor.Name
            Description = $obj.Description
        }
        $count++
        if ($count % 500 -eq 0) { 
            Write-Host "Mapped $count objects..." 
            $model.ClearCache()
        }
    }

    $results | ConvertTo-Json | Out-File $OutputPath -Encoding utf8
    Write-Host "Inventory complete: $count objects saved to $OutputPath"
    
    $kb.Close()
} catch {
    Write-Error "Inventory failed: $($_.Exception.Message)"
}
