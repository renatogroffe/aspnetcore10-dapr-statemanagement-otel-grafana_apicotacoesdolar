using APICotacoesDolar.Models;
using APICotacoesDolar.Tracing;
using Dapr.Client;
using Grafana.OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDaprClient();

var grafanaCloudZone = builder.Configuration["GrafanaCloud:Zone"];
var grafanaCloudInstanceId = builder.Configuration["GrafanaCloud:InstanceId"];
var grafanaCloudApiKey = builder.Configuration["GrafanaCloud:ApiKey"];
string? grafanaCloudAuthorization = null;
if (!string.IsNullOrWhiteSpace(grafanaCloudZone) &&
    !string.IsNullOrWhiteSpace(grafanaCloudInstanceId) && 
    !string.IsNullOrWhiteSpace(grafanaCloudApiKey))
{
    grafanaCloudAuthorization = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes($"{grafanaCloudInstanceId}:{grafanaCloudApiKey}"));
}
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(serviceName: OpenTelemetryExtensions.ServiceName,
        serviceVersion: OpenTelemetryExtensions.ServiceVersion);
builder.Services.AddOpenTelemetry()
    .WithTracing((traceBuilder) =>
    {
        traceBuilder
            .AddSource(OpenTelemetryExtensions.ServiceName)
            .SetResourceBuilder(resourceBuilder)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .UseGrafana(settings =>
            {
                if (!string.IsNullOrWhiteSpace(grafanaCloudAuthorization))
                {
                    settings.ExporterSettings = new OtlpExporter
                    {
                        Endpoint = new Uri($"https://otlp-gateway-prod-{grafanaCloudZone}.grafana.net/otlp"),
                        Headers = $"Authorization=Basic {grafanaCloudAuthorization}",
                        Protocol = OtlpExportProtocol.HttpProtobuf
                    };
                }
            });
    });
builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(resourceBuilder);
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    options.AttachLogsToActivityEvent();
    options.UseGrafana(settings =>
    {
        if (!string.IsNullOrWhiteSpace(grafanaCloudAuthorization))
        {
            settings.ExporterSettings = new OtlpExporter
            {
                Endpoint = new Uri($"https://otlp-gateway-prod-{grafanaCloudZone}.grafana.net/otlp"),
                Headers = $"Authorization=Basic {grafanaCloudAuthorization}",
                Protocol = OtlpExportProtocol.HttpProtobuf
            };
        }
    });
});

var app = builder.Build();

app.MapOpenApi();

app.UseHttpsRedirection();

// Middleware necessário na integração com Dapr
app.UseCloudEvents();

const decimal VALOR_BASE_DOLAR = 5.100m;

app.MapGet("/updatecotacaodolar", async (DaprClient daprClient) =>
{
    using var activity1 = OpenTelemetryExtensions.ActivitySource
        .StartActivity("AtualizarCotacaoDolar")!;
    app.Logger.LogInformation("Gerando nova cotacao para o dolar...");
    var dolar = VALOR_BASE_DOLAR + new Random().Next(0, 21) / 1000m;
    var dataUltimaAtualizacao = JsonSerializer.Serialize(DateTime.UtcNow.AddHours(-3));
    var storeName = app.Configuration["Dapr:StoreName"]!;
    await daprClient.SaveStateAsync(storeName, "ValorCotacaoDolar", dolar);
    await daprClient.SaveStateAsync(storeName, "DataUltimaAtualizacao", dataUltimaAtualizacao);
    activity1?.SetTag("StateStoreName", storeName);
    activity1?.SetTag("ValorCotacaoDolar", dolar);
    activity1?.SetTag("DataUltimaAtualizacao", dataUltimaAtualizacao);
    app.Logger.LogInformation($"Nova cotacao para o dolar gerada: {dolar} | Data da ultima atualizacao: {dataUltimaAtualizacao})");
    return Results.StatusCode(StatusCodes.Status202Accepted);
})
.Produces(StatusCodes.Status202Accepted);

app.MapGet("/cotacaodolar", async (DaprClient daprClient) =>
{
    using var activity1 = OpenTelemetryExtensions.ActivitySource
        .StartActivity("ConsultarCotacaoDolar")!;
    app.Logger.LogInformation("Consultando a ultima cotacao para o dolar...");
    var storeName = app.Configuration["Dapr:StoreName"]!;
    var dolar = await daprClient.GetStateAsync<decimal>(storeName, "ValorCotacaoDolar");
    var dataUltimaAtualizacao = await daprClient.GetStateAsync<string>(storeName, "DataUltimaAtualizacao");
    activity1?.SetTag("StateStoreName", storeName);
    activity1?.SetTag("ValorCotacaoDolar", dolar);
    activity1?.SetTag("DataUltimaAtualizacao", dataUltimaAtualizacao);
    app.Logger.LogInformation($"Ultima cotacao para o dolar consultada: {dolar} | Data da ultima atualizacao: {dataUltimaAtualizacao})");
    return Results.Ok(new Cotacao
    {
        Valor = dolar,
        DataUltimaAtualizacao = dataUltimaAtualizacao
    });
})
.Produces<Cotacao>(StatusCodes.Status200OK);

app.Run();