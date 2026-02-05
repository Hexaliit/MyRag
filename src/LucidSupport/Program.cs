using LucidSupport.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("lucidsupport");

    config.AddCommand<LearnCommand>("learn")
        .WithDescription("Learn a web page and output a .support.md file")
        .WithExample("learn", "https://example.com/checkout");

    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("Ingest a .support.md file into the RAG corpus")
        .WithExample("ingest", "checkout-payment.support.md");

    config.AddCommand<ServeCommand>("serve")
        .WithDescription("Start the demo server with widget and API endpoints")
        .WithExample("serve", "--port", "5050");
});

return await app.RunAsync(args);
