using System.ComponentModel.DataAnnotations;

namespace Service.Api.Infrastructure.Cache;

public sealed class CacheOptions
{
    [Range(1, 86400)]
    public int DefaultTtlSeconds { get; set; } = 300;
    [Required]
    public string KeyPrefix { get; set; } = "service";
}
