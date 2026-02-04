// Shared analysis types from Summarizer.Core.
// Signal is NOT imported here — ImageSummarizer defines its own Signal subclass in Models/Dynamic/Signal.cs.
// AggregationStrategy and SignalTags are imported directly so 34+ wave files can use them
// without adding explicit using directives for Mostlylucid.Summarizer.Core.Analysis.

global using AggregationStrategy = Mostlylucid.Summarizer.Core.Analysis.AggregationStrategy;
global using SignalTags = Mostlylucid.Summarizer.Core.Analysis.SignalTags;