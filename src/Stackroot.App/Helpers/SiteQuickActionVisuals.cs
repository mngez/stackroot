using System.IO;

using System.Windows.Media;

using System.Windows.Media.Imaging;

using Stackroot.Core.Abstractions;

using Stackroot.Core.Sites.Commands;



namespace Stackroot.App.Helpers;



public sealed record QuickActionVisual(

    string ShortLabel,

    string RuntimeBadge,

    string AccentColor,

    ImageSource? IconSource,

    bool HasIcon);



public static class SiteQuickActionVisuals

{

    private static readonly string ImagesDirectory =

        Path.Combine(AppContext.BaseDirectory, "Assets", "Images");



    public static QuickActionVisual Resolve(SiteQuickActionDefinition action)

    {

        var iconSource = ResolveIconSource(action);

        return new(

            ResolveShortLabel(action),

            ResolveRuntimeBadge(action.Runtime),

            ResolveAccentColor(action),

            iconSource,

            iconSource is not null);

    }



    public static string ResolveGroupTitle(string groupKey) =>

        SiteQuickActionPresets.GroupLabels.TryGetValue(groupKey, out var label)

            ? label

            : groupKey;



    private static ImageSource? ResolveIconSource(SiteQuickActionDefinition action)

    {

        var fileName = ResolveIconFile(action);

        if (fileName is null)

        {

            return null;

        }



        var path = Path.Combine(ImagesDirectory, fileName);

        if (!File.Exists(path))

        {

            return null;

        }



        var image = new BitmapImage();

        image.BeginInit();

        image.CacheOption = BitmapCacheOption.OnLoad;

        image.UriSource = new Uri(path, UriKind.Absolute);

        image.EndInit();

        image.Freeze();

        return image;

    }



    private static string? ResolveIconFile(SiteQuickActionDefinition action)

    {

        if (action.Runtime == SiteCommandRuntime.Npm)

        {

            return "npm.png";

        }



        if (action.Runtime == SiteCommandRuntime.Composer)

        {

            return null;

        }



        if (action.Runtime == SiteCommandRuntime.Php)

        {

            return UsesArtisan(action) ? "laravel.png" : "php.png";

        }



        return null;

    }



    private static bool UsesArtisan(SiteQuickActionDefinition action) =>

        action.Argv.Any(part => string.Equals(part, "artisan", StringComparison.OrdinalIgnoreCase));



    private static string ResolveShortLabel(SiteQuickActionDefinition action) =>

        action.Id.ToLowerInvariant() switch

        {

            "php-version" => "PHP version",

            "php-artisan-about" => "Artisan about",

            "migrate" => "Run migrations",

            "migrate-fresh" => "Fresh migrate",

            "optimize-clear" => "Optimize clear",

            "cache-clear" => "Cache clear",

            "config-clear" => "Config clear",

            "route-clear" => "Route clear",

            "view-clear" => "View clear",

            "composer-install" => "composer install",

            "composer-update" => "composer update",

            "npm-install" => "npm install",

            "npm-dev" => "npm run dev",

            "npm-build" => "npm run build",

            "storage-link" => "storage:link",

            "queue-restart" => "queue:restart",

            _ => action.Label

        };



    private static string ResolveRuntimeBadge(SiteCommandRuntime runtime) =>

        runtime switch

        {

            SiteCommandRuntime.Php => "PHP",

            SiteCommandRuntime.Composer => "Composer",

            SiteCommandRuntime.Npm => "npm",

            _ => "CLI"

        };



    private static string ResolveAccentColor(SiteQuickActionDefinition action)

    {

        if (action.Dangerous)

        {

            return "#E88A92";

        }



        return action.Runtime switch

        {

            SiteCommandRuntime.Composer => "#885630",

            SiteCommandRuntime.Npm => "#CB3837",

            SiteCommandRuntime.Php when UsesArtisan(action) => "#FF2D20",

            SiteCommandRuntime.Php => "#777BB4",

            _ => "#4CAE8C"

        };

    }

}


