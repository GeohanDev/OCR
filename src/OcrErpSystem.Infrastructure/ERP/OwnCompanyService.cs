using Microsoft.Extensions.Configuration;
using OcrErpSystem.Application.ERP;

namespace OcrErpSystem.Infrastructure.ERP;

public class OwnCompanyService : IOwnCompanyService
{
    private readonly IReadOnlyList<string> _ownNames;

    public OwnCompanyService(IConfiguration config)
    {
        var names = config.GetSection("OwnCompany:Names").Get<string[]>()
            ?? config["OwnCompany:NamesFlat"]?.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        _ownNames = names.Select(Normalize).ToList();
    }

    public bool IsOwnCompanyName(string value) =>
        _ownNames.Any(n => string.Equals(n, Normalize(value), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string s) =>
        string.Join(" ", s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
