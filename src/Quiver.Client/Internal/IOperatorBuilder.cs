using Quiver.Core;
using Quiver.Operators;

namespace Quiver.Client.Internal;

internal interface IOperatorBuilder
{
    int CurrentEntityColumn { get; }
    int PredictedOutputColumnCount { get; }
    IPhysicalOperator Build(ISchemaApi schema);
}
