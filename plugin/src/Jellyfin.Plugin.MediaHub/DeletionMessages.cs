using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Personal plain-text presentation only, never authority or executable markup.</summary>
public static class DeletionMessages
{
    public static void Validate(string? template)
    {
        if (template is null) return;
        var remainder = template.Replace("{title}", "").Replace("{delayDays}", "").Replace("{activityDays}", "");
        if (template.Length > 300 || template.Any(char.IsControl) || remainder.Contains('{') || remainder.Contains('}'))
            throw new HubError("Use at most 300 plain-text characters and only {title}, {delayDays}, {activityDays} placeholders.");
    }
    public static string Render(string? template, string title, int delayDays, int activityDays)
    {
        Validate(template);
        // A single substitution pass: braces in a media title never become another placeholder.
        return string.IsNullOrWhiteSpace(template) ? "Allow deletion?" : Regex.Replace(template, "\\{(title|delayDays|activityDays)\\}", match => match.Groups[1].Value switch
        { "title" => title, "delayDays" => delayDays.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => activityDays.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
}
