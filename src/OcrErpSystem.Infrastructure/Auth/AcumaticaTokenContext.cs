using OcrErpSystem.Application.Auth;

namespace OcrErpSystem.Infrastructure.Auth;

public class AcumaticaTokenContext : IAcumaticaTokenContext
{
    public string? ForwardedToken { get; set; }
}
