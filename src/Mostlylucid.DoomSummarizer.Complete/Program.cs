// DoomSummarizer.Complete — Meta project. All plugins and readers statically linked.
// Config file enables/disables individual plugins via the "plugins" section.

// Force-load plugin assemblies so PluginDiscovery can scan them.
// Using an array consumed by RuntimeHelpers.RunClassConstructor ensures the JIT
// can't optimize these away (unlike discarded _ = typeof(...) which can be elided).

using System.Runtime.CompilerServices;
using DoomSummarizer;
using DoomSummarizer.Sources.Academic;
using DoomSummarizer.Sources.Google;
using DoomSummarizer.Sources.HackerNews;
using DoomSummarizer.Sources.News;
using DoomSummarizer.Sources.Reddit;
using DoomSummarizer.Sources.Reference;
using DoomSummarizer.Sources.Science;
using DoomSummarizer.Sources.Web;
using Mostlylucid.DoomSummarizer.Plugin.Audio;
using Mostlylucid.DoomSummarizer.Plugin.Books;
using Mostlylucid.DoomSummarizer.Plugin.Data;
using Mostlylucid.DoomSummarizer.Plugin.Image;
using Mostlylucid.DoomSummarizer.Plugin.Video;
using Mostlylucid.Summarizers.Reader.Docling;
using Mostlylucid.Summarizers.Reader.Docx;
using Mostlylucid.Summarizers.Reader.Gutenberg;
using Mostlylucid.Summarizers.Reader.Markdown;
using Mostlylucid.Summarizers.Reader.Pdf;

RuntimeHelpers.RunClassConstructor(
    typeof(HackerNewsSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(RedditSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(GoogleSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(NewsSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(WebSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(AcademicSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(ScienceSourcePlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(ReferenceSourcePlugin).TypeHandle);

// Anchor reader assemblies.
RuntimeHelpers.RunClassConstructor(
    typeof(PdfReader).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(DocxReader).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(GutenbergReader).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(MarkdownReader).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(DoclingReader).TypeHandle);

// Anchor processor plugins (one per plugin ensures assembly loaded for discovery).
RuntimeHelpers.RunClassConstructor(
    typeof(BookProcessorPlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(VideoProcessorPlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(ImageProcessorPlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(AudioProcessorPlugin).TypeHandle);
RuntimeHelpers.RunClassConstructor(
    typeof(DataProcessorPlugin).TypeHandle);

return await CliApp.RunAsync(args);