using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>タプル行に対するフィルタ述語。</summary>
internal interface IPredicate
{
    bool Evaluate(in TupleRef tuple, ITransaction tx);
}
