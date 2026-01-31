using CodeSummarizer.Parsing;

namespace CodeSummarizer.Core.Tests;

public class TreeSitterParserTests
{
    private readonly TreeSitterParser _parser = new();

    [Fact]
    public void Parse_CSharp_ExtractsClassAndMethod()
    {
        var code = """
            using System;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Multiply(int a, int b)
                {
                    return a * b;
                }
            }
            """;

        var result = _parser.Parse(code, "csharp");

        result.Parsed.Should().BeTrue();
        result.Language.Should().Be("csharp");
        result.Classes.Should().ContainSingle(c => c.Name == "Calculator");
        result.Functions.Should().Contain(f => f.Name == "Add");
        result.Functions.Should().Contain(f => f.Name == "Multiply");
    }

    [Fact]
    public void Parse_Python_ExtractsFunctionsAndImports()
    {
        var code = """
            import os
            from pathlib import Path

            def greet(name):
                print(f"Hello, {name}!")

            def calculate_area(width, height):
                return width * height
            """;

        var result = _parser.Parse(code, "python");

        result.Parsed.Should().BeTrue();
        result.Functions.Should().Contain(f => f.Name == "greet");
        result.Functions.Should().Contain(f => f.Name == "calculate_area");
        result.Imports.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Parse_JavaScript_ExtractsFunctions()
    {
        var code = """
            function sayHello(name) {
                console.log(`Hello, ${name}!`);
            }

            const add = (a, b) => a + b;
            """;

        var result = _parser.Parse(code, "javascript");

        result.Parsed.Should().BeTrue();
        result.Functions.Should().Contain(f => f.Name == "sayHello");
    }

    [Fact]
    public void Parse_UnsupportedLanguage_FallsBackToHeuristic()
    {
        var code = """
            function hello(name)
                print("Hello " .. name)
            end

            class MyClass
                def method()
                    -- do stuff
                end
            end
            """;

        // Use a language that likely isn't in our registry
        var result = _parser.Parse(code, "unreallanguage");

        result.Parsed.Should().BeFalse();
        result.Language.Should().Be("unreallanguage");
    }

    [Fact]
    public void Parse_EmptyCode_ReturnsEmptyStructure()
    {
        var result = _parser.Parse("", "csharp");

        result.TotalLines.Should().Be(1);
        result.Functions.Should().BeEmpty();
        result.Classes.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Json_ParsesWithoutError()
    {
        var code = """
            {
                "name": "test",
                "version": "1.0.0",
                "dependencies": {
                    "lodash": "^4.17.21"
                }
            }
            """;

        var result = _parser.Parse(code, "json");

        // JSON has no functions/classes, but should parse without error
        result.Parsed.Should().BeTrue();
        result.Functions.Should().BeEmpty();
        result.Classes.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Go_ExtractsFunctions()
    {
        var code = """
            package main

            import "fmt"

            func main() {
                fmt.Println("Hello, World!")
            }

            func add(a int, b int) int {
                return a + b
            }
            """;

        var result = _parser.Parse(code, "go");

        result.Parsed.Should().BeTrue();
        result.Functions.Should().Contain(f => f.Name == "main");
        result.Functions.Should().Contain(f => f.Name == "add");
    }

    [Fact]
    public void Parse_HeuristicFallback_ExtractsCStyleFunctions()
    {
        var code = """
            function processData(input)
                local result = {}
                return result
            end
            """;

        var result = _parser.Parse(code, "unreallanguage2");

        result.Parsed.Should().BeFalse();
        result.Functions.Should().Contain(f => f.Name == "processData");
    }
}
