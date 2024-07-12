# Webefinity Service Layer

## INTRODUCTION

Service layer is a code generation layer for use with Blazor.  It makes a service defined by an interface transparently available during server side rendering, at the client in Blazor Server, and at the client in WASM.

Server side endoints are generated according to an interface description, and a client is similarly generated that transports the request from client to server transparently.

Interface methods must current follow these rules:
* The service method must return Task.
* The service method can optionally return a model, which must be serializable using System.Text.Json, as in Task\<SomeModel\>.
* The service method can optionally take one model parameter, also serializable, like SomeModel model.
* The service method must take as its second (or first without a model) a CancellationToken ct.

Some possible service method definitions:
public Task DoSomethingAsync(CancellationToken ct);
public Task\<SomeModel\> ReturnSomethingAsync(CancellationToken ct);
public Task\<SomeModel\> ChangeSomethingAsync(SomeOtherModel model, CancellationToken ct);


## CODE GENERATORS

Include the following references to both your Blazor and Blazor.Client apps.

```
<ProjectReference Include="ServiceLayer.Abstractions.csproj" />
<ProjectReference Include="ServiceLayer.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false"  />
```

## INTERFACE EXAMPLE

Define a service interface, in the client project or another that is shared between the client and the server.

``` c#
using ServiceLayer.Abstractions;

namespace HelloExample;

public class NameModel {
    public string Name { get; set; }
}

[ExposedServiceInterface]
public interface IHelloService {
    Task<NameModel> SayHelloAsync(NameModel name, CancellationToken ct);
}
```

## SERVICE EXAMPLE

Implement the service on the server side.  This must implement the interface and be attributed as an [ExposedService].

The exposed service attribute generates code for the endpoints that all into the service.

``` c#
using ServiceLayer.Abstractions;

namespace HelloExample;

[ExposedService]
public class HelloService : IHelloService {
    public Task<NameModel> SayHelloAsync(NameModel name, CancellationToken ct) {
        return Task.FromResult(new HelloModel() { Name = $"Hello, {name.Name}" });
    }
}
```

## CLIENT Program.cs

Inject the generated client into your client application.  Calls to this client are tunnelled back to the blazor server.

``` c#
builder.Services.AddScoped<IHelloService, HelloServiceClient>();
```

## BLAZOR Program.cs

On the blazor server side project, inject the actual service for use by Blazor Server and SSR.

``` c#
builder.Services.AddScoped<IHelloService, HelloService>();
```

And exposed the client required endpoints.

``` c#
app.MapHelloServiceEndpoints();
```

## RAZOR PAGE

Use the service as usual from the client page, this code will work both on the client and on the server.

``` c#

@inject IHelloService HelloService

@if (name is not null) {
    <p>@name.Name</p>
}

@code {
    NameModel name = null;
    
    protected override async Task OnInitialized() {
        
        name = await HelloService.SayHelloAsync(new NameModel() { Name = "Me"}, CancellationToken.None);
        
    }
}

```