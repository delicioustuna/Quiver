namespace Quiver.Studio.Resources;

using System.Globalization;
using System.Resources;

internal static class Strings
{
    private static ResourceManager? resourceManager;

    private static ResourceManager ResourceManager =>
        resourceManager ??= new ResourceManager("Quiver.Studio.Resources.Strings", typeof(Strings).Assembly);

    public static CultureInfo? Culture { get; set; }

    public static string AppTitle => ResourceManager.GetString(nameof(AppTitle), Culture)!;
    public static string File => ResourceManager.GetString(nameof(File), Culture)!;
    public static string Open => ResourceManager.GetString(nameof(Open), Culture)!;
    public static string Save => ResourceManager.GetString(nameof(Save), Culture)!;
    public static string Exit => ResourceManager.GetString(nameof(Exit), Culture)!;
    public static string Query => ResourceManager.GetString(nameof(Query), Culture)!;
    public static string Execute => ResourceManager.GetString(nameof(Execute), Culture)!;
    public static string Clear => ResourceManager.GetString(nameof(Clear), Culture)!;
    public static string View => ResourceManager.GetString(nameof(View), Culture)!;
    public static string Results => ResourceManager.GetString(nameof(Results), Culture)!;
    public static string Schema => ResourceManager.GetString(nameof(Schema), Culture)!;
    public static string Properties => ResourceManager.GetString(nameof(Properties), Culture)!;
    public static string Search => ResourceManager.GetString(nameof(Search), Culture)!;
    public static string History => ResourceManager.GetString(nameof(History), Culture)!;
    public static string AddNode => ResourceManager.GetString(nameof(AddNode), Culture)!;
    public static string AddRelationship => ResourceManager.GetString(nameof(AddRelationship), Culture)!;
    public static string Delete => ResourceManager.GetString(nameof(Delete), Culture)!;
    public static string NodeLabel => ResourceManager.GetString(nameof(NodeLabel), Culture)!;
    public static string RelationshipType => ResourceManager.GetString(nameof(RelationshipType), Culture)!;
    public static string From => ResourceManager.GetString(nameof(From), Culture)!;
    public static string To => ResourceManager.GetString(nameof(To), Culture)!;
    public static string OK => ResourceManager.GetString(nameof(OK), Culture)!;
    public static string Cancel => ResourceManager.GetString(nameof(Cancel), Culture)!;
}
