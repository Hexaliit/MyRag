// Re-export shared resilience types from DocSummarizer.Core.
// This file preserves the DoomSummarizer.Models namespace for existing code.
global using CircuitStatus = Mostlylucid.DocSummarizer.Resilience.CircuitStatus;
global using CircuitFailureType = Mostlylucid.DocSummarizer.Resilience.CircuitFailureType;
global using CircuitEntry = Mostlylucid.DocSummarizer.Resilience.CircuitEntry;
global using BudgetCheckResult = Mostlylucid.DocSummarizer.Resilience.BudgetCheckResult;
