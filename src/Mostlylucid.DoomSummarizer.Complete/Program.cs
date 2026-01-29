// DoomSummarizer.Complete — Meta project. All source plugins statically linked.
// Config file enables/disables individual plugins via the "plugins" section.

// Anchor plugin assemblies so the linker doesn't trim them.
_ = typeof(DoomSummarizer.Sources.HackerNews.HackerNewsSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Reddit.RedditSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Google.GoogleSourcePlugin);
_ = typeof(DoomSummarizer.Sources.News.NewsSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Web.WebSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Academic.AcademicSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Science.ScienceSourcePlugin);
_ = typeof(DoomSummarizer.Sources.Reference.ReferenceSourcePlugin);

return await DoomSummarizer.CliApp.RunAsync(args);
