namespace UniversalSpellCheck;

internal sealed class AppSettings
{
    public bool StartOnLogin { get; set; }
    public string Model { get; set; } = OpenAiSpellcheckService.DefaultModel;
}
