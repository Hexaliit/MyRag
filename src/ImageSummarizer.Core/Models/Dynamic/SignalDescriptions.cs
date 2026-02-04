namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
///     Human-readable descriptions for signals, used in detailed reports and documentation.
///     Provides explanatory text for each signal key to make analysis results more understandable.
/// </summary>
public static class SignalDescriptions
{
    private static readonly Dictionary<string, SignalDescription> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Identity signals
        ["identity.format"] = new SignalDescription("Image Format",
            "The file format (GIF, PNG, JPEG, etc.) detected from the image header."),
        ["identity.width"] = new SignalDescription("Width", "Image width in pixels."),
        ["identity.height"] = new SignalDescription("Height", "Image height in pixels."),
        ["identity.aspect_ratio"] =
            new SignalDescription("Aspect Ratio", "Width divided by height (e.g., 1.78 for 16:9)."),
        ["identity.file_size"] = new SignalDescription("File Size", "Size of the image file in bytes."),
        ["identity.is_animated"] =
            new SignalDescription("Animated", "Whether the image contains multiple frames (animated GIF, APNG)."),
        ["identity.frame_count"] = new SignalDescription("Frame Count", "Number of frames in an animated image."),

        // Color signals
        ["color.dominant_colors"] = new SignalDescription("Dominant Colors",
            "The most prevalent colors in the image, with percentage coverage."),
        ["color.unique_count"] =
            new SignalDescription("Unique Colors", "Approximate number of distinct colors in the image."),
        ["color.mean_saturation"] = new SignalDescription("Mean Saturation",
            "Average color saturation (0 = grayscale, 1 = fully saturated)."),
        ["color.is_grayscale"] =
            new SignalDescription("Grayscale", "Whether the image is primarily black and white or sepia."),
        ["color.palette"] =
            new SignalDescription("Color Palette", "Named colors representing the image's color scheme."),
        ["color.vibrant"] = new SignalDescription("Vibrant Color", "The most vivid, eye-catching color in the image."),
        ["color.muted"] = new SignalDescription("Muted Color", "The most subdued, soft color in the image."),

        // Motion signals
        ["motion.has_motion"] =
            new SignalDescription("Has Motion", "Whether significant motion was detected between frames."),
        ["motion.type"] = new SignalDescription("Motion Type",
            "Classification: camera_pan, camera_zoom, object_motion, or general."),
        ["motion.direction"] = new SignalDescription("Motion Direction",
            "Primary direction of movement: left, right, up, down, stationary."),
        ["motion.magnitude"] =
            new SignalDescription("Motion Magnitude", "Intensity of detected motion (higher = more movement)."),
        ["motion.activity"] =
            new SignalDescription("Motion Activity", "Percentage of the image area that contains motion."),
        ["motion.summary"] =
            new SignalDescription("Motion Summary", "Human-readable description of the motion pattern."),
        ["motion.moving_objects"] =
            new SignalDescription("Moving Objects", "List of identified objects that are in motion."),
        ["motion.temporal_consistency"] = new SignalDescription("Temporal Consistency",
            "How consistent the motion is across frames (1 = very consistent)."),
        ["motion.is_looping"] = new SignalDescription("Looping", "Whether the animation loops seamlessly."),
        ["motion.regions"] = new SignalDescription("Motion Regions", "Areas of the image where motion was detected."),

        // OCR signals
        ["ocr.full_text"] = new SignalDescription("OCR Text",
            "All text extracted from the image using optical character recognition."),
        ["ocr.markdown"] =
            new SignalDescription("OCR Markdown", "Structured OCR output in Markdown (tables, headings, lists)."),
        ["ocr.text_region"] =
            new SignalDescription("Text Region", "A specific area containing detected text with bounding box."),
        ["ocr.confidence"] = new SignalDescription("OCR Confidence",
            "How confident the OCR engine is in the extracted text (0-1)."),
        ["ocr.no_text_found"] =
            new SignalDescription("No Text Found", "OCR was performed but no readable text was detected."),
        ["ocr.unavailable"] =
            new SignalDescription("OCR Unavailable", "Tesseract data not found - use Vision LLM mode instead."),
        ["ocr.skipped"] = new SignalDescription("OCR Skipped", "OCR was skipped because text likelihood was too low."),
        ["ocr.nanonets.markdown"] =
            new SignalDescription("Nanonets OCR Markdown", "Markdown OCR output from Nanonets OCR-s."),
        ["ocr.nanonets.text"] =
            new SignalDescription("Nanonets OCR Text", "Plain-text OCR output from Nanonets OCR-s."),
        ["ocr.olmocr2.markdown"] = new SignalDescription("OlmOCR-2 Markdown", "Markdown OCR output from OlmOCR-2."),
        ["ocr.olmocr2.text"] = new SignalDescription("OlmOCR-2 Text", "Plain-text OCR output from OlmOCR-2."),
        ["ocr.voting.consensus_text"] =
            new SignalDescription("Voting Consensus", "Text agreed upon by multiple frame OCR passes."),
        ["ocr.quality.spell_check_score"] = new SignalDescription("Spell Check Score",
            "Percentage of recognized words that are valid dictionary words."),
        ["ocr.quality.is_garbled"] = new SignalDescription("Garbled Text",
            "Whether the OCR output appears to be noise rather than real text."),
        ["content.extracted_markdown"] =
            new SignalDescription("Extracted Markdown", "Structured OCR output stored as Markdown."),

