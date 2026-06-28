namespace Quiver.Studio.Resources;

using System.Globalization;
using System.Resources;

internal static class Strings
{
    private static ResourceManager? resourceManager;

    private static ResourceManager ResourceManager =>
        resourceManager ??= new ResourceManager("Quiver.Studio.Resources.Strings", typeof(Strings).Assembly);

    public static CultureInfo? Culture { get; set; }

    // App
    public static string AppTitle => ResourceManager.GetString(nameof(AppTitle), Culture)!;

    // Menu: File
    public static string Menu_File => ResourceManager.GetString(nameof(Menu_File), Culture)!;
    public static string Menu_OpenDatabase => ResourceManager.GetString(nameof(Menu_OpenDatabase), Culture)!;
    public static string Menu_CloseDatabase => ResourceManager.GetString(nameof(Menu_CloseDatabase), Culture)!;
    public static string Menu_RecentFiles => ResourceManager.GetString(nameof(Menu_RecentFiles), Culture)!;
    public static string Menu_Exit => ResourceManager.GetString(nameof(Menu_Exit), Culture)!;

    // Menu: View
    public static string Menu_View => ResourceManager.GetString(nameof(Menu_View), Culture)!;

    // Menu: Query
    public static string Menu_Query => ResourceManager.GetString(nameof(Menu_Query), Culture)!;
    public static string Menu_Execute => ResourceManager.GetString(nameof(Menu_Execute), Culture)!;
    public static string Menu_Clear => ResourceManager.GetString(nameof(Menu_Clear), Culture)!;

    // Panel titles
    public static string Panel_Schema => ResourceManager.GetString(nameof(Panel_Schema), Culture)!;
    public static string Panel_Statistics => ResourceManager.GetString(nameof(Panel_Statistics), Culture)!;
    public static string Panel_QueryEditor => ResourceManager.GetString(nameof(Panel_QueryEditor), Culture)!;
    public static string Panel_FullTextSearch => ResourceManager.GetString(nameof(Panel_FullTextSearch), Culture)!;
    public static string Panel_Settings => ResourceManager.GetString(nameof(Panel_Settings), Culture)!;
    public static string Panel_Properties => ResourceManager.GetString(nameof(Panel_Properties), Culture)!;
    public static string Panel_Results => ResourceManager.GetString(nameof(Panel_Results), Culture)!;
    public static string Panel_Graph => ResourceManager.GetString(nameof(Panel_Graph), Culture)!;
    public static string Panel_Output => ResourceManager.GetString(nameof(Panel_Output), Culture)!;
    public static string Panel_History => ResourceManager.GetString(nameof(Panel_History), Culture)!;

    // Connection
    public static string Connection => ResourceManager.GetString(nameof(Connection), Culture)!;
    public static string Disconnected => ResourceManager.GetString(nameof(Disconnected), Culture)!;
    public static string OpenDatabasePrompt => ResourceManager.GetString(nameof(OpenDatabasePrompt), Culture)!;
    public static string Close => ResourceManager.GetString(nameof(Close), Culture)!;

    // Schema
    public static string Schema => ResourceManager.GetString(nameof(Schema), Culture)!;

    // Stats
    public static string Stats_Nodes => ResourceManager.GetString(nameof(Stats_Nodes), Culture)!;
    public static string Stats_Relationships => ResourceManager.GetString(nameof(Stats_Relationships), Culture)!;
    public static string Stats_Properties => ResourceManager.GetString(nameof(Stats_Properties), Culture)!;
    public static string Stats_DataFile => ResourceManager.GetString(nameof(Stats_DataFile), Culture)!;
    public static string Stats_WAL => ResourceManager.GetString(nameof(Stats_WAL), Culture)!;

    // Query Editor
    public static string ExecuteF5 => ResourceManager.GetString(nameof(ExecuteF5), Culture)!;
    public static string QueryEditorPlaceholder => ResourceManager.GetString(nameof(QueryEditorPlaceholder), Culture)!;
    public static string GraphPlaceholder => ResourceManager.GetString(nameof(GraphPlaceholder), Culture)!;

    // Results
    public static string CopyCsv => ResourceManager.GetString(nameof(CopyCsv), Culture)!;
    public static string CopyJson => ResourceManager.GetString(nameof(CopyJson), Culture)!;

