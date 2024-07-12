namespace ServiceLayer.Generators;

internal class MethodToGenerate
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public (string cancellationParameterType, string cancellationParameterName) CancellationParameter { get; internal set; }
    public (string? dataParameterType, string? dataParameterName) DataParmeter { get; internal set; }
    public string ReturnTypeWithTask { get; set; } = string.Empty;
}
