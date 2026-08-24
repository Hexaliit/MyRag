try {
    $asm = [System.Reflection.Assembly]::LoadFrom('C:\Users\10686661\.nuget\packages\markdig\1.1.2\lib\net10.0\Markdig.dll')
    Write-Host "Assembly loaded: $($asm.FullName)"
    $types = $asm.GetTypes()
    Write-Host "Total types: $($types.Count)"
    foreach ($t in $types) {
        if ($t.Namespace -like '*Table*') {
            Write-Host $t.FullName
        }
    }
} catch {
    Write-Host "Error: $_"
}
