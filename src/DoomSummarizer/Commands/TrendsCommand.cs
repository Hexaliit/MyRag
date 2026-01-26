using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class TrendsCommand : AsyncCommand<TrendsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--days")]
        [Description("Number of days to analyze")]
        [DefaultValue(7)]
        public int Days { get; init; } = 7;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        await using var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        var trends = await storage.GetTrendAnalysisAsync(settings.Days);

        AnsiConsole.Write(new Rule($"[bold cyan]Doom Scroll Trends - Last {settings.Days} Days[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Overview stats
        var statsTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        statsTable.AddRow("Total Items", trends.TotalItems.ToString());
        statsTable.AddRow("Average Sentiment", FormatSentiment(trends.AverageSentiment));
        statsTable.AddRow("Sentiment Change", FormatSentimentChange(trends.SentimentChange));
        statsTable.AddRow("Period", $"{trends.StartDate:MMM dd} - {trends.EndDate:MMM dd}");

        AnsiConsole.Write(statsTable);
        AnsiConsole.WriteLine();

        // Topic breakdown
        if (trends.TopTopics.Count > 0)
        {
            AnsiConsole.Write(new Rule("[bold]Top Topics[/]").RuleStyle("grey").LeftJustified());

            var topicChart = new BarChart()
                .Width(60)
                .Label("[green bold]Posts by Topic[/]")
                .CenterLabel();

            foreach (var topic in trends.TopTopics.Take(8))
            {
                var color = topic.AverageSentiment switch
                {
                    > 0.3f => Color.Green,
                    < -0.3f => Color.Red,
                    _ => Color.Yellow
                };
                topicChart.AddItem(topic.Topic, topic.Count, color);
            }

            AnsiConsole.Write(topicChart);
            AnsiConsole.WriteLine();

            // Detailed topic table
            var topicTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Topic")
                .AddColumn("Posts")
                .AddColumn("Avg Sentiment")
                .AddColumn("Vibe");

            foreach (var topic in trends.TopTopics)
            {
                var vibe = topic.AverageSentiment switch
                {
                    > 0.5f => "[green]:)[/]",
                    > 0.2f => "[lime]:)[/]",
                    > -0.2f => "[yellow]:|[/]",
                    > -0.5f => "[orange1]:([/]",
                    _ => "[red]:([/]"
                };

                topicTable.AddRow(
                    topic.Topic,
                    topic.Count.ToString(),
                    FormatSentiment(topic.AverageSentiment),
                    vibe);
            }

            AnsiConsole.Write(topicTable);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]No data found for this period. Run 'doomsummarizer scroll' to fetch some content.[/]");
        }

        AnsiConsole.WriteLine();

        // Sentiment interpretation
        var sentimentMessage = trends.AverageSentiment switch
        {
            > 0.3f => "[green]The tech world is feeling optimistic![/]",
            > 0.1f => "[lime]Generally positive vibes in the community.[/]",
            > -0.1f => "[yellow]Balanced mix of news - typical day.[/]",
            > -0.3f => "[orange1]Some concerns in the community.[/]",
            _ => "[red]Doom levels elevated. Maybe take a break?[/]"
        };

        var changeMessage = trends.SentimentChange switch
        {
            > 0.2f => "[green]^ Mood improving significantly[/]",
            > 0.05f => "[lime]^ Slight improvement in sentiment[/]",
            > -0.05f => "[grey]- Sentiment stable[/]",
            > -0.2f => "[orange1]v Sentiment declining slightly[/]",
            _ => "[red]v Notable drop in sentiment[/]"
        };

        AnsiConsole.Write(new Panel(
            $"{sentimentMessage}\n{changeMessage}")
            .Header("[bold]Sentiment Summary[/]")
            .Border(BoxBorder.Rounded));

        return 0;
    }

    private static string FormatSentiment(float sentiment)
    {
        var color = sentiment switch
        {
            > 0.3f => "green",
            > 0f => "lime",
            > -0.3f => "yellow",
            _ => "red"
        };
        var sign = sentiment >= 0 ? "+" : "";
        return $"[{color}]{sign}{sentiment:F2}[/]";
    }

    private static string FormatSentimentChange(float change)
    {
        var color = change switch
        {
            > 0.1f => "green",
            > 0f => "lime",
            > -0.1f => "yellow",
            _ => "red"
        };
        var arrow = change >= 0 ? "^" : "v";
        var sign = change >= 0 ? "+" : "";
        return $"[{color}]{arrow} {sign}{change:F2}[/]";
    }
}
