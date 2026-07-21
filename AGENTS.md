## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).


## Coding Workflow

For every coding task:

1. Query Graphify first.
2. Identify existing implementation.
3. Identify related files.
4. Find similar implementations.
5. Prefer modifying existing code.
6. Avoid creating duplicate services.
7. Preserve project architecture.
8. Preserve dependency injection.
9. Preserve naming conventions.
10. Preserve folder structure.
11. Generate production-quality code only.

Never generate code before understanding the graph.

## Existing Code Policy

Before writing any new class or function:

- Search for similar implementations.
- Reuse utilities.
- Reuse repositories.
- Reuse services.
- Reuse DTOs.
- Reuse models.

Never duplicate logic that already exists.

If similar code exists,
follow its implementation style.

## Modification Policy

Prefer:

Modify existing code

instead of

Create new files.

Only create new files if the current architecture requires it.

Always explain why a new file is necessary.