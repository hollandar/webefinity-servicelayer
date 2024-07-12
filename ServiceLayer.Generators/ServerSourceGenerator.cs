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
public class ServerSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ClassToGenerate?> interfacesToGenerate = context.SyntaxProvider
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
        => node is ClassDeclarationSyntax m && m.AttributeLists.Count > 0;

    static ClassToGenerate? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
    {
        // we know the node is a EnumDeclarationSyntax thanks to IsSyntaxTargetForGeneration
        var classDeclarationSyntax = (ClassDeclarationSyntax)context.Node;

        var exposedServiceAttribute = EnumerateAttributeSyntax(context.SemanticModel, classDeclarationSyntax, "ServiceLayer.Abstractions.ExposedServiceAttribute").FirstOrDefault();
        if (exposedServiceAttribute is not null)
        {
            return GetClassToGenerate(context.SemanticModel, classDeclarationSyntax);
        }

        // we didn't find the attribute we were looking for
        return null;
    }

    private static ClassToGenerate GetClassToGenerate(SemanticModel semanticModel, ClassDeclarationSyntax classDeclarationSyntax)
    {

        var classSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);
        if (classSymbol is null)
        {
            throw new System.Exception("Could not get class symbol");
        }

        var interfaceName = string.Empty;
        var interfaceNamespace = string.Empty;
        var interfaceSymbols = classSymbol.AllInterfaces;
        foreach (var interfaceSymbol in interfaceSymbols)
        {
            var attributes = interfaceSymbol.GetAttributes();
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass?.ToDisplayString() == "ServiceLayer.Abstractions.ExposedServiceInterfaceAttribute")
                {
                    interfaceName = interfaceSymbol.Name;
                    interfaceNamespace = interfaceSymbol.ContainingNamespace.ToDisplayString();
                }
            }
        }

        var className = classDeclarationSyntax.Identifier.Text;

        var interfaceToGenerate = new ClassToGenerate
        {
            InterfaceName = interfaceName,
            InterfaceNamespace = interfaceNamespace, 
            ClassNamespace = classSymbol.ContainingNamespace.ToDisplayString(),
            ClassName = className,
        };

        if (interfaceToGenerate.InterfaceName == string.Empty)
        {
            interfaceToGenerate.Diagnostics.Add(
                Diagnostic.Create("IF000", "Interface Generation", $"Class must implement an interface with ExposedServiceInterfaceAttribute.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
            );
        }

        foreach (var members in classDeclarationSyntax.Members)
        {
            if (members is MethodDeclarationSyntax methodDeclarationSyntax)
            {
                var methodSymbolInfo = semanticModel.GetDeclaredSymbol(methodDeclarationSyntax);
                if (methodSymbolInfo is null)
                {
                    throw new Exception($"Could not get method symbol for {methodDeclarationSyntax}");
                }

                var methodName = methodSymbolInfo.Name;
                var routeInterfaceComponent = interfaceName;
                if (routeInterfaceComponent.Length > 2 && routeInterfaceComponent.StartsWith("I") && char.IsUpper(routeInterfaceComponent[1]))
                {
                    routeInterfaceComponent = routeInterfaceComponent.Substring(1);
                }
                var defaultRoute = $"/s/{routeInterfaceComponent}/{methodName}".ToLower();

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

                        continue;
                    }
                }

                if (methodSymbolInfo.ReturnType is null)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Return from an externally exposed interface method must not be null, it was.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
                    );

                    continue;
                }

                if (methodSymbolInfo.ReturnType.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks" || methodSymbolInfo.ReturnType.Name != "Task")
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Return from an externally exposed interface method must be a task, it was {methodSymbolInfo.ReturnType.ToDisplayString()}", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
                    );

                    continue;
                }

                var returnTypeWithTask = methodSymbolInfo.ReturnType.ToDisplayString();
                var taskRegex = new Regex("System.Threading.Tasks.Task<(?<returnType>.*)>");
                var taskRegexMatch = taskRegex.Match(returnTypeWithTask);
                if (!taskRegexMatch.Success)
                {
                    interfaceToGenerate.Diagnostics.Add(
                        Diagnostic.Create("IF000", "Interface Generation", $"Return from an externally exposed interface method must be a task, it was {methodSymbolInfo.ReturnType.ToDisplayString()}", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)
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
                            Diagnostic.Create("IF000", "Interface Generation", $"Data parameter must be a simple type, it was {dataParameter.Type?.ToFullString() ?? "Unknown data parameter type"}.", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0, location: dataParameter.GetLocation())
                        );

                        continue;
                    }

                    dataParameterType = dataParameterSymbol.ToDisplayString();
                    dataParameterName = dataParameterTokenNode.AsToken().ToFullString();
                }

                var cancellationParameter = parameterQueue.Dequeue();
                var cancellationParameterNameNode = cancellationParameter.ChildNodesAndTokens().First();
                var cancellationParameterTokenNode = cancellationParameter.ChildNodesAndTokens().Last();
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

    private static void Execute(SourceProductionContext ctx, ClassToGenerate source)
    {
        var sourceBuilder = new StringBuilder();
        sourceBuilder.AppendLine("// <auto-generated />");
        sourceBuilder.AppendLine("using Microsoft.AspNetCore.Mvc;");

        sourceBuilder.AppendLine($"namespace {source.ClassNamespace}");
        sourceBuilder.AppendLine("{");

        sourceBuilder.AppendLine($"    public static class {source.ClassName}_StartupExtensions");

        sourceBuilder.AppendLine( "    {");
        sourceBuilder.AppendLine($"        public static void Map{source.ClassName}Endpoints(this WebApplication app)");
        sourceBuilder.AppendLine( "        {");
        foreach (var method in source.Methods) {
            if (method.DataParmeter.dataParameterType is not null)
            {
                sourceBuilder.AppendLine($"            app.MapPost(\"{method.Route}\", async ({source.InterfaceNamespace}.{source.InterfaceName} service, [FromBody] {method.DataParmeter.dataParameterType} {method.DataParmeter.dataParameterName}, CancellationToken ct) => await service.{method.Name}({method.DataParmeter.dataParameterName}, ct));");
            }
            else
            {
                sourceBuilder.AppendLine($"            app.MapGet(\"{method.Route}\", async ({source.InterfaceNamespace}.{source.InterfaceName} service, CancellationToken ct) => await service.{method.Name}(ct));");
            }
        }
        sourceBuilder.AppendLine( "        }");
        sourceBuilder.AppendLine( "    }");
        sourceBuilder.AppendLine( "}");



    ctx.AddSource($"{source.ClassNamespace.Replace('.','_')}_{source.ClassName}.g.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));

        foreach (var diagnostic in source.Diagnostics)
        {
            ctx.ReportDiagnostic(diagnostic);
        }
    }
}
