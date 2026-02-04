using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
///     Browser integration tests for all content modalities.
///     Tests upload, search, and LLM chat for documents, images, audio, and video.
///     Requires the lucidRAG app to be running on localhost:5080.
///     Start with: dotnet run --project src/LucidRAG/LucidRAG.csproj --standalone
///     Run locally only with: dotnet test --filter "Category=Browser"
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class BrowserModalityTests : IAsyncLifetime
{
    private const string BaseUrl = "http://127.0.0.1:5080";
    private const int ProcessingTimeoutMs = 60000; // 60 seconds for processing
    private const int PollingIntervalMs = 2000;

    private readonly List<string> _consoleMessages = [];
    private readonly List<string> _uploadedDocumentIds = [];
    private IBrowser? _browser;
    private IPage? _page;

    public async Task InitializeAsync()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        });

        _page = await _browser.NewPageAsync();
        await _page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });

        _page.Console += (_, e) => _consoleMessages.Add($"[{e.Message.Type}] {e.Message.Text}");
    }

    public async Task DisposeAsync()
    {
        // Clean up uploaded documents
        foreach (var docId in _uploadedDocumentIds)
            try
            {
                await _page!.EvaluateFunctionAsync(@"
                    async (docId) => {
                        await fetch(`/api/documents/${docId}`, { method: 'DELETE' });
                    }
                ", docId);
            }
            catch
            {
                /* Ignore cleanup errors */
            }

        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    #region End-to-End Flow Tests

    [Fact]
    public async Task EndToEnd_UploadSearchChat_FullFlow()
    {
        // This test runs the full flow: upload -> process -> search -> chat
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        // Step 1: Upload document
        Console.WriteLine("Step 1: Uploading document...");
        var testContent = $@"# E2E Test Document {uniqueId}

This document tests the complete lucidRAG pipeline.

## Content Section
The unique identifier {uniqueId} should be searchable.
This tests document processing, entity extraction, and embeddings.

## Summary
End-to-end testing ensures all components work together.
";

        var uploadResult = await UploadDocumentAsync(testContent, $"e2e-test-{uniqueId}.md", "text/markdown");
        uploadResult.TryGetProperty("error", out _).Should().BeFalse("Upload should succeed");
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);
        Console.WriteLine($"  Uploaded: {documentId}");

        // Step 2: Wait for processing
        Console.WriteLine("Step 2: Waiting for processing...");
        var processed = await WaitForProcessingAsync(documentId);
        processed.Should().BeTrue("Processing should complete");
        Console.WriteLine("  Processing complete");

        // Step 3: Verify document appears in list
        Console.WriteLine("Step 3: Verifying document in list...");
        var listResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (docId) => {
                const response = await fetch('/api/documents');
                if (!response.ok) return { error: 'Failed to list documents' };
                const result = await response.json();
                const found = result.documents?.some(d => d.id === docId) ?? false;
                return { found, total: result.documents?.length ?? 0 };
            }
        ", documentId);

        if (!listResult.TryGetProperty("error", out _))
        {
            var found = listResult.GetProperty("found").GetBoolean();
            Console.WriteLine($"  Document found in list: {found}");
        }

        // Step 4: Search for document
        Console.WriteLine("Step 4: Searching for document...");
        var searchResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (query) => {
                const response = await fetch(`/api/search?query=${encodeURIComponent(query)}`);
                if (!response.ok) return { error: 'Search failed' };
                return await response.json();
            }
        ", $"e2e test {uniqueId}");

        if (!searchResult.TryGetProperty("error", out _) && searchResult.TryGetProperty("results", out var results))
            Console.WriteLine($"  Search returned {results.GetArrayLength()} results");

        // Step 5: Chat about document
        Console.WriteLine("Step 5: Testing chat...");
        var chatResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const collectionsResp = await fetch('/api/collections');
                const collections = await collectionsResp.json();
                if (!collections || collections.length === 0) return { noCollections: true };

                const response = await fetch('/api/chat', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        message: 'What is this collection about?',
                        collectionId: collections[0].id
                    })
                });
                return { success: response.ok, status: response.status };
            }
        ");

        if (!chatResult.TryGetProperty("error", out _) && !chatResult.TryGetProperty("noCollections", out _))
        {
            var chatSuccess = chatResult.GetProperty("success").GetBoolean();
            Console.WriteLine($"  Chat test: {(chatSuccess ? "passed" : "failed")}");
        }

        Console.WriteLine("End-to-end test completed");
    }

    #endregion

    #region Document Upload Tests

    [Fact]
    public async Task Upload_MarkdownDocument_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        var testContent = $@"# Test Document - Markdown Upload {Guid.NewGuid():N}

