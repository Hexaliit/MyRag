// Re-export backend types for DoomSummarizer app.
// The concrete implementations moved to Mostlylucid.DocSummarizer.Rdbms.Sqlite.

global using ApiBudgetService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteApiBudgetService;
global using CircuitBreakerService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteCircuitBreakerService;
global using ApiBudgetConfig = Mostlylucid.DocSummarizer.Resilience.ApiBudgetConfig;