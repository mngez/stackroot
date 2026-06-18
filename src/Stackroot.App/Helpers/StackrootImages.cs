using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Stackroot.App.Helpers;

public static class StackrootImages
{
    private const int FolderIconDisplaySize = 18;

    private static readonly Lazy<ImageSource?> FolderIconLazy = new(LoadFolderIcon);

    public static ImageSource? FolderIcon => FolderIconLazy.Value;

    private static ImageSource? LoadFolderIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "filebrowser-quantum.png");
        if (!File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.DecodePixelWidth = FolderIconDisplaySize * 2;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
