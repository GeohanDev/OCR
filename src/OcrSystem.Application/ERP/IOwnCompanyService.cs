namespace OcrSystem.Application.ERP;

/// <summary>
/// Determines whether a name matches the configured own-company names list.
/// Used across validators to avoid flagging our own company name as a vendor,
/// invoice owner, or any other entity that should only appear externally.
/// </summary>
public interface IOwnCompanyService
{
    bool IsOwnCompanyName(string value);
}
