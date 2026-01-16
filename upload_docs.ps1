$files = Get-ChildItem 'C:\Blog\mostlylucidweb\Mostlylucid\Markdown\*.md'
$total = $files.Count
$count = 0
$success = 0
$failed = 0

Write-Host "Starting upload of $total markdown files..."

foreach ($file in $files) {
    $count++
    try {
        # Use curl.exe (built into Windows) for multipart form upload
        $result = & curl.exe -k -s -X POST "https://localhost:5020/api/documents/upload" -F "file=@$($file.FullName)"
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[$count/$total] Uploaded: $($file.Name)"
            $success++
        } else {
            Write-Host "[$count/$total] FAILED: $($file.Name) - curl exit code $LASTEXITCODE"
            $failed++
        }
    } catch {
        Write-Host "[$count/$total] FAILED: $($file.Name) - $($_.Exception.Message)"
        $failed++
    }
}
Write-Host ""
Write-Host "Done! Uploaded: $success, Failed: $failed"