    // Status bar
    public static string Status_Zoom => ResourceManager.GetString(nameof(Status_Zoom), Culture)!;
    public static string Status_GraphInfo => ResourceManager.GetString(nameof(Status_GraphInfo), Culture)!;

    // Dialogs
    public static string AddNode => ResourceManager.GetString(nameof(AddNode), Culture)!;
    public static string AddRelationship => ResourceManager.GetString(nameof(AddRelationship), Culture)!;
    public static string Delete => ResourceManager.GetString(nameof(Delete), Culture)!;
    public static string NodeLabel => ResourceManager.GetString(nameof(NodeLabel), Culture)!;
    public static string RelationshipType => ResourceManager.GetString(nameof(RelationshipType), Culture)!;
    public static string OK => ResourceManager.GetString(nameof(OK), Culture)!;
    public static string Cancel => ResourceManager.GetString(nameof(Cancel), Culture)!;
    public static string Error => ResourceManager.GetString(nameof(Error), Culture)!;
    public static string ErrorOpenDatabase => ResourceManager.GetString(nameof(ErrorOpenDatabase), Culture)!;

    // Settings
    public static string Settings_ScoreVisualization => ResourceManager.GetString(nameof(Settings_ScoreVisualization), Culture)!;
    public static string Settings_ScoreVizDesc => ResourceManager.GetString(nameof(Settings_ScoreVizDesc), Culture)!;
    public static string Settings_DisplayMode => ResourceManager.GetString(nameof(Settings_DisplayMode), Culture)!;
    public static string Settings_ContourMap => ResourceManager.GetString(nameof(Settings_ContourMap), Culture)!;
    public static string Settings_ContourDesc => ResourceManager.GetString(nameof(Settings_ContourDesc), Culture)!;
    public static string Settings_EvaluationMode => ResourceManager.GetString(nameof(Settings_EvaluationMode), Culture)!;
    public static string Settings_ScoreRange => ResourceManager.GetString(nameof(Settings_ScoreRange), Culture)!;
    public static string Settings_Min => ResourceManager.GetString(nameof(Settings_Min), Culture)!;
    public static string Settings_Max => ResourceManager.GetString(nameof(Settings_Max), Culture)!;
    public static string Settings_ColorPalette => ResourceManager.GetString(nameof(Settings_ColorPalette), Culture)!;
    public static string Settings_DiscreteSteps => ResourceManager.GetString(nameof(Settings_DiscreteSteps), Culture)!;
    public static string Settings_CustomPalette => ResourceManager.GetString(nameof(Settings_CustomPalette), Culture)!;
    public static string Settings_Preview => ResourceManager.GetString(nameof(Settings_Preview), Culture)!;
    public static string Settings_NodeRelationship => ResourceManager.GetString(nameof(Settings_NodeRelationship), Culture)!;
    public static string Settings_NodeRelDesc => ResourceManager.GetString(nameof(Settings_NodeRelDesc), Culture)!;
    public static string Settings_NodeShape => ResourceManager.GetString(nameof(Settings_NodeShape), Culture)!;
    public static string Settings_RelationshipStyle => ResourceManager.GetString(nameof(Settings_RelationshipStyle), Culture)!;
    public static string Settings_Language => ResourceManager.GetString(nameof(Settings_Language), Culture)!;
    public static string Settings_LanguageDesc => ResourceManager.GetString(nameof(Settings_LanguageDesc), Culture)!;
    public static string Settings_LanguageAuto => ResourceManager.GetString(nameof(Settings_LanguageAuto), Culture)!;
    public static string Settings_RestartRequired => ResourceManager.GetString(nameof(Settings_RestartRequired), Culture)!;

    // Layout
    public static string Layout_Force => ResourceManager.GetString(nameof(Layout_Force), Culture)!;
    public static string Layout_Hierarchy => ResourceManager.GetString(nameof(Layout_Hierarchy), Culture)!;
    public static string Export_SVG => ResourceManager.GetString(nameof(Export_SVG), Culture)!;
    public static string Export_PNG => ResourceManager.GetString(nameof(Export_PNG), Culture)!;

    // Theme
    public static string Theme_Light => ResourceManager.GetString(nameof(Theme_Light), Culture)!;
    public static string Theme_Dark => ResourceManager.GetString(nameof(Theme_Dark), Culture)!;
}
