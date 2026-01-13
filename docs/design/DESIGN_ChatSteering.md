# Chat Steering (Design)

## Summary
Chat steering allows the user to adjust retrieval scope and ranking during an ongoing conversation without changing the core topic. Examples:
- "No, use older documents."
- "Let's focus on documents older than 2021-01-01."
- "Focus only on the X document."
- "Exclude the audit report and include the incident postmortem instead."

The system uses the Sentinel with prior document context to generate a new content query plus retrieval constraints. The final synthesis prompt must explicitly account for the steering instruction so the answer reflects the new scope.

## Goals
- Support steering directives that adjust retrieval (document scope and time bias) while keeping conversation context.
- Use Sentinel to interpret steering and produce an effective content query.
- Ensure the response prompt includes the steering instruction and the user message.
- Preserve transparency (return steering metadata for UI/debug).

## Non-goals
- Full UI work. This is a backend behavior spec.
- Multi-step agent planning beyond query decomposition and retrieval tuning.
- Automatic document ingestion changes.

## User experience examples
1) Steering only:
- User: "No, use older documents."
- System: re-run the last topic query with a bias toward older content.

2) Steering + question:
- User: "Focus on the design doc and explain the tradeoffs."
- System: constrain to the doc named by the user and answer the question.

3) Scope override:
- User: "Let's just use the X document."
- System: filter to that document; if ambiguous, ask a clarification question.

4) Include/exclude swap:
- User: "Exclude the audit report and include the incident postmortem instead."
- System: remove audit report, include postmortem, then answer the question.

5) Date constraint:
- User: "Let's focus on documents older than 2021-01-01."
- System: re-run the last topic query constrained to documents before the date.

## Detection and routing
Add a steering detection stage before follow-up detection.

Pipeline for each message:
1) If new conversation: normal search.
2) Else:
   a) Run steering detection (Sentinel) with prior context.
   b) If steering detected, build effective query and retrieval constraints.
   c) Else, fall back to existing follow-up detection.

## Policy and precedence
Constraint precedence (highest to lowest):
1) Explicit user constraints in the current message
2) Sticky steering state
3) Active document scope
4) Collection-wide defaults

## Confidence and fallback
- Use a configurable confidence threshold (example: >= 0.7) for steering acceptance.
- If below threshold, either request clarification or fall back to follow-up detection.

## Sentinel steering interpretation
Introduce a new Sentinel method:
- DetectSteeringAsync(query, previousQuery, activeDocs, recentSources, history)

Inputs (minimal):
- User query
- Previous topic query (LastTopicQuery)
- Active document IDs
- Active document metadata (names, dates)
- Recent conversation snippets (optional)

Output (SteeringPlan):
- IsSteering: bool
- Confidence: 0.0 to 1.0
- Reason: string
- SteeringEffect: "retrieval" | "synthesis" | "both"
- EffectiveQuery: string (rewritten content query)
- Scope:
  - DocumentIdsInclude: Guid[]?
  - DocumentIdsExclude: Guid[]?
  - ScopeMode: "active" | "collection"
- TimeBias:
  - "older" | "newer" | "none"
  - Optional time window hints (before/after)
- TimeConstraint:
  - BeforeDate: ISO-8601 string?
  - AfterDate: ISO-8601 string?
  - Reference: "explicit" | "document" | "collection"
- Sticky: bool ("from now on" style)
- SteeringSummary: short natural language summary
- NeedsClarification: bool
- ClarificationQuestion: string?
- AmbiguousDocuments: string[]? (document name candidates)

### Steering prompt shape
The prompt should:
- Explain the available active documents (name + date).
- Provide the previous topic query.
- Ask for a JSON steering plan that preserves the content intent unless the user asks a new question.

## Retrieval changes
Steering adjusts retrieval before synthesis:
- Document scope:
  - Include list -> filter to those docs.
  - Exclude list -> remove those docs from candidates.
  - ScopeMode "active" -> limit to active set.
- Time bias:
  - For "older" -> invert or down-weight freshness boost in RRF.
  - For "newer" -> increase freshness weight (current behavior is a mild boost).
- Time constraint:
  - Apply CreatedAt filter using BeforeDate/AfterDate when provided.
- SteeringEffect:
  - "synthesis" -> no retrieval change; adjust response style only.

Implementation note:
- In RRF, swap the freshness ordering for older bias or apply a negative weight.

## Conversation state
- Reuse existing fields:
  - ActiveDocumentIds
  - LastTopicQuery
- If Sticky steering is enabled, store a small steering state in conversation metadata.
- Sticky steering persists until explicitly overridden or the topic query changes.

## Prompting and synthesis
The synthesis prompt must incorporate steering explicitly:
- Prepend a "Steering instructions" block.
- Include the user message verbatim.
- Use EffectiveQuery for retrieval, but answer the user message.

Example prompt addition:
"Steering instructions: Focus only on doc 'X' and prefer older sources."

## API and DTO changes
- ChatRequest:
  - Optional SteeringHint or a field to request steering interpretation.
- ChatResponse:
  - Steering metadata (summary, constraints applied, effective query).

## Observability
Add counters and logs:
- steering.detected
- steering.confidence
- steering.constraints_applied
- steering.time_bias

## Edge cases
- Steering directive references a doc not in active set.
  - Ask a clarification question or fall back to collection-wide search.
- Steering only message with no prior topic.
  - Ask for a topic or run a normal search with the message as a query.

## Testing
- Unit tests for steering detection logic.
- Integration tests for:
  - Document scope filter (include/exclude).
  - Older vs newer ranking behavior.
  - Prompt includes steering block.

## Rollout
- Feature flag in config (default off).
- Enable in dev first, then prod.
