using OcrSystem.Application.Auth;

namespace OcrSystem.Infrastructure.Auth;

public class AcumaticaTokenContext : IAcumaticaTokenContext
{
    public string? ForwardedToken { get; set; }
}
