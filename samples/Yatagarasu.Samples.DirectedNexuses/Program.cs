using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

using var db = YatagarasuDatabase.CreateInMemory();
VertexId hydrogen, oxygen, water, hydrogenPeroxide;
using (var write = db.BeginWriteTransaction())
{
    hydrogen = write.CreateVertex("Substance");
    oxygen = write.CreateVertex("Substance");
    water = write.CreateVertex("Substance");
    hydrogenPeroxide = write.CreateVertex("Substance");

    NexusId makeWater = write.CreateNexus("Reaction", [
        new("reactant", hydrogen), new("reactant", oxygen), new("product", water)]);
    write.SetProperty(makeWater, "cost", PropertyValue.FromDouble(2));

    NexusId makePeroxide = write.CreateNexus("Reaction", [
        new("reactant", water), new("reactant", oxygen), new("product", hydrogenPeroxide)]);
    write.SetProperty(makePeroxide, "cost", PropertyValue.FromDouble(3));
    write.Commit();
}

using var read = db.BeginReadTransaction();
var reachable = read.FindReachableVertices(
    [hydrogen, oxygen], "Reaction", "reactant", "product");
Console.WriteLine($"合成可能な物質数: {reachable.Vertices.Count}");

var derivation = read.FindShortestDerivation(
    [hydrogen, oxygen], hydrogenPeroxide,
    "Reaction", "reactant", "product",
    static (tx, nexus) => tx.GetProperty(nexus, "cost").DoubleValue,
    DerivationCostMode.Additive);
Console.WriteLine($"最小反応コスト: {derivation.Cost}");
Console.WriteLine($"導出木のノード数: {derivation.Tree.Count}");