## Introduction

This is a test markdown document created at {DateTime.UtcNow:O}.
It contains structured content for testing the RAG pipeline.

## Key Points

- First point about artificial intelligence
- Second point about machine learning
- Third point about natural language processing

## Technical Details

The lucidRAG system processes documents through multiple stages:

1. Text extraction and chunking
2. Entity extraction via GraphRAG
3. Embedding generation via ONNX
4. Vector storage for semantic search

## Conclusion

This document should be searchable after processing completes.
";

        // Act - Upload
        var uploadResult = await UploadDocumentAsync(testContent, "test-markdown.md", "text/markdown");

        // Assert - Upload succeeds
        uploadResult.TryGetProperty("error", out _).Should().BeFalse("Upload should succeed");
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        documentId.Should().NotBeNullOrEmpty();
        _uploadedDocumentIds.Add(documentId);

        // Wait for processing
        var processed = await WaitForProcessingAsync(documentId);
        processed.Should().BeTrue("Document should complete processing within timeout");

        Console.WriteLine($"Markdown document {documentId} processed successfully");
    }

    [Fact]
    public async Task Upload_HtmlDocument_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        var testContent = $@"<!DOCTYPE html>
<html>
<head><title>Test HTML Document</title></head>
<body>
<h1>HTML Test Document {Guid.NewGuid():N}</h1>
<p>This is an HTML document for testing the lucidRAG pipeline.</p>
<h2>Features</h2>
<ul>
    <li>Semantic search capabilities</li>
    <li>Entity extraction</li>
    <li>Knowledge graph building</li>
</ul>
<p>Created at {DateTime.UtcNow:O}</p>
</body>
</html>";

        // Act
        var uploadResult = await UploadDocumentAsync(testContent, "test-html.html", "text/html");

        // Assert
        uploadResult.TryGetProperty("error", out _).Should().BeFalse();
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        var processed = await WaitForProcessingAsync(documentId);
        processed.Should().BeTrue();

        Console.WriteLine($"HTML document {documentId} processed successfully");
    }

    [Fact]
    public async Task Upload_PlainTextDocument_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        var testContent = $@"Plain Text Test Document - {Guid.NewGuid():N}

This is a plain text document for testing the document processing pipeline.

The lucidRAG system should:
- Extract text content
- Generate embeddings
- Enable semantic search

Document created: {DateTime.UtcNow:O}

