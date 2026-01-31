// DoomSummarizer.Complete — Meta project. All plugins and readers statically linked.
// Config file enables/disables individual plugins via the "plugins" section.

// Force-load plugin assemblies so PluginDiscovery can scan them.
// Using an array consumed by RuntimeHelpers.RunClassConstructor ensures the JIT
// can't optimize these away (unlike discarded _ = typeof(...) which can be elided).
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.HackerNews.HackerNewsSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.Reddit.RedditSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.Google.GoogleSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.News.NewsSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.Web.WebSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.Academic.AcademicSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.Science.ScienceSourcePlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(DoomSummarizer.Sources.Reference.ReferenceSourcePlugin).TypeHandle);

// Anchor reader assemblies.
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.Summarizers.Reader.Pdf.PdfReader).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.Summarizers.Reader.Docx.DocxReader).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.Summarizers.Reader.Gutenberg.GutenbergReader).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.Summarizers.Reader.Markdown.MarkdownReader).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.Summarizers.Reader.Docling.DoclingReader).TypeHandle);

// Anchor processor plugins (one per plugin ensures assembly loaded for discovery).
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.DoomSummarizer.Plugin.Books.BookProcessorPlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.DoomSummarizer.Plugin.Video.VideoProcessorPlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.DoomSummarizer.Plugin.Image.ImageProcessorPlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.DoomSummarizer.Plugin.Audio.AudioProcessorPlugin).TypeHandle);
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
    typeof(Mostlylucid.DoomSummarizer.Plugin.Data.DataProcessorPlugin).TypeHandle);

return await DoomSummarizer.CliApp.RunAsync(args);
