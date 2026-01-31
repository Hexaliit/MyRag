/**
 * Mermaid parser entry point for Jint.
 * Exposes a single parse(text) function that returns structured data.
 *
 * mermaid.parse() validates syntax and returns a ParseResult with:
 *   - diagramType: the type of diagram detected
 *
 * We augment this with node/edge extraction where possible using
 * mermaid's internal diagram database after parsing.
 */

import mermaid from 'mermaid';

// Initialize mermaid in parse-only mode (no rendering)
mermaid.initialize({
    startOnLoad: false,
    suppressErrors: true,
    logLevel: 'silent'    // suppress console noise in Jint
});

/**
 * Parse mermaid code and return structured data.
 * @param {string} text - Mermaid diagram source code
 * @returns {{ diagramType: string, nodes: string[], edges: string[], description: string }}
 */
function parse(text) {
    if (!text || typeof text !== 'string' || text.trim().length === 0) {
        return {
            diagramType: 'unknown',
            nodes: [],
            edges: [],
            description: 'Empty diagram'
        };
    }

    try {
        // mermaid.parse() validates the syntax and returns the diagram type
        var parseResult = mermaid.parse(text);

        // Detect diagram type from the text if parse doesn't return it
        var diagramType = 'unknown';
        if (parseResult && parseResult.diagramType) {
            diagramType = parseResult.diagramType;
        } else {
            diagramType = detectDiagramType(text);
        }

        // Try to extract nodes and edges from the parsed diagram
        var extracted = extractStructure(text, diagramType);

        return {
            diagramType: diagramType,
            nodes: extracted.nodes || [],
            edges: extracted.edges || [],
            description: buildDescription(diagramType, extracted)
        };
    } catch (parseError) {
        // If mermaid.parse() throws, the syntax is invalid
        // Still try to detect the type and provide what we can
        var fallbackType = detectDiagramType(text);
        return {
            diagramType: fallbackType,
            nodes: [],
            edges: [],
            description: 'Invalid mermaid syntax: ' + (parseError.message || 'parse error')
        };
    }
}

function detectDiagramType(text) {
    var firstLine = text.trim().split('\n')[0].trim().toLowerCase();

    if (firstLine.match(/^(graph|flowchart)\s/)) return 'flowchart';
    if (firstLine.startsWith('sequencediagram')) return 'sequence';
    if (firstLine.startsWith('classdiagram')) return 'class';
    if (firstLine.startsWith('statediagram')) return 'state';
    if (firstLine.startsWith('erdiagram')) return 'er';
    if (firstLine.startsWith('gantt')) return 'gantt';
    if (firstLine.startsWith('pie')) return 'pie';
    if (firstLine.startsWith('journey')) return 'journey';
    if (firstLine.startsWith('gitgraph')) return 'gitgraph';
    if (firstLine.startsWith('mindmap')) return 'mindmap';
    if (firstLine.startsWith('timeline')) return 'timeline';
    if (firstLine.startsWith('quadrantchart')) return 'quadrant';
    if (firstLine.startsWith('xychart')) return 'xychart';
    if (firstLine.startsWith('block')) return 'block';
    if (firstLine.startsWith('sankey')) return 'sankey';
    if (firstLine.startsWith('requirement')) return 'requirement';
    if (firstLine.startsWith('c4')) return 'c4';
    if (firstLine.startsWith('zenuml')) return 'zenuml';

    return 'unknown';
}

function extractStructure(text, diagramType) {
    // Best-effort extraction of nodes/edges from mermaid text
    // This is a lightweight extraction - the main value is syntax validation
    var nodes = [];
    var edges = [];

    try {
        var lines = text.split('\n').slice(1).filter(function(l) { return l.trim().length > 0; });

        switch (diagramType) {
            case 'flowchart':
                extractFlowchart(lines, nodes, edges);
                break;
            case 'sequence':
                extractSequence(lines, nodes, edges);
                break;
            case 'class':
                extractClass(lines, nodes, edges);
                break;
            case 'state':
                extractState(lines, nodes, edges);
                break;
            default:
                // For other types, just count content lines
                break;
        }
    } catch (e) {
        // Best-effort: return what we have
    }

    return { nodes: nodes, edges: edges };
}

