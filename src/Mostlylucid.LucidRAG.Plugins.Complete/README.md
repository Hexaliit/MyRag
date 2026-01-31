# Mostlylucid.LucidRAG.Plugins.Complete

Meta-package that bundles all DoomSummarizer processor plugins. Install this
single package for the full plugin experience.

## Included Plugins

| Plugin | Extensions | CLI Commands |
|--------|-----------|--------------|
| Books | .pdf, .docx, .txt, .md, .zip | `books split`, `books detect` |
| Video | .mp4, .mkv, .avi, .webm, .mov, .wmv | `video analyze`, `video shots`, `video scenes` |
| Image | .png, .jpg, .jpeg, .gif, .webp, .bmp, .tiff | `image analyze`, `image ocr`, `image caption` |
| Audio | .mp3, .wav, .flac, .ogg, .m4a, .opus | `audio transcribe`, `audio speakers` |
| Data | .csv, .xlsx, .xls, .parquet, .json, .jsonl, .tsv | `data profile`, `data schema` |

## Usage

```csharp
// Register all plugins at once
var registry = new ProcessorPluginRegistry();
registry.AddAllPlugins();
```
