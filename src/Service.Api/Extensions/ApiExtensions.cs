using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Service.Api.Enums;
using Service.Api.Exceptions;

namespace Service.Api.Extensions;

public static class ApiExtensions
{
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(
                new JsonStringEnumConverter<ItemStatus>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            options.JsonSerializerOptions.Converters.Add(
                new JsonStringEnumConverter<ActionType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddSwaggerGen(options =>
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Service.Api", Version = "v1" }));

        return services;
    }
}
