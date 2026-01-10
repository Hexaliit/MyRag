#!/usr/bin/env dotnet-script
#r "nuget: DocumentFormat.OpenXml, 3.0.0"

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

if (Args.Count < 1)
{
    Console.WriteLine("Usage: dotnet script compare-transcripts.csx <docx-file>");
    return 1;
}

var docxPath = Args[0];

using var wordDoc = WordprocessingDocument.Open(docxPath, false);
var body = wordDoc.MainDocumentPart?.Document.Body;

if (body == null)
{
    Console.WriteLine("Error: Could not read document body");
    return 1;
}

var text = new StringBuilder();
foreach (var paragraph in body.Elements<Paragraph>())
{
    text.AppendLine(paragraph.InnerText);
}

Console.WriteLine(text.ToString());
return 0;
