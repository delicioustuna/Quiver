using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;

namespace GraphDb.Engine.Client.Internal;

internal interface IOperatorBuilder
{
    int CurrentEntityColumn { get; }
    int PredictedOutputColumnCount { get; }
    IPhysicalOperator Build(ISchemaApi schema);
}