function extractFlowchart(lines, nodes, edges) {
    var nodeSet = {};
    lines.forEach(function(line) {
        line = line.trim();
        // Node definitions: A[label], B{label}, C((label))
        var nodeMatch = line.match(/(\w+)\s*[\[\({]/g);
        if (nodeMatch) {
            nodeMatch.forEach(function(m) {
                var id = m.match(/(\w+)/)[1];
                if (id && !nodeSet[id]) {
                    nodeSet[id] = true;
                    nodes.push(id);
                }
            });
        }
        // Edge: A --> B, A -.-> B, A ==> B
        var edgeMatch = line.match(/(\w+)\s*(-->|-.->|==>|---)\s*(?:\|[^|]*\|)?\s*(\w+)/);
        if (edgeMatch) {
            edges.push(edgeMatch[1] + ' -> ' + edgeMatch[3]);
            if (!nodeSet[edgeMatch[1]]) { nodeSet[edgeMatch[1]] = true; nodes.push(edgeMatch[1]); }
            if (!nodeSet[edgeMatch[3]]) { nodeSet[edgeMatch[3]] = true; nodes.push(edgeMatch[3]); }
        }
        // Subgraph
        var subMatch = line.match(/^subgraph\s+(.+)$/i);
        if (subMatch) {
            nodes.push('[subgraph: ' + subMatch[1].trim() + ']');
        }
    });
}

function extractSequence(lines, nodes, edges) {
    var participants = {};
    lines.forEach(function(line) {
        line = line.trim();
        var partMatch = line.match(/^participant\s+(\S+)(?:\s+as\s+(.+))?$/i);
        if (partMatch) {
            var name = partMatch[2] || partMatch[1];
            if (!participants[name]) { participants[name] = true; nodes.push(name); }
            return;
        }
        var msgMatch = line.match(/^(\w+)\s*(->>|-->>|->|-->|-x|--x|-\)|--\))\+?-?\s*(\w+)\s*:\s*(.+)$/);
        if (msgMatch) {
            if (!participants[msgMatch[1]]) { participants[msgMatch[1]] = true; nodes.push(msgMatch[1]); }
            if (!participants[msgMatch[3]]) { participants[msgMatch[3]] = true; nodes.push(msgMatch[3]); }
            edges.push(msgMatch[1] + ' -> ' + msgMatch[3] + ': ' + msgMatch[4].trim());
        }
    });
}

function extractClass(lines, nodes, edges) {
    var classes = {};
    lines.forEach(function(line) {
        line = line.trim();
        var classMatch = line.match(/^\s*class\s+(\w+)/i);
        if (classMatch) {
            if (!classes[classMatch[1]]) { classes[classMatch[1]] = true; nodes.push(classMatch[1]); }
            return;
        }
        var relMatch = line.match(/^(\w+)\s+(<\|--|--\|>|\*--|\*-|o--|--o|<\.\.>|\.\.\>|<\.\.|--)\s+(\w+)/);
        if (relMatch) {
            if (!classes[relMatch[1]]) { classes[relMatch[1]] = true; nodes.push(relMatch[1]); }
            if (!classes[relMatch[3]]) { classes[relMatch[3]] = true; nodes.push(relMatch[3]); }
            edges.push(relMatch[1] + ' -> ' + relMatch[3]);
        }
    });
}

function extractState(lines, nodes, edges) {
    var states = {};
    lines.forEach(function(line) {
        line = line.trim();
        var transMatch = line.match(/^(\[?\*?\]?|\w+)\s*-->\s*(\[?\*?\]?|\w+)\s*(?::\s*(.+))?$/);
        if (transMatch) {
            var from = transMatch[1], to = transMatch[2];
            if (from !== '[*]' && !states[from]) { states[from] = true; nodes.push(from); }
            if (to !== '[*]' && !states[to]) { states[to] = true; nodes.push(to); }
            edges.push(from + ' -> ' + to + (transMatch[3] ? ' (' + transMatch[3].trim() + ')' : ''));
        }
    });
}

function buildDescription(diagramType, extracted) {
    var desc = diagramType.charAt(0).toUpperCase() + diagramType.slice(1) + ' diagram';
    if (extracted.nodes.length > 0) {
        desc += ' with ' + extracted.nodes.length + ' nodes';
    }
    if (extracted.edges.length > 0) {
        desc += ' and ' + extracted.edges.length + ' edges';
    }
    desc += '.';
    return desc;
}

// Export for IIFE global access
globalThis.MermaidParser = { parse: parse };
