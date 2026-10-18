namespace Jellyfin.Plugin.WatchlistDeletion.Api;

public static class MediaPresentation
{
    public static string? Poster(string? path)=>path is not null&&System.Text.RegularExpressions.Regex.IsMatch(path,"\\A/[A-Za-z0-9_-]{1,100}\\.(jpg|png|webp)\\z")?"https://image.tmdb.org/t/p/w185"+path:null;
    public static string Filename(string name){name=name.Replace('\\','/').Split('/').Last();name=new string(name.Where(c=>!char.IsControl(c)&&c is not ('"' or ':' or '<' or '>' or '|')).Take(120).ToArray()).Trim();return string.IsNullOrEmpty(name)||name is "." or ".."?"file":name;}
    public static bool DiscordAsset(string url)
    {
        if(url is null||url.Length>4096||url.Any(c=>char.IsControl(c)||c=='\\')||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net"))return false;
        var path=Uri.UnescapeDataString(uri.AbsolutePath);return System.Text.RegularExpressions.Regex.IsMatch(path,"\\A/(attachments|avatars|embed/avatars|emojis)/[A-Za-z0-9_./-]+\\z")&&!path.Contains("..");
    }
}
