using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace ServiceLayer.Generators;

internal class InterfaceToGenerate
{
    public string InterfaceName { get; set; } = string.Empty;
    public string InterfaceNamespace { get; set; } = string.Empty;

    public List<MethodToGenerate> Methods { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
}