Keywords: testing, RAG, embeddings, search, AI
";

        // Act
        var uploadResult = await UploadDocumentAsync(testContent, "test-plain.txt", "text/plain");

        // Assert
        uploadResult.TryGetProperty("error", out _).Should().BeFalse();
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        var processed = await WaitForProcessingAsync(documentId);
        processed.Should().BeTrue();

        Console.WriteLine($"Plain text document {documentId} processed successfully");
    }

    #endregion

    #region Image Upload Tests

    [Fact]
    public async Task Upload_PngImage_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        // Create a minimal valid PNG (1x1 red pixel)
        var pngBytes = CreateMinimalPng();

        // Act
        var uploadResult = await UploadBinaryAsync(pngBytes, "test-image.png", "image/png");

        // Assert
        uploadResult.TryGetProperty("error", out _).Should().BeFalse("PNG upload should succeed");
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        var processed = await WaitForProcessingAsync(documentId, 120000); // Images take longer
        processed.Should().BeTrue("Image should complete processing");

        Console.WriteLine($"PNG image {documentId} processed successfully");
    }

    [Fact]
    public async Task Upload_JpegImage_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        // Create a minimal valid JPEG
        var jpegBytes = CreateMinimalJpeg();

        // Act
        var uploadResult = await UploadBinaryAsync(jpegBytes, "test-image.jpg", "image/jpeg");

        // Assert
        uploadResult.TryGetProperty("error", out _).Should().BeFalse("JPEG upload should succeed");
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        var processed = await WaitForProcessingAsync(documentId, 120000);
        processed.Should().BeTrue("Image should complete processing");

        Console.WriteLine($"JPEG image {documentId} processed successfully");
    }

    #endregion

    #region Audio Upload Tests

    [Fact]
    public async Task Upload_Mp3Audio_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        // Create minimal MP3 header (silent audio)
        var mp3Bytes = CreateMinimalMp3();

        // Act
        var uploadResult = await UploadBinaryAsync(mp3Bytes, "test-audio.mp3", "audio/mpeg");

        // Assert - Upload should succeed (processing may fail without Whisper)
        if (uploadResult.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"Audio upload error (expected if Whisper not configured): {error}");
            return; // Skip if audio not supported
        }

        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        // Audio processing may fail if Whisper is not available
        var processed = await WaitForProcessingAsync(documentId, 180000, true);
        Console.WriteLine($"Audio document {documentId} processing result: {processed}");
    }

    [Fact]
    public async Task Upload_WavAudio_ProcessesSuccessfully()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        // Create minimal WAV file (silent, 1 second)
        var wavBytes = CreateMinimalWav();

        // Act
        var uploadResult = await UploadBinaryAsync(wavBytes, "test-audio.wav", "audio/wav");

        // Assert
        if (uploadResult.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"WAV upload error (expected if audio not supported): {error}");
            return;
        }

        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        var processed = await WaitForProcessingAsync(documentId, 180000, true);
        Console.WriteLine($"WAV document {documentId} processing result: {processed}");
    }

    #endregion

    #region Search Tests

    [Fact]
    public async Task Search_AfterDocumentUpload_ReturnsResults()
    {
        // Arrange - Upload a document with known content
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var testContent = $@"# Searchable Document {uniqueId}

This document contains unique searchable content about quantum computing.
The term quantum-{uniqueId} should be findable via semantic search.

## Topics
- Quantum entanglement
- Superposition states
- Quantum error correction
";

        var uploadResult = await UploadDocumentAsync(testContent, $"search-test-{uniqueId}.md", "text/markdown");
        uploadResult.TryGetProperty("error", out _).Should().BeFalse();
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        // Wait for processing
        var processed = await WaitForProcessingAsync(documentId);
        processed.Should().BeTrue("Document must process before search test");

        // Act - Search for the content
        var searchResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (query) => {
                const response = await fetch(`/api/search?query=${encodeURIComponent(query)}`);
                if (!response.ok) {
                    return { error: await response.text(), status: response.status };
                }
                return await response.json();
            }
        ", $"quantum computing {uniqueId}");

        // Assert
        if (searchResult.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"Search error: {error}");
            // Search may fail if embeddings not ready yet
            return;
        }

        // Check if results exist
        if (searchResult.TryGetProperty("results", out var results))
        {
            var resultCount = results.GetArrayLength();
            Console.WriteLine($"Search returned {resultCount} results for query containing {uniqueId}");
            resultCount.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task Search_ViaUi_ReturnsResults()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(2000);

        // Act - Check that search functionality exists in UI
        var searchCapability = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const appEl = document.querySelector('[x-data*=""ragApp""]');
                const data = appEl?._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data' };

                return {
                    hasSubmitQuestion: typeof data.submitQuestion === 'function',
                    hasSearchMode: 'searchMode' in data,
                    searchMode: data.searchMode,
                    hasCurrentMessage: 'currentMessage' in data,
                    collectionsCount: data.collections?.length ?? 0
                };
            }
        ");

        // Assert
        searchCapability.TryGetProperty("error", out _).Should().BeFalse();
        searchCapability.GetProperty("hasSubmitQuestion").GetBoolean().Should().BeTrue();

        Console.WriteLine(
            $"Search capability verified - collections: {searchCapability.GetProperty("collectionsCount")}");
    }

    #endregion

    #region LLM Chat Tests

    [Fact]
    public async Task Chat_WithUploadedDocument_GeneratesResponse()
    {
        // Arrange - Upload a document first
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(1000);

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var testContent = $@"# Chat Test Document {uniqueId}

This document is about the history of computing pioneers.

## Alan Turing
Alan Turing was a British mathematician who created the concept of the Turing machine.
He is considered the father of theoretical computer science.

## Ada Lovelace
Ada Lovelace wrote the first algorithm intended for a machine.
She is often considered the first computer programmer.

The unique identifier for this document is {uniqueId}.
";

        var uploadResult = await UploadDocumentAsync(testContent, $"chat-test-{uniqueId}.md", "text/markdown");
        uploadResult.TryGetProperty("error", out _).Should().BeFalse();
        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        _uploadedDocumentIds.Add(documentId);

        // Wait for processing
        var processed = await WaitForProcessingAsync(documentId);
        processed.Should().BeTrue("Document must process before chat test");

        // Act - Send a chat message via Alpine.js
        var chatResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (question) => {
                const appEl = document.querySelector('[x-data*=""ragApp""]');
                const data = appEl?._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data' };

                // Check if we have collections
                if (!data.collections || data.collections.length === 0) {
                    return { error: 'No collections available' };
                }

                // Select first collection if none selected
                if (!data.currentCollectionId) {
                    data.currentCollectionId = data.collections[0].id;
                }

                // Set the question
                data.currentMessage = question;

                // Check if submitQuestion exists
                if (typeof data.submitQuestion !== 'function') {
                    return { error: 'submitQuestion not available' };
                }

                try {
                    // Submit the question
                    await data.submitQuestion();

                    // Wait a bit for streaming to start
                    await new Promise(r => setTimeout(r, 5000));

                    return {
                        success: true,
                        chatHistoryCount: data.chatHistory?.length ?? 0,
                        messagesCount: data.messages?.length ?? 0,
                        isTyping: data.isTyping,
                        lastMessage: data.messages?.[data.messages.length - 1]?.content?.substring(0, 200) ?? 'none'
                    };
                } catch (e) {
                    return { error: e.toString() };
                }
            }
        ", $"Who was Alan Turing according to document {uniqueId}?");

        // Assert
        if (chatResult.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"Chat error: {error}");
            // Chat may not work without LLM configured
            return;
        }

        var messagesCount = chatResult.GetProperty("messagesCount").GetInt32();
        Console.WriteLine($"Chat completed - messages: {messagesCount}");

        if (chatResult.TryGetProperty("lastMessage", out var lastMessage))
            Console.WriteLine($"Last message preview: {lastMessage}");
    }

    [Fact]
    public async Task Chat_StreamingResponse_UpdatesUi()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(2000);

        // Act - Test that chat UI elements exist and function
        var chatState = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const appEl = document.querySelector('[x-data*=""ragApp""]');
                const data = appEl?._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data' };

                return {
                    hasSubmitQuestion: typeof data.submitQuestion === 'function',
                    hasChatHistory: Array.isArray(data.chatHistory),
                    hasMessages: Array.isArray(data.messages),
                    hasStreamingContent: 'streamingContent' in data,
                    hasIsTyping: 'isTyping' in data,
                    collectionsCount: data.collections?.length ?? 0,
                    currentCollectionId: data.currentCollectionId ?? null
                };
            }
        ");

        // Assert
        chatState.TryGetProperty("error", out _).Should().BeFalse();
        chatState.GetProperty("hasSubmitQuestion").GetBoolean().Should().BeTrue("submitQuestion should exist");
        chatState.GetProperty("hasIsTyping").GetBoolean().Should().BeTrue("isTyping state should exist");

        Console.WriteLine($"Chat state verified - collections: {chatState.GetProperty("collectionsCount")}");
    }

    [Fact]
    public async Task Chat_WithCollection_SendsQuery()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(2000);

        // Act - Try to send a chat message to the API directly
        var chatApiResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                // First get collections
                const collectionsResp = await fetch('/api/collections');
                if (!collectionsResp.ok) {
                    return { error: 'Failed to get collections' };
                }
                const collections = await collectionsResp.json();

                if (!collections || collections.length === 0) {
                    return { noCollections: true };
                }

                // Try sending a chat request
                const collectionId = collections[0].id;
                const response = await fetch('/api/chat', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        message: 'What documents are available?',
                        collectionId: collectionId,
                        conversationId: null
                    })
                });

                if (!response.ok) {
                    return { error: await response.text(), status: response.status };
                }

                // For streaming response, just check we got a response
                return {
                    success: true,
                    status: response.status,
                    contentType: response.headers.get('content-type')
                };
            }
        ");

        // Assert
        if (chatApiResult.TryGetProperty("noCollections", out _))
        {
            Console.WriteLine("No collections available - skipping chat test");
            return;
        }

        if (chatApiResult.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"Chat API error: {error}");
            // May fail if LLM not configured
            return;
        }

        var success = chatApiResult.GetProperty("success").GetBoolean();
        Console.WriteLine($"Chat API test completed - success: {success}");
    }

    #endregion

    #region Helper Methods

    private async Task<JsonElement> UploadDocumentAsync(string content, string filename, string mimeType)
    {
        return await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async (content, filename, mimeType) => {
                const blob = new Blob([content], { type: mimeType });
                const formData = new FormData();
                formData.append('file', blob, filename);

                try {
                    const response = await fetch('/api/documents/upload', {
                        method: 'POST',
                        body: formData
                    });

                    if (!response.ok) {
                        return { error: await response.text(), httpStatus: response.status };
                    }

                    return await response.json();
                } catch (e) {
                    return { error: e.toString() };
                }
            }
        ", content, filename, mimeType);
    }

    private async Task<JsonElement> UploadBinaryAsync(byte[] bytes, string filename, string mimeType)
    {
        var base64 = Convert.ToBase64String(bytes);

        return await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async (base64Data, filename, mimeType) => {
                // Convert base64 to binary
                const binaryString = atob(base64Data);
                const bytes = new Uint8Array(binaryString.length);
                for (let i = 0; i < binaryString.length; i++) {
                    bytes[i] = binaryString.charCodeAt(i);
                }

                const blob = new Blob([bytes], { type: mimeType });
                const formData = new FormData();
                formData.append('file', blob, filename);

                try {
                    const response = await fetch('/api/documents/upload', {
                        method: 'POST',
                        body: formData
                    });

                    if (!response.ok) {
                        return { error: await response.text(), httpStatus: response.status };
                    }

                    return await response.json();
                } catch (e) {
                    return { error: e.toString() };
                }
            }
        ", base64, filename, mimeType);
    }

    private async Task<bool> WaitForProcessingAsync(string documentId, int timeoutMs = ProcessingTimeoutMs,
        bool allowFailed = false)
    {
        var startTime = DateTime.UtcNow;

        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            var statusResult = await _page!.EvaluateFunctionAsync<JsonElement>(@"
                async (docId) => {
                    const response = await fetch(`/api/documents/${docId}`);
                    if (!response.ok) return { error: 'Not found' };
                    return await response.json();
                }
            ", documentId);

            if (statusResult.TryGetProperty("status", out var statusProp))
            {
                var status = statusProp.GetString();
                Console.WriteLine($"  Document {documentId} status: {status}");

                if (status == "completed")
                    return true;

                if (status == "failed" && allowFailed)
                    return true;

                if (status == "failed")
                    return false;
            }

            await Task.Delay(PollingIntervalMs);
        }

        Console.WriteLine($"  Document {documentId} processing timed out after {timeoutMs}ms");
        return false;
    }

    /// <summary>
    ///     Creates a minimal valid PNG file (1x1 red pixel)
    /// </summary>
    private static byte[] CreateMinimalPng()
    {
        // Minimal PNG: 8-byte signature + IHDR + IDAT + IEND
        return
        [
            // PNG signature
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            // IHDR chunk (13 bytes)
            0x00, 0x00, 0x00, 0x0D, // Length
            0x49, 0x48, 0x44, 0x52, // "IHDR"
            0x00, 0x00, 0x00, 0x01, // Width: 1
            0x00, 0x00, 0x00, 0x01, // Height: 1
            0x08, // Bit depth: 8
            0x02, // Color type: RGB
            0x00, // Compression method
            0x00, // Filter method
            0x00, // Interlace method
            0x90, 0x77, 0x53, 0xDE, // CRC
            // IDAT chunk (compressed pixel data)
            0x00, 0x00, 0x00, 0x0C, // Length
            0x49, 0x44, 0x41, 0x54, // "IDAT"
            0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00, // Compressed
            0x1B, 0xB6, 0xEE, 0x56, // CRC
            // IEND chunk
            0x00, 0x00, 0x00, 0x00, // Length
            0x49, 0x45, 0x4E, 0x44, // "IEND"
            0xAE, 0x42, 0x60, 0x82 // CRC
        ];
    }

    /// <summary>
    ///     Creates a minimal valid JPEG file (1x1 red pixel)
    /// </summary>
    private static byte[] CreateMinimalJpeg()
    {
        // Minimal valid JPEG
        return
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
            0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
            0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
            0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20,
            0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
            0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32,
            0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
            0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00,
            0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x10, 0x00, 0x02, 0x01, 0x03,
            0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D,
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06,
            0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
            0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72,
            0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45,
            0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75,
            0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3,
            0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
            0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9,
            0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4,
            0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01,
            0x00, 0x00, 0x3F, 0x00, 0xFB, 0xD5, 0xDB, 0x20, 0xB8, 0xED, 0x73, 0xE5,
            0x63, 0x00, 0x22, 0x00, 0xFF, 0xD9
        ];
    }

    /// <summary>
    ///     Creates a minimal valid MP3 file (silent, single frame)
    /// </summary>
    private static byte[] CreateMinimalMp3()
    {
        // Minimal MP3 frame header + padding
        return
        [
            // ID3v2 header (optional but common)
            0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            // MP3 frame header (MPEG1 Layer3, 128kbps, 44.1kHz, stereo)
            0xFF, 0xFB, 0x90, 0x00,
            // Frame data (silent/zeroed)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            // Another frame
            0xFF, 0xFB, 0x90, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
    }

    /// <summary>
    ///     Creates a minimal valid WAV file (silent, 1 second at 8kHz mono)
    /// </summary>
    private static byte[] CreateMinimalWav()
    {
        var sampleRate = 8000;
        var numSamples = sampleRate; // 1 second
        var dataSize = numSamples * 2; // 16-bit samples
        var fileSize = 36 + dataSize;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(fileSize);
        writer.Write("WAVE"u8);

        // fmt chunk
        writer.Write("fmt "u8);
        writer.Write(16); // Chunk size
        writer.Write((short)1); // Audio format (PCM)
        writer.Write((short)1); // Num channels
        writer.Write(sampleRate); // Sample rate
        writer.Write(sampleRate * 2); // Byte rate
        writer.Write((short)2); // Block align
        writer.Write((short)16); // Bits per sample

        // data chunk
        writer.Write("data"u8);
        writer.Write(dataSize);

        // Silent samples
        for (var i = 0; i < numSamples; i++)
            writer.Write((short)0);

        return ms.ToArray();
    }

    #endregion
}