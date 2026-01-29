// Re-export backend types for test backward compatibility.
global using CircuitBreakerService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteCircuitBreakerService;
global using ApiBudgetService = Mostlylucid.DocSummarizer.Rdbms.Sqlite.SqliteApiBudgetService;
global using ApiBudgetConfig = Mostlylucid.DocSummarizer.Resilience.ApiBudgetConfig;
