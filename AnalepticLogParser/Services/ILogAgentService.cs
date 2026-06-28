using AnalepticLogParser.Models;

namespace AnalepticLogParser.Services;

public interface ILogAgentService
{
    Task<TriageReport> ExecuteTriageAsync(string rawLog, string providedKey, string accessCode);
}
