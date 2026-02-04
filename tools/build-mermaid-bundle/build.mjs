/**
 * Build script for the mermaid parser bundle.
 * Produces an IIFE bundle that exposes MermaidParser.parse() globally.
 *
 * Usage: node build.mjs
 * Output: ../../src/CodeSummarizer.Mermaid.Jint/Resources/mermaid-parser-bundle.js
 */

import * as esbuild from 'esbuild';
import {dirname, resolve} from 'path';
import {fileURLToPath} from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const outFile = resolve(__dirname, '../../src/CodeSummarizer.Mermaid.Jint/Resources/mermaid-parser-bundle.js');

async function build() {
    try {
        const result = await esbuild.build({
            entryPoints: [resolve(__dirname, 'mermaid-parser-entry.js')],
            bundle: true,
            format: 'iife',
            globalName: 'MermaidParserBundle',
            outfile: outFile,
            target: 'es2020',
            platform: 'neutral',
            mainFields: ['module', 'main'],
            conditions: ['import', 'default'],
            // Tree-shake rendering code we don't need
            treeShaking: true,
            // Don't minify for debuggability (the bundle is an embedded resource anyway)
            minify: false,
            // Source map for debugging issues
            sourcemap: false,
            // Log level
            logLevel: 'info',
            // Define stubs for node builtins that mermaid might transitively reference
            define: {
                'process.env.NODE_ENV': '"production"',
                'process.platform': '"win32"',
            },
            // Ignore node-specific modules
            external: [],
        });

        console.log(`Bundle written to: ${outFile}`);
        if (result.warnings.length > 0) {
            console.log('Warnings:', result.warnings);
        }
    } catch (error) {
        console.error('Build failed:', error);
        process.exit(1);
    }
}

build();
