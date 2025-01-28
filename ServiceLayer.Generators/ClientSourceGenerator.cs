using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ServiceLayer.Generators;

[Generator]
public class ClientSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<InterfaceToGenerate?> interfacesToGenerate = context.SyntaxProvider
            .CreateSyntaxProvider(
            predicate: (s, ct) => IsSyntaxTargetForGeneration(s),
            transform: (ctx, ct) => GetSemanticTargetForGeneration(ctx))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(interfacesToGenerate, static (ctx, source) =>
        {
            if (source is not null)
                Execute(ctx, source);
        });
    }

    static bool IsSyntaxTargetForGeneration(SyntaxNode node)
        => node is InterfaceDeclarationSyntax m && m.AttributeLists.Count > 0;

    static InterfaceToGenerate? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
    {
        // we know the node is a EnumDeclarationSyntax thanks to IsSyntaxTargetForGeneration
        var enumDeclarationSyntax = (InterfaceDeclarationSyntax)context.Node;

        var exposedServiceAttribute = EnumerateAttributeSyntax(context.SemanticModel, enumDeclarationSyntax, "ServiceLayer.Abstractions.ExposedServiceInterfaceAttribute").FirstOrDefault();
        if (exposedServiceAttribute is not null)
        {
            return GetInterfaceToGenerate(context.SemanticModel, enumDeclarationSyntax);
        }

        // we didn't find the attribute we were looking for
        return null;
    }

    private static InterfaceToGenerate GetInterfaceToGenerate(SemanticModel semanticModel, InterfaceDeclarationSyntax interfaceDeclarationSyntax)
    {

        var interfaceSymbol = semanticModel.GetDeclaredSymbol(interfaceDeclarationSyntax);
        if (interfaceSymbol is null)
        {
            throw new Exception("Could not get symbol for interface.");
        }

        var interfaceName = interfaceDeclarationSyntax.Identifier.Text;

        var interfaceToGenerate = new InterfaceToGenerate
        {
            InterfaceNamespace = interfaceSymbol.ContainingNamespace.ToDisplayString(),
            InterfaceName = interfaceName,
        };


        foreach (var members in interfaceDeclarationSyntax.Members)
        {
            if (members is MethodDeclarationSyntax methodDeclarationSyntax)
            {
                var methodSymbolInfo = semanticModel.GetDeclaredSymbol(methodDeclarationSyntax);
                if (methodSymbolInfo is null)
                {
                    throw new Exception("Could not get symbol for method.");
                }

                var methodName = methodSymbolInfo.Name;
                var routeInterfaceComponent = interfaceName;
                if (routeInterfaceComponent.Length > 2 && routeInterfaceComponent.StartsWith("I") && char.IsUpper(routeInterfaceComponent[1]))
                {
                    routeInterfaceComponent = routeInterfaceComponent.Substring(1);
                }
                var defaultRoute = $"s/{routeInterfaceComponent}/{methodName}".ToLower();

                var exposedServiceRoute = EnumerateAttributeSyntax(semanticModel, methodDeclarationSyntax, "ServiceLayer.Abstractions.ExposedServiceRouteAttribute").FirstOrDefault();
                if (exposedServiceRoute is not null)
                {
                    var route = exposedServiceRoute.ArgumentList?.Arguments.FirstOrDefault();
                    if (route?.Expression is LiteralExpressionSyntax)
                    {
                        defaultRoute = ((LiteralExpressionSyntax)route.Expression).Token.ValueText.ToLower();
                    }
                    else
                    {
                        interfaceToGenerate.Diagnostics.Add(
                            Diagnostic.Create("IF000", "Interface Generation", $"Route parameter to ExposedServiceRouteAttribute was not provided.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: exposedServiceRoute.GetLocation())
                        );
                    }
                }

                if (methodSymbolInfo.ReturnType is null)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Return from an externally exposed interface method must not be null, it was.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
                    );
                    continue;
                }

                var returnTypeWithTask = methodSymbolInfo.ReturnType.ToDisplayString();
                var taskRegex = new Regex("System.Threading.Tasks.Task<(?<returnType>.*)>");
                var taskRegexMatch = taskRegex.Match(returnTypeWithTask);
                if (!taskRegexMatch.Success)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Return from an externally exposed interface method must be a task that returns a result, it was {methodSymbolInfo.ReturnType.ToString()}", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
                    );

                    continue;
                }
                var returnType = taskRegexMatch.Groups["returnType"].Value;

                var parameterCount = methodDeclarationSyntax.ParameterList.Parameters.Count;
                if (parameterCount > 2 || parameterCount < 1)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Externally exposed interface method must have 1 or 2 parameters, it had {parameterCount}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
                    );

                    continue;
                }

                var parameterQueue = new Queue<ParameterSyntax>(methodDeclarationSyntax.ParameterList.Parameters.Where(r => r is ParameterSyntax).Cast<ParameterSyntax>());
                string? dataParameterType = null;
                string? dataParameterName = null;
                string? cancellationParameterType = null;
                string? cancellationParameterName = null;
                if (parameterCount > 1)
                {
                    var dataParameter = parameterQueue.Dequeue();
                    var dataParameterNameNode = dataParameter.ChildNodesAndTokens().First();
                    var dataParameterTokenNode = dataParameter.ChildNodesAndTokens().Last();
                    var dataParameterExpressionSyntax = dataParameterNameNode.AsNode();
                    if (dataParameterExpressionSyntax is not IdentifierNameSyntax dataParameterNameSyntax && dataParameterExpressionSyntax is not GenericNameSyntax)
                    {
                        interfaceToGenerate.Diagnostics.Add(
                            Diagnostic.Create("IF000", "Interface Generation", $"Data parameter must be a simple type, or generic, it was {dataParameterNameNode.ToFullString()}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: dataParameter.GetLocation())
                        );

                        continue;
                    }

                    var identifierSymbolInfo = semanticModel.GetSymbolInfo(dataParameterExpressionSyntax);
                    if (identifierSymbolInfo.Symbol is not INamedTypeSymbol dataParameterSymbol)
                    {
                        interfaceToGenerate.Diagnostics.Add(
                            Diagnostic.Create("IF000", "Interface Generation", $"Data parameter must be a simple type, it was {dataParameter.Type?.ToFullString() ?? "Unknown data paramter type"}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: dataParameter.GetLocation())
                        );

                        continue;
                    }

                    dataParameterType = dataParameterSymbol.ToDisplayString();
                    dataParameterName = dataParameterTokenNode.AsToken().ToFullString();
                }

                var cancellationParameter = parameterQueue.Dequeue();
                var cancellationParameterNameNode = cancellationParameter.ChildNodesAndTokens().First();
                var cancellationParameterTokenNode = cancellationParameter.ChildNodesAndTokens().Skip(1).First();
                if (cancellationParameterNameNode.AsNode() is not IdentifierNameSyntax cancellationParameterNameSyntax)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Data parameter must be a simple type, it was {cancellationParameterNameNode.ToFullString()}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: cancellationParameter.GetLocation())
                    );

                    continue;
                }

                var cancellationIdentifierSymbolInfo = semanticModel.GetSymbolInfo(cancellationParameterNameSyntax);
                if (cancellationIdentifierSymbolInfo.Symbol is not INamedTypeSymbol cancellationParameterSymbol)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Data parameter must be a simple type, it was {cancellationParameter.Type?.ToFullString() ?? "Unknown cancellation parameter type"}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: cancellationParameter.GetLocation())
                    );

                    continue;
                }

                if (cancellationIdentifierSymbolInfo.Symbol.ToDisplayString() != "System.Threading.CancellationToken")
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Cancellation parameter must be a CancellationToken, it was {cancellationParameterSymbol.ToDisplayString()}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: cancellationParameter.GetLocation())
                    );

                    continue;
                }

                cancellationParameterType = cancellationParameterSymbol.ToDisplayString();
                cancellationParameterName = cancellationParameterTokenNode.AsToken().ToFullString();

                //throw new Exception(x);
                interfaceToGenerate.Methods.Add(new MethodToGenerate
                {
                    Name = methodName,
                    ReturnTypeWithTask = returnTypeWithTask,
                    ReturnType = returnType,
                    Route = defaultRoute,
                    DataParmeter = (dataParameterType, dataParameterName),
                    CancellationParameter = (cancellationParameterType, cancellationParameterName)
                });
            }
        }


        return interfaceToGenerate;
    }

    private static IEnumerable<AttributeSyntax> EnumerateAttributeSyntax(SemanticModel semanticModel, MemberDeclarationSyntax methodDeclarationSyntax, string fullAttributeName)
    {
        foreach (AttributeListSyntax attributeList in methodDeclarationSyntax.AttributeLists)
        {
            foreach (AttributeSyntax attributeSyntax in attributeList.Attributes)
            {
                if (semanticModel.GetSymbolInfo(attributeSyntax).Symbol is not IMethodSymbol attributeSymbol)
                {
                    // weird, we couldn't get the symbol, ignore it
                    continue;
                }

                INamedTypeSymbol attributeContainingTypeSymbol = attributeSymbol.ContainingType;
                string fullName = attributeContainingTypeSymbol.ToDisplayString();

                // Is the attribute the [EnumExtensions] attribute?
                if (fullName == fullAttributeName)
                {
                    yield return attributeSyntax;
                }
            }
        }
    }

    private static void Execute(SourceProductionContext ctx, InterfaceToGenerate source)
    {
        var clientName = $"{source.InterfaceName}Client";
        if (clientName.Length > 2 && clientName.StartsWith("I") && char.IsUpper(clientName[1]))
        {
            clientName = clientName.Substring(1);
        }

        var sourceBuilder = new StringBuilder();
        sourceBuilder.AppendLine("// <auto-generated />");
        sourceBuilder.AppendLine("#nullable enable");

        // Usings
        sourceBuilder.AppendLine("using System.Net.Http.Json;");

        sourceBuilder.AppendLine($"namespace {source.InterfaceNamespace}");
        sourceBuilder.AppendLine("{");

        sourceBuilder.AppendLine($"    public class {clientName} : {source.InterfaceName}");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        private readonly HttpClient client;");

        // Constructor
        sourceBuilder.AppendLine($"        public {clientName}(HttpClient client)");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            this.client = client;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();

        // Interface methods
        foreach (var method in source.Methods)
        {
            sourceBuilder.AppendLine($"        // Callout to route {method.Route}.");
            sourceBuilder.Append($"        public async {method.ReturnTypeWithTask} {method.Name}(");
            if (method.DataParmeter.dataParameterType is not null)
            {
                sourceBuilder.Append($"{method.DataParmeter.dataParameterType} {method.DataParmeter.dataParameterName}, ");
            }
            if (method.CancellationParameter.cancellationParameterType is not null)
            {
                sourceBuilder.Append($"{method.CancellationParameter.cancellationParameterType} {method.CancellationParameter.cancellationParameterName}");
            }
            sourceBuilder.AppendLine(")");
            sourceBuilder.AppendLine($"        {{");
            if (method.DataParmeter.dataParameterType is not null)
            {
                sourceBuilder.AppendLine($"             var response = await client.PostAsJsonAsync<{method.DataParmeter.dataParameterType}>($\"{method.Route}\", {method.DataParmeter.dataParameterName}, {method.CancellationParameter.cancellationParameterName});");
            }
            else
            {
                sourceBuilder.AppendLine($"             var response = await client.GetAsync($\"{method.Route}\", {method.CancellationParameter.cancellationParameterName});");
            }
            sourceBuilder.AppendLine("             if (response.IsSuccessStatusCode)");
            sourceBuilder.AppendLine("             {");
            sourceBuilder.AppendLine($"                 var content = await response.Content.ReadFromJsonAsync<{method.ReturnType}>();");
            sourceBuilder.AppendLine("                 return content!;");
            sourceBuilder.AppendLine("             }");
            sourceBuilder.AppendLine("             else if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {");
            sourceBuilder.AppendLine($"                throw new InvalidOperationException(\"The endpoint {method.Route} was not found.  Are service endpoints registered?\");");
            sourceBuilder.AppendLine("             }");
            sourceBuilder.AppendLine("             else");
            sourceBuilder.AppendLine("             {");
            sourceBuilder.AppendLine("                 throw new InvalidOperationException();");
            sourceBuilder.AppendLine("             }");
            sourceBuilder.AppendLine($"        }}");
        }
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine("}");

        ctx.AddSource($"{source.InterfaceNamespace.Replace('.', '_')}_{source.InterfaceName}.g.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));

        foreach (var diagnostic in source.Diagnostics)
        {
            ctx.ReportDiagnostic(diagnostic);
        }
    }
}
