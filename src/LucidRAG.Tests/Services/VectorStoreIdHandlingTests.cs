using System.Reflection;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Tests.Services;

/// <summary>
/// Tests for vector store ID handling consistency.
/// Ensures document IDs and hashes are handled correctly across different formats.
/// </summary>
public class VectorStoreIdHandlingTests
{
    /// <summary>
    /// Calls the private ExtractDocHash method via reflection for testing.
    /// </summary>
    private static string ExtractDocHashViaReflection(string segmentId)
    {
        var method = typeof(QdrantVectorStore).GetMethod("ExtractDocHash",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null)
            throw new InvalidOperationException("ExtractDocHash method not found");
        return (string)method.Invoke(null, [segmentId])!;
    }

    [Theory]
    [InlineData("14_217138edd1c840c1", "217138edd1c840c1")]
    [InlineData("mydoc_abcdef1234567890", "abcdef1234567890")]
    [InlineData("file_name_a1b2c3d4e5f67890", "a1b2c3d4e5f67890")]
    public void ExtractDocHash_TwoPartFormat_ReturnsHash(string docId, string expectedHash)
    {
        // Two-part format: filename_contenthash (e.g., from DocumentSummary.Trace.DocumentId)
        var result = ExtractDocHashViaReflection(docId);
        Assert.Equal(expectedHash, result);
    }

    [Theory]
    [InlineData("14_217138edd1c840c1_s_0", "217138edd1c840c1")]
    [InlineData("14_217138edd1c840c1_p_5", "217138edd1c840c1")]
    [InlineData("myfile_abcdef1234567890_s_10", "abcdef1234567890")]
    public void ExtractDocHash_FourPartSegmentFormat_ReturnsHash(string segmentId, string expectedHash)
    {
        // Four-part format: filename_contenthash_type_index (e.g., Segment.Id)
        var result = ExtractDocHashViaReflection(segmentId);
        Assert.Equal(expectedHash, result);
    }

    [Fact]
    public void ExtractDocHash_DocIdAndSegmentId_ReturnSameHash()
    {
        // Critical: When storing segments with Segment.Id and querying with DocumentSummary.Trace.DocumentId,
        // the extracted hash must match!
        var docId = "14_217138edd1c840c1";           // Format from DocumentSummary.Trace
        var segmentId = "14_217138edd1c840c1_s_0";    // Format from Segment.Id

        var docHash = ExtractDocHashViaReflection(docId);
        var segmentHash = ExtractDocHashViaReflection(segmentId);

        Assert.Equal(docHash, segmentHash);
        Assert.Equal("217138edd1c840c1", docHash);
    }

    [Theory]
    [InlineData("14_217138edd1c840c1_s_0", "14_217138edd1c840c1_s_1")]
    [InlineData("14_217138edd1c840c1_s_0", "14_217138edd1c840c1_p_0")]
    [InlineData("14_217138edd1c840c1_s_0", "14_217138edd1c840c1")]
    public void ExtractDocHash_SameDocumentDifferentSegments_ReturnSameHash(string id1, string id2)
    {
        // All segments from the same document should have the same docHash
        var hash1 = ExtractDocHashViaReflection(id1);
        var hash2 = ExtractDocHashViaReflection(id2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ExtractDocHash_DifferentDocuments_ReturnDifferentHashes()
    {
        var doc1 = "file1_aaaaaaaaaaaaaaaa_s_0";
        var doc2 = "file2_bbbbbbbbbbbbbbbb_s_0";

        var hash1 = ExtractDocHashViaReflection(doc1);
        var hash2 = ExtractDocHashViaReflection(doc2);

        Assert.NotEqual(hash1, hash2);
        Assert.Equal("aaaaaaaaaaaaaaaa", hash1);
        Assert.Equal("bbbbbbbbbbbbbbbb", hash2);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("no_hash_here")]
    [InlineData("")]
    public void ExtractDocHash_NoValidHash_GeneratesFallbackHash(string input)
    {
        // When no valid 16-char hash can be found, a fallback hash is generated
        var result = ExtractDocHashViaReflection(input);

        // Fallback hash should be generated deterministically
        Assert.NotNull(result);
        Assert.NotEmpty(result);

        // Calling again should return the same result
        var result2 = ExtractDocHashViaReflection(input);
        Assert.Equal(result, result2);
    }
}
