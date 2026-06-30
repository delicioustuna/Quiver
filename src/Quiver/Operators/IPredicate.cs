using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>タプル行に対するフィルタ述語。</summary>
internal interface IPredicate
{
    bool Evaluate(in TupleRef tuple, ITransaction tx);
}
