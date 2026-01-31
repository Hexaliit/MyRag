// DoomSummarizer.Complete — Meta project. All plugins and readers statically linked.
// Config file enables/disables individual plugins via the "plugins" section.

// Anchor source plugin assemblies so the linker doesn't trim them.
_ = typeof(DoomSummarizer.Sources.HackerNews.HackerNewsSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Reddit.RedditSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Google.GoogleSourcePlugin);
_ = typeof(DoomSummarizer.Sources.News.NewsSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Web.WebSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Academic.AcademicSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Science.ScienceSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Reference.ReferenceSourcePlugin);

// Anchor reader assemblies.
_ = typeof(Mostlylucid.Summarizers.Reader.Pdf.PdfReader);
_ = typeof(Mostlylucid.Summarizers.Reader.Docx.DocxReader);
_ = typeof(Mostlylucid.Summarizers.Reader.Gutenberg.GutenbergReader);
_ = typeof(Mostlylucid.Summarizers.Reader.Markdown.MarkdownReader);
_ = typeof(Mostlylucid.Summarizers.Reader.Docling.DoclingReader);

// Anchor processor plugins.
_ = typeof(Mostlylucid.DoomSummarizer.Plugin.Books.BookProcessorPlugin);

return await DoomSummarizer.CliApp.RunAsync(args);
