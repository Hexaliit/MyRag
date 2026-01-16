# Upload blog markdown files to LucidRAG via curl
param(
    [int]$BatchSize = 100,
    [int]$Skip = 0,
    [int]$Parallel = 5
)

$sourceFolder = "C:\Blog\mostlylucidweb\Mostlylucid\Markdown"
$apiUrl = "http://localhost:5019/api/documents/upload"

# Get markdown files
$files = Get-ChildItem -Path $sourceFolder -Filter "*.md" -File |
    Select-Object -Skip $Skip -First $BatchSize

Write-Host "Uploading $($files.Count) files (skipping $Skip)..." -ForegroundColor Cyan

$successCount = 0
$errorCount = 0
$duplicateCount = 0
$processed = 0

foreach ($file in $files) {
    try {
        # Use curl for multipart upload
        $result = & curl.exe -s -X POST $apiUrl `
            -F "file=@$($file.FullName);filename=$($file.Name)" `
            -H "Accept: application/json" 2>&1

        $response = $result | ConvertFrom-Json -ErrorAction SilentlyContinue

        if ($response.status -eq "duplicate" -or $response.status -eq "exists") {
            $duplicateCount++
        } elseif ($response.documentId) {
            $successCount++
        } else {
            $errorCount++
            Write-Host "Unexpected response for $($file.Name): $result" -ForegroundColor Yellow
        }

        $processed++
        if ($processed % 20 -eq 0) {
            Write-Host "Progress: $processed/$($files.Count) - $successCount new, $duplicateCount existing..." -ForegroundColor Gray
        }
    } catch {
        $errorCount++
        Write-Host "Error uploading $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "`nDone!" -ForegroundColor Green
Write-Host "  New uploads: $successCount"
Write-Host "  Already exists: $duplicateCount"
Write-Host "  Errors: $errorCount"