        // Vision LLM signals
        ["vision.llm.caption"] =
            new SignalDescription("Caption", "A concise description of the image generated by Vision LLM."),
        ["vision.llm.detailed_description"] = new SignalDescription("Detailed Description",
            "Comprehensive analysis of the image content, setting, and mood."),
        ["vision.llm.scene"] = new SignalDescription("Scene Type",
            "Classification of the scene: indoor, outdoor, food, nature, urban, document, screenshot, meme, art."),
        ["vision.llm.entities"] = new SignalDescription("Detected Entities",
            "People, animals, objects, and text identified in the image."),
        ["vision.llm.text"] = new SignalDescription("LLM Text Reading",
            "Text extracted by Vision LLM - often better for stylized/artistic fonts."),
        ["vision.llm.disabled"] =
            new SignalDescription("Vision LLM Disabled", "Vision LLM analysis was not enabled for this run."),
        ["vision.llm.error"] =
            new SignalDescription("Vision LLM Error", "An error occurred during Vision LLM analysis."),

        // Quality signals
        ["quality.sharpness"] =
            new SignalDescription("Sharpness", "Image sharpness score - higher values indicate crisper details."),
        ["quality.blur"] = new SignalDescription("Blur", "Amount of blur detected (0 = sharp, 1 = very blurry)."),
        ["quality.noise"] = new SignalDescription("Noise", "Level of digital noise or grain in the image."),
        ["quality.compression_artifacts"] =
            new SignalDescription("Compression Artifacts", "Visible JPEG compression blocks or GIF banding."),
        ["quality.overall"] = new SignalDescription("Overall Quality", "Combined quality assessment score."),

        // Content signals
        ["content.text_likeliness"] =
            new SignalDescription("Text Likelihood", "Probability that the image contains readable text (0-1)."),
        ["content.salient_regions"] =
            new SignalDescription("Salient Regions", "Areas of the image that draw visual attention."),

        // Visual/Composition signals
        ["visual.edge_density"] =
            new SignalDescription("Edge Density", "Amount of edges/detail in the image (higher = more complex)."),
        ["visual.complexity"] = new SignalDescription("Visual Complexity", "Overall visual complexity score."),
        ["visual.mean_luminance"] =
            new SignalDescription("Mean Brightness", "Average brightness of the image (0 = black, 1 = white)."),
        ["composition.symmetry"] = new SignalDescription("Symmetry", "How symmetrical the image composition is."),
        ["composition.rule_of_thirds"] =
            new SignalDescription("Rule of Thirds", "How well the composition follows the rule of thirds.")
    };

    /// <summary>
    ///     Get all registered signal descriptions
    /// </summary>
    public static IReadOnlyDictionary<string, SignalDescription> All => Descriptions;

    /// <summary>
    ///     Get the description for a signal key
    /// </summary>
    public static SignalDescription? GetDescription(string signalKey)
    {
        // Try exact match first
        if (Descriptions.TryGetValue(signalKey, out var desc))
            return desc;

        // Try prefix match for entity types like "vision.llm.entity.person"
        var prefix = signalKey.Contains('.')
            ? string.Join(".", signalKey.Split('.').Take(3))
            : signalKey;

        if (Descriptions.TryGetValue(prefix, out desc))
            return desc;

        return null;
    }

    /// <summary>
    ///     Format a signal for display with its description
    /// </summary>
    public static string FormatWithDescription(string key, object? value, double confidence)
    {
        var desc = GetDescription(key);
        var name = desc?.Name ?? key;
        var explanation = desc?.Description ?? "";

        var valueStr = value?.ToString() ?? "null";
        if (valueStr.Length > 100)
            valueStr = valueStr.Substring(0, 100) + "...";

        return $"**{name}** ({confidence:P0}): {valueStr}\n  _{explanation}_";
    }
}

/// <summary>
///     Description for a signal type
/// </summary>
public record SignalDescription(string Name, string Description);