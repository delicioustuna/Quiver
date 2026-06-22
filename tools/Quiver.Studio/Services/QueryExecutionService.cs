using Microsoft.Extensions.Logging;

namespace Quiver.Studio.Services;

public sealed class QueryExecutionService
{
    private readonly DatabaseService _databaseService;
    private readonly ILogger<QueryExecutionService> _logger;

    public QueryExecutionService(DatabaseService databaseService, ILogger<QueryExecutionService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }
}
