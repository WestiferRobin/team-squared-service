using Service.Api.Exceptions.Item;
using Service.Api.Exceptions.Action;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Exceptions;

namespace Service.Api.IntegrationTests.Fixtures;

public sealed class ErrorApiFactory(string environment) : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(environment);
        builder.ConfigureServices(services => services.AddControllers()
            .AddApplicationPart(typeof(ErrorTestController).Assembly));
    }
}

// This controller is compiled only into the test assembly and explicitly registered above.
[ApiController]
[Route("__tests/errors")]
public sealed class ErrorTestController : ControllerBase
{
    [HttpGet("{kind}")]
    public IActionResult Get(string kind) => throw (kind switch
    {
        "not-found" => new NotFoundException("The requested resource was not found."),
        "item-not-found" => new ItemNotFoundException(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "action-not-found" => new ActionNotFoundException(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        "validation" => new RequestValidationException("The request is invalid."),
        _ => new InvalidOperationException("INTERNAL_SECRET_SENTINEL")
    });
}
