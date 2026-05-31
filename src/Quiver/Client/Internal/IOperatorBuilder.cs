using Quiver.Core;
using Quiver.Query.Physical;

namespace Quiver.Api.Internal;

internal interface IOperatorBuilder
{
    int CurrentEntityColumn { get; }
    int PredictedOutputColumnCount { get; }
    IPhysicalOperator Build(ISchemaApi schema);
}
